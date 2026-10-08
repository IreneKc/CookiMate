from __future__ import annotations

import logging
from dataclasses import dataclass, field
from datetime import datetime, timezone

import hdbscan
import numpy as np
from scipy.sparse import csr_matrix
from sklearn.decomposition import TruncatedSVD
from sklearn.feature_extraction.text import TfidfVectorizer
from sqlalchemy import bindparam, text
from sqlalchemy.orm import Session
from umap import UMAP

from text_utils import build_clustering_text, build_recipe_text

logger = logging.getLogger(__name__)

MIN_RECIPES_FOR_CLUSTERING = 20
MIN_CLUSTER_SIZE_OVERRIDE: int | None = None
MIN_SAMPLES_OVERRIDE: int | None = 1


# ---------------------------------------------------------------------------
# ModelCache
# ---------------------------------------------------------------------------

@dataclass
class ModelCache:
    vectorizer: TfidfVectorizer
    tfidf_matrix: csr_matrix
    cluster_labels: np.ndarray
    recipe_ids: list[int] 

    # --- With defaults --------------------------------------------------------
    recipe_id_to_idx: dict[int, int] = field(default_factory=dict)

    cluster_vectorizer: TfidfVectorizer | None = None

    # Fitted reducers/clusterer
    svd_model: TruncatedSVD | None = None
    umap_model: UMAP | None = None
    hdbscan_model: hdbscan.HDBSCAN | None = None

    # Clustering space
    umap_embeddings: np.ndarray | None = None

    # label -> centroid in UMAP space
    cluster_centroids: dict[int, np.ndarray] = field(default_factory=dict)

    # Metadata
    built_at: datetime = field(default_factory=lambda: datetime.now(timezone.utc))
    n_recipes: int = 0
    n_clusters: int = 0


# ---------------------------------------------------------------------------
# Dynamic parameter scaling (kept conservative for small corpora)
# ---------------------------------------------------------------------------

def _svd_components(n: int, n_features: int) -> int:
    return max(2, min(100, n - 1, n_features - 1))


def _umap_components(n: int) -> int:
    return max(2, min(10, n // 20))


def _umap_neighbors(n: int) -> int:
    return max(2, min(15, n - 1))


def _min_cluster_size(n: int) -> int:
    if MIN_CLUSTER_SIZE_OVERRIDE is not None:
        return max(2, MIN_CLUSTER_SIZE_OVERRIDE)
    return max(5, n // 40)


# ---------------------------------------------------------------------------
# Degenerate fallback cache (single cluster)
# ---------------------------------------------------------------------------

def _single_cluster_cache(
    vectorizer: TfidfVectorizer,
    tfidf_matrix: csr_matrix,
    recipe_ids: list[int],
    recipe_id_to_idx: dict[int, int],
    n: int,
    *,
    cluster_vectorizer: TfidfVectorizer | None = None,
    svd_model: TruncatedSVD | None = None,
    umap_model: UMAP | None = None,
    hdbscan_model: hdbscan.HDBSCAN | None = None,
    umap_embeddings: np.ndarray | None = None,
) -> ModelCache:
    cluster_labels = np.zeros(n, dtype=int)
    centroids: dict[int, np.ndarray] = {}
    if umap_embeddings is not None and len(umap_embeddings):
        centroids = {0: umap_embeddings.mean(axis=0)}

    return ModelCache(
        vectorizer=vectorizer,
        tfidf_matrix=tfidf_matrix,
        cluster_labels=cluster_labels,
        recipe_ids=recipe_ids,
        recipe_id_to_idx=recipe_id_to_idx,
        cluster_vectorizer=cluster_vectorizer,
        svd_model=svd_model,
        umap_model=umap_model,
        hdbscan_model=hdbscan_model,
        umap_embeddings=umap_embeddings,
        cluster_centroids=centroids,
        built_at=datetime.now(timezone.utc),
        n_recipes=n,
        n_clusters=1,
    )


# ---------------------------------------------------------------------------
# Core build
# ---------------------------------------------------------------------------

def build_model_cache(db: Session) -> ModelCache:
    logger.info("ModelCache build started")

    # 1. Load approved recipes
    recipe_rows = db.execute(
        text(
            """
            SELECT recipe_id, title, description, instructions
            FROM recipes
            WHERE status = 'approved'
            ORDER BY recipe_id
            """
        )
    ).fetchall()

    if not recipe_rows:
        raise RuntimeError("No approved recipes found — cannot build ModelCache.")

    recipe_ids = [r[0] for r in recipe_rows]
    n = len(recipe_ids)
    recipe_id_to_idx = {rid: i for i, rid in enumerate(recipe_ids)}
    logger.info("Loaded %d approved recipes", n)

    # 2. Ingredients per recipe
    ing_stmt = text(
        """
        SELECT ri.recipe_id, i.ingredient_name
        FROM recipe_ingredients ri
        JOIN ingredients i ON i.ingredient_id = ri.ingredient_id
        WHERE ri.recipe_id IN :ids
        ORDER BY ri.recipe_id, i.ingredient_name
        """
    ).bindparams(bindparam("ids", expanding=True))

    ingredients_map: dict[int, list[str]] = {rid: [] for rid in recipe_ids}
    for rid, ing in db.execute(ing_stmt, {"ids": recipe_ids}).fetchall():
        ingredients_map[rid].append(ing)

    # 3. Build TWO document corpora
    ranking_docs = [
        build_recipe_text(r[1], r[2], ingredients_map[r[0]], r[3])
        for r in recipe_rows
    ]
    clustering_docs = [
        build_clustering_text(r[1], r[2], ingredients_map[r[0]], r[3])
        for r in recipe_rows
    ]

    # 4. RANKING TF-IDF
    logger.info("Fitting ranking TF-IDF …")
    vectorizer = TfidfVectorizer(
        lowercase=True,
        stop_words="english",
        ngram_range=(1, 2),
        max_features=50_000,
    )
    tfidf_matrix = vectorizer.fit_transform(ranking_docs)  # sparse (n, vocab), L2-norm
    logger.info("Ranking TF-IDF fitted — %d documents, %d features",
                n, tfidf_matrix.shape[1])

    # Degenerate corpus: too few recipes for meaningful density clustering.
    if n < MIN_RECIPES_FOR_CLUSTERING:
        logger.warning(
            "Only %d approved recipes (< %d) — skipping SVD/UMAP/HDBSCAN; "
            "using a single cluster.",
            n,
            MIN_RECIPES_FOR_CLUSTERING,
        )
        return _single_cluster_cache(
            vectorizer, tfidf_matrix, recipe_ids, recipe_id_to_idx, n
        )

    # 4b. CLUSTERING TF-IDF
    logger.info("Fitting clustering TF-IDF …")
    cluster_vectorizer = TfidfVectorizer(
        lowercase=True,
        stop_words="english",
        ngram_range=(1, 2),
        max_features=50_000,
    )
    cluster_tfidf = cluster_vectorizer.fit_transform(clustering_docs)
    cluster_features = cluster_tfidf.shape[1]
    logger.info("Clustering TF-IDF fitted — %d documents, %d features",
                n, cluster_features)

    # 5. TruncatedSVD
    n_svd = _svd_components(n, cluster_features)
    logger.info("Fitting TruncatedSVD (n_components=%d) …", n_svd)
    svd_model = TruncatedSVD(n_components=n_svd, random_state=42)
    svd_matrix = svd_model.fit_transform(cluster_tfidf)

    # 6. UMAP
    n_umap = _umap_components(n)
    logger.info("Fitting UMAP (n_components=%d) …", n_umap)
    umap_model = UMAP(
        n_components=n_umap,
        n_neighbors=_umap_neighbors(n),
        min_dist=0.0,
        metric="cosine",
        random_state=42,
    )
    umap_embeddings = np.asarray(umap_model.fit_transform(svd_matrix))

    # 7. HDBSCAN — density clustering on the UMAP embedding
    mcs = _min_cluster_size(n)
    ms = MIN_SAMPLES_OVERRIDE if MIN_SAMPLES_OVERRIDE is not None else mcs
    logger.info("Fitting HDBSCAN (min_cluster_size=%d, min_samples=%d) …", mcs, ms)
    clusterer = hdbscan.HDBSCAN(
        min_cluster_size=mcs,
        min_samples=ms,
        metric="euclidean",
        prediction_data=True,
        gen_min_span_tree=True, 
    )
    raw_labels = clusterer.fit_predict(umap_embeddings)  # (n,)

    n_noise = int((raw_labels == -1).sum())
    n_clusters = int(raw_labels.max()) + 1 if raw_labels.max() >= 0 else 0
    logger.info(
        "HDBSCAN: %d clusters, %d noise points (%.1f%%)",
        n_clusters,
        n_noise,
        100.0 * n_noise / n,
    )

    if n_clusters >= 2:
        logger.info("HDBSCAN DBCV (relative_validity) = %.4f",
                    clusterer.relative_validity_)

    # 7b. No stable cluster -> degrade gracefully to a single cluster
    if n_clusters == 0:
        logger.warning("HDBSCAN found no stable clusters — falling back to single cluster.")
        return _single_cluster_cache(
            vectorizer,
            tfidf_matrix,
            recipe_ids,
            recipe_id_to_idx,
            n,
            cluster_vectorizer=cluster_vectorizer,
            svd_model=svd_model,
            umap_model=umap_model,
            hdbscan_model=clusterer,
            umap_embeddings=umap_embeddings,
        )

    # 8. Per-cluster centroids in UMAP space
    cluster_centroids: dict[int, np.ndarray] = {}
    for label in sorted(set(raw_labels) - {-1}):
        cluster_centroids[label] = umap_embeddings[raw_labels == label].mean(axis=0)

    # 9. Resolve noise points to their nearest centroid
    cluster_labels = raw_labels.copy()
    if n_noise > 0:
        centroid_keys = list(cluster_centroids.keys())
        centroid_matrix = np.stack(list(cluster_centroids.values()))  # (k, d)
        noise_idx = np.where(cluster_labels == -1)[0]

        # (m, 1, d) - (1, k, d) -> (m, k, d); L2 norm over d -> (m, k)
        dists = np.linalg.norm(
            umap_embeddings[noise_idx][:, None, :] - centroid_matrix[None, :, :],
            axis=2,
        )
        nearest = dists.argmin(axis=1)
        for i, idx in enumerate(noise_idx):
            cluster_labels[idx] = centroid_keys[nearest[i]]
        logger.info("Resolved %d noise points to nearest centroid", n_noise)

    # 10. Assemble
    cache = ModelCache(
        vectorizer=vectorizer,
        tfidf_matrix=tfidf_matrix,
        cluster_labels=cluster_labels,
        recipe_ids=recipe_ids,
        recipe_id_to_idx=recipe_id_to_idx,
        cluster_vectorizer=cluster_vectorizer,
        svd_model=svd_model,
        umap_model=umap_model,
        hdbscan_model=clusterer,
        umap_embeddings=umap_embeddings,
        cluster_centroids=cluster_centroids,
        built_at=datetime.now(timezone.utc),
        n_recipes=n,
        n_clusters=n_clusters,
    )
    logger.info(
        "ModelCache ready — %d recipes, %d clusters, built %s",
        n,
        n_clusters,
        cache.built_at.isoformat(),
    )
    return cache