from __future__ import annotations

import logging
from datetime import datetime, timezone

from fastapi import APIRouter, BackgroundTasks, FastAPI, Request
from sqlalchemy.orm import Session

from db import SessionLocal
from model_cache import ModelCache, build_model_cache

logger = logging.getLogger(__name__)

router = APIRouter(prefix="/admin")


# ---------------------------------------------------------------------------
# GET /admin/model-info
# ---------------------------------------------------------------------------

@router.get("/model-info")
def model_info(request: Request):
    cache: ModelCache | None = getattr(request.app.state, "model_cache", None)
    if cache is None:
        return {"status": "not_loaded"}

    umap_dims = (
        cache.umap_embeddings.shape[1]
        if cache.umap_embeddings is not None
        else None
    )
    return {
        "status": "loaded",
        "built_at": cache.built_at.isoformat(),
        "n_recipes": cache.n_recipes,
        "n_clusters": cache.n_clusters,
        "umap_dims": umap_dims,
    }


# ---------------------------------------------------------------------------
# POST /admin/rebuild-model
# ---------------------------------------------------------------------------

def _rebuild_task(app: FastAPI) -> None:
    db: Session = SessionLocal()
    try:
        logger.info(
            "Background model rebuild started at %s",
            datetime.now(timezone.utc).isoformat(),
        )
        new_cache = build_model_cache(db)
        app.state.model_cache = new_cache  # atomic reference swap
        logger.info(
            "Background model rebuild complete — %d recipes, %d clusters",
            new_cache.n_recipes,
            new_cache.n_clusters,
        )
    except Exception as exc:  # noqa: BLE001
        logger.error("Background model rebuild failed: %s", exc, exc_info=True)
    finally:
        db.close()


@router.post("/rebuild-model")
def rebuild_model(request: Request, background_tasks: BackgroundTasks):
    cache: ModelCache | None = getattr(request.app.state, "model_cache", None)
    background_tasks.add_task(_rebuild_task, request.app)
    return {
        "message": "Model rebuild started in background.",
        "current_built_at": cache.built_at.isoformat() if cache else None,
    }
