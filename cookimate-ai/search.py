from __future__ import annotations

import hdbscan
import numpy as np
from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel
from sklearn.metrics.pairwise import cosine_similarity
from sqlalchemy import bindparam, text
from sqlalchemy.orm import Session

from db import get_db
from model_cache import ModelCache
from scoring_utils import (
    batch_favorite_counts,
    batch_ingredients,
    batch_rating_scores,
    batch_recipe_meta,
    ingredient_overlap_score,
)
from text_utils import preprocess, tokenize
from user_preferences import excluded_by_allergens, excluded_by_dislikes, load_user_preferences

router = APIRouter()

W_TFIDF = 0.50
W_OVERLAP = 0.25
W_RATING = 0.15
W_POPULARITY = 0.10

MIN_TFIDF = 0.10

DEFAULT_WIDEN_THRESHOLD = 7

MAX_RESULTS = 50


class SearchRequest(BaseModel):
    query: str
    top_k: int = DEFAULT_WIDEN_THRESHOLD
    diet_tag: str | None = None
    ingredient_filters: list[str] | None = None
    cuisine: str | None = None
    meal_tags: list[str] | None = None
    user_id: int | None = None


# ---------------------------------------------------------------------------
# Filter resolution -> allowed recipe id set (None = no filtering)
# ---------------------------------------------------------------------------

def _apply_filters(db: Session, payload: SearchRequest) -> set[int] | None:
    allowed: set[int] | None = None

    if payload.diet_tag:
        rows = db.execute(
            text(
                """
                SELECT rt.recipe_id FROM recipe_tags rt
                JOIN tags t ON t.tag_id = rt.tag_id
                WHERE t.tag_type = 'diet' AND LOWER(t.tag_name) = LOWER(:tag)
                """
            ),
            {"tag": payload.diet_tag},
        ).fetchall()
        ids = {r[0] for r in rows}
        allowed = ids if allowed is None else (allowed & ids)

    if payload.cuisine:
        rows = db.execute(
            text(
                """
                SELECT rt.recipe_id FROM recipe_tags rt
                JOIN tags t ON t.tag_id = rt.tag_id
                WHERE t.tag_type = 'cuisine' AND LOWER(t.tag_name) = LOWER(:cuisine)
                """
            ),
            {"cuisine": payload.cuisine},
        ).fetchall()
        ids = {r[0] for r in rows}
        allowed = ids if allowed is None else (allowed & ids)

    if payload.meal_tags:
        meals = [m.strip().lower() for m in payload.meal_tags if m.strip()]
        if meals:
            stmt = text(
                """
                SELECT DISTINCT rt.recipe_id FROM recipe_tags rt
                JOIN tags t ON t.tag_id = rt.tag_id
                WHERE t.tag_type = 'meal' AND LOWER(t.tag_name) IN :meals
                """
            ).bindparams(bindparam("meals", expanding=True))
            rows = db.execute(stmt, {"meals": meals}).fetchall()
            ids = {r[0] for r in rows}
            allowed = ids if allowed is None else (allowed & ids)

    if payload.ingredient_filters:
        filters = [i.strip().lower() for i in payload.ingredient_filters if i.strip()]
        if filters:
            placeholders = {f"ing{i}": v for i, v in enumerate(filters)}
            conditions = " OR ".join(
                f"LOWER(i.ingredient_name) = :{k}" for k in placeholders
            )
            rows = db.execute(
                text(
                    f"""
                    SELECT ri.recipe_id FROM recipe_ingredients ri
                    JOIN ingredients i ON i.ingredient_id = ri.ingredient_id
                    WHERE {conditions}
                    GROUP BY ri.recipe_id
                    """
                ),
                placeholders,
            ).fetchall()
            ids = {r[0] for r in rows}
            allowed = ids if allowed is None else (allowed & ids)

    return allowed


# ---------------------------------------------------------------------------
# Cluster assignment for a query (None -> single-cluster / whole-corpus mode)
# ---------------------------------------------------------------------------

def _assign_cluster(cache: ModelCache, combined_q: str) -> int | None:
    if (
        cache.n_clusters <= 1
        or cache.cluster_vectorizer is None
        or cache.svd_model is None
        or cache.umap_model is None
        or cache.hdbscan_model is None
    ):
        return None

    q_cluster_tfidf = cache.cluster_vectorizer.transform([combined_q])
    q_svd = cache.svd_model.transform(q_cluster_tfidf)
    q_umap = np.asarray(cache.umap_model.transform(q_svd))

    labels, _ = hdbscan.approximate_predict(cache.hdbscan_model, q_umap)
    label = int(labels[0])

    if label == -1 and cache.cluster_centroids:
        keys = list(cache.cluster_centroids.keys())
        centroids = np.stack(list(cache.cluster_centroids.values()))
        dists = np.linalg.norm(q_umap[0] - centroids, axis=1)
        label = int(keys[int(dists.argmin())])

    return label


# ---------------------------------------------------------------------------
# Candidate index selection
# ---------------------------------------------------------------------------

def _cluster_indices(
    cache: ModelCache,
    target_cluster: int | None,
    allowed: set[int] | None,
) -> list[int]:

    def in_allowed(i: int) -> bool:
        return allowed is None or cache.recipe_ids[i] in allowed

    if target_cluster is None:
        # Single-cluster / whole-corpus mode: everything allowed.
        return [i for i in range(len(cache.recipe_ids)) if in_allowed(i)]

    return [
        i
        for i, lbl in enumerate(cache.cluster_labels)
        if int(lbl) == target_cluster and in_allowed(i)
    ]


def _corpus_indices(cache: ModelCache, allowed: set[int] | None) -> list[int]:

    def in_allowed(i: int) -> bool:
        return allowed is None or cache.recipe_ids[i] in allowed

    return [i for i in range(len(cache.recipe_ids)) if in_allowed(i)]


def _rank_filter_only_candidates(db: Session, cand_rids: list[int]) -> list[int]:

    if not cand_rids:
        return []

    rating_map = batch_rating_scores(db, cand_rids)
    fav_map = batch_favorite_counts(db, cand_rids)
    max_fav = max(fav_map.values(), default=0) or 1

    w_total = W_RATING + W_POPULARITY
    rating_w = W_RATING / w_total  # 0.60
    pop_w = W_POPULARITY / w_total  # 0.40

    def prelim_score(rid: int) -> float:
        rating = rating_map.get(rid, 0.0)
        pop = fav_map.get(rid, 0) / max_fav
        return rating_w * rating + pop_w * pop

    ranked = sorted(cand_rids, key=prelim_score, reverse=True)
    return ranked[:MAX_RESULTS]


def _score_indices(cache: ModelCache, q_tfidf, cand_idx: list[int]) -> dict[int, float]:
    if not cand_idx:
        return {}
    cand_matrix = cache.tfidf_matrix[cand_idx]
    sims = cosine_similarity(q_tfidf, cand_matrix).flatten()
    passing: dict[int, float] = {}
    for local_i, global_i in enumerate(cand_idx):
        score = float(sims[local_i])
        if score >= MIN_TFIDF:
            passing[cache.recipe_ids[global_i]] = score
    return passing


# ---------------------------------------------------------------------------
# Endpoint
# ---------------------------------------------------------------------------

@router.post("/search")
def search_recipes(
    payload: SearchRequest,
    request: Request,
    db: Session = Depends(get_db),
):
    cache: ModelCache | None = getattr(request.app.state, "model_cache", None)
    if cache is None:
        raise HTTPException(status_code=503, detail="ML model not ready. Try again shortly.")

    q = preprocess(payload.query or "")
    ing_q = " ".join(
        preprocess(i) for i in (payload.ingredient_filters or []) if i.strip()
    )
    combined_q = " ".join(filter(None, [q, ing_q])).strip()

    has_filters = bool(
        payload.diet_tag 
        or payload.ingredient_filters 
        or payload.cuisine
        or payload.meal_tags
    )
    if not combined_q and not has_filters:
        return _empty_response(payload)

    query_tokens = tokenize(combined_q)

    allowed = _apply_filters(db, payload)

    excluded_ids: set[int] = set()
    if payload.user_id:
        prefs = load_user_preferences(db, payload.user_id)
        excluded_ids = excluded_by_allergens(db, prefs["allergies"]) | excluded_by_dislikes(
            db, prefs["dislikes"]
        )
    if excluded_ids:
        allowed = (set(cache.recipe_ids) if allowed is None else allowed) - excluded_ids

    if allowed is not None and not allowed:
        return _empty_response(payload, excluded_count=len(excluded_ids))

    if not combined_q:
        target_cluster = None
        widened = False
        corpus_idx = _corpus_indices(cache, allowed)
        cand_rids_all = [cache.recipe_ids[i] for i in corpus_idx]
        ranked_rids = _rank_filter_only_candidates(db, cand_rids_all)
        passing = {rid: 0.0 for rid in ranked_rids}
    else:

        q_tfidf = cache.vectorizer.transform([combined_q]) 

        target_cluster = _assign_cluster(cache, combined_q)

        widen_threshold = max(1, payload.top_k)

        cluster_idx = _cluster_indices(cache, target_cluster, allowed)
        passing = _score_indices(cache, q_tfidf, cluster_idx)

        widened = False
        if target_cluster is not None and len(passing) < widen_threshold:
            corpus_idx = _corpus_indices(cache, allowed)
            passing = _score_indices(cache, q_tfidf, corpus_idx)
            widened = True

    if not passing:

        return _empty_response(payload, cluster_used=target_cluster, excluded_count=len(excluded_ids))

    cand_rids = sorted(passing, key=lambda rid: passing[rid], reverse=True)[:MAX_RESULTS]
    sim_by_rid = {rid: passing[rid] for rid in cand_rids}

    meta = batch_recipe_meta(db, cand_rids)
    ing_map = batch_ingredients(db, cand_rids)
    rating_map = batch_rating_scores(db, cand_rids)
    fav_map = batch_favorite_counts(db, cand_rids)

    candidates = []
    for rid in cand_rids:
        if rid not in meta:
            continue  # dropped: unapproved/deleted since last rebuild

        row = meta[rid]
        ingredients = ing_map.get(rid, [])
        candidates.append(
            {
                "recipe_id": rid,
                "title": row[1],
                "description": row[2],
                "prep_time": row[4],
                "cook_time": row[5],
                "servings": row[6],
                "difficulty": row[7],
                "image_url": row[8],
                "calories": row[9],
                "ingredients": ingredients,
                "tfidf_score": round(sim_by_rid[rid], 6),
                "ingredient_overlap_score": round(
                    ingredient_overlap_score(query_tokens, ingredients), 6
                ),
                "rating_score": round(rating_map.get(rid, 0.0), 6),
                "favorite_count": fav_map.get(rid, 0),
                "cluster": target_cluster,
            }
        )

    if not candidates:
        return _empty_response(payload, cluster_used=target_cluster, excluded_count=len(excluded_ids))

    max_fav = max(c["favorite_count"] for c in candidates) or 1
    for c in candidates:
        pop = c["favorite_count"] / max_fav
        c["popularity_score"] = round(pop, 6)
        c["final_score"] = round(
            W_TFIDF * c["tfidf_score"]
            + W_OVERLAP * c["ingredient_overlap_score"]
            + W_RATING * c["rating_score"]
            + W_POPULARITY * pop,
            6,
        )

    candidates.sort(key=lambda x: x["final_score"], reverse=True)
    results = candidates

    fallback_message = None
    if payload.query and payload.ingredient_filters:
        q_pre = preprocess(payload.query)
        if q_pre and not any(q_pre in r["title"].lower() for r in results):
            ing_str = ", ".join(payload.ingredient_filters)
            fallback_message = (
                f'No "{payload.query}" with "{ing_str}" found. You may like these:'
            )

    return {
        "query": payload.query,
        "diet_tag": payload.diet_tag,
        "ingredient_filters": payload.ingredient_filters,
        "cuisine": payload.cuisine,
        "meal_tags": payload.meal_tags,
        "cluster_used": target_cluster,
        "widened_to_corpus": widened,
        "excluded_by_preferences": len(excluded_ids),
        "count": len(results),
        "is_fallback": fallback_message is not None,
        "fallback_message": fallback_message,
        "ranking_formula": {
            "tfidf_score": W_TFIDF,
            "ingredient_overlap_score": W_OVERLAP,
            "rating_score": W_RATING,
            "popularity_score": W_POPULARITY,
        },
        "results": results,
    }


def _empty_response(
    payload: SearchRequest, cluster_used: int | None = None, excluded_count: int = 0
) -> dict:
    return {
        "query": payload.query,
        "diet_tag": payload.diet_tag,
        "ingredient_filters": payload.ingredient_filters,
        "cuisine": payload.cuisine,
        "meal_tags": payload.meal_tags,
        "cluster_used": cluster_used,
        "widened_to_corpus": False,
        "excluded_by_preferences": excluded_count,
        "count": 0,
        "is_fallback": False,
        "fallback_message": None,
        "results": [],
    }