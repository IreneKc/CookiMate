from __future__ import annotations
from security import require_api_key

import logging
import hdbscan
import numpy as np
from collections import defaultdict
from contextlib import asynccontextmanager

from apscheduler.schedulers.background import BackgroundScheduler
from fastapi import Depends, FastAPI, Query, Request
from sqlalchemy import text
from sqlalchemy.orm import Session

from admin import router as admin_router
from db import SessionLocal, get_db
from model_cache import build_model_cache
from recommendations import router as recommend_router
from search import router as search_router
from guidance import router as guidance_router
from allergen import router as allergen_router

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# Nightly rebuild (runs in the APScheduler background thread)
# ---------------------------------------------------------------------------

def _nightly_rebuild(app: FastAPI) -> None:
    logger.info("Nightly model rebuild triggered by scheduler")
    db = SessionLocal()
    try:
        new_cache = build_model_cache(db)
        app.state.model_cache = new_cache
        logger.info(
            "Nightly rebuild complete — %d recipes, %d clusters",
            new_cache.n_recipes,
            new_cache.n_clusters,
        )
    except Exception as exc:
        logger.error("Nightly rebuild failed: %s", exc, exc_info=True)
    finally:
        db.close()


# ---------------------------------------------------------------------------
# Lifespan
# ---------------------------------------------------------------------------

@asynccontextmanager
async def lifespan(app: FastAPI):
    logger.info("CookiMate AI Engine starting up …")

    db = SessionLocal()
    try:
        app.state.model_cache = build_model_cache(db)
    except Exception as exc:
        logger.error("Startup model build failed: %s", exc, exc_info=True)
        app.state.model_cache = None
    finally:
        db.close()

    scheduler = BackgroundScheduler()
    scheduler.add_job(
        _nightly_rebuild,
        trigger="cron",
        hour=2,
        minute=0,
        args=[app],
        id="nightly_model_rebuild",
        replace_existing=True,
    )
    scheduler.start()
    logger.info("Nightly scheduler started (fires at 02:00)")

    try:
        yield
    finally:
        logger.info("CookiMate AI Engine shutting down …")
        scheduler.shutdown(wait=False)


app = FastAPI(title="CookiMate AI Engine", lifespan=lifespan)

protected = [Depends(require_api_key)]

app.include_router(search_router, dependencies=protected)
app.include_router(recommend_router, dependencies=protected)
app.include_router(admin_router, dependencies=protected)
app.include_router(guidance_router, dependencies=protected)
app.include_router(allergen_router, dependencies=protected)

# ---------------------------------------------------------------------------
# Lightweight endpoints (no ML dependency)
# ---------------------------------------------------------------------------

@app.get("/health")
def health(request: Request, db: Session = Depends(get_db)):
    db.execute(text("SELECT 1"))

    cache = getattr(request.app.state, "model_cache", None)
    return {
        "status": "ok",
        "database": "connected",
        "model": {
            "loaded": cache is not None,
            "built_at": cache.built_at.isoformat() if cache else None,
            "n_recipes": cache.n_recipes if cache else None,
            "n_clusters": cache.n_clusters if cache else None,
        },
    }


@app.get("/recipes", dependencies=protected)
def list_recipes(db: Session = Depends(get_db)):
    rows = db.execute(text("SELECT recipe_id, title FROM recipes")).fetchall()
    return [{"recipe_id": r[0], "title": r[1]} for r in rows]


@app.get("/debug/clusters", dependencies=protected)
def debug_clusters(request: Request, db: Session = Depends(get_db)):
    cache = getattr(request.app.state, "model_cache", None)
    if cache is None:
        return {"error": "model not loaded"}

    groups = defaultdict(list)
    for rid, idx in cache.recipe_id_to_idx.items():
        groups[int(cache.cluster_labels[idx])].append(rid)

    rows = db.execute(text("SELECT recipe_id, title FROM recipes")).fetchall()
    titles = {r[0]: r[1] for r in rows}

    return {
        "n_clusters": cache.n_clusters,
        "clusters": {
            str(c): [
                {"id": rid, "title": titles.get(rid, "?")}
                for rid in sorted(members)
            ]
            for c, members in sorted(groups.items())
        },
    }

@app.get("/debug/tune", dependencies=protected)
def debug_tune(
    request: Request,
    min_cluster_sizes: list[int] = Query(default=[4, 5, 6, 8, 10]),
    min_samples_list: list[int] = Query(default=[1, 2, 3, 5]),
):

    cache = getattr(request.app.state, "model_cache", None)
    if cache is None:
        return {"error": "model not loaded"}

    emb = cache.umap_embeddings
    if emb is None:
        return {"error": "no UMAP embedding (single-cluster fallback path); "
                         "need >= MIN_RECIPES_FOR_CLUSTERING approved recipes"}
    emb = np.asarray(emb)
    n = len(emb)

    results = []
    for mcs in min_cluster_sizes:
        for ms in min_samples_list:
            clusterer = hdbscan.HDBSCAN(
                min_cluster_size=int(mcs),
                min_samples=int(ms),
                metric="euclidean",
                gen_min_span_tree=True,
            )
            labels = clusterer.fit_predict(emb)
            n_clusters = int(labels.max()) + 1 if labels.max() >= 0 else 0
            n_noise = int((labels == -1).sum())
            dbcv = (round(float(clusterer.relative_validity_), 4)
                    if n_clusters >= 2 else None)
            results.append({
                "min_cluster_size": int(mcs),
                "min_samples": int(ms),
                "n_clusters": n_clusters,
                "n_noise": n_noise,
                "noise_pct": round(100.0 * n_noise / n, 1),
                "dbcv": dbcv,
                "is_current": (int(mcs), int(ms)) == (5, 1),
            })

    results.sort(key=lambda r: (r["dbcv"] if r["dbcv"] is not None else -1e9),
                 reverse=True)
    return {
        "n_recipes": n,
        "embedding_dims": int(emb.shape[1]),
        "built_at": cache.built_at.isoformat(),
        "grid": {"min_cluster_size": list(min_cluster_sizes),
                 "min_samples": list(min_samples_list)},
        "results": results,
    }