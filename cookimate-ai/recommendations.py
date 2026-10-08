from __future__ import annotations

from collections import defaultdict

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel
from sqlalchemy import text
from sqlalchemy.orm import Session

from db import get_db
from model_cache import ModelCache
from scoring_utils import (
    batch_favorite_counts,
    batch_ingredients,
    batch_rating_scores,
    batch_recipe_meta,
)
from user_preferences import excluded_by_allergens, excluded_by_dislikes, load_user_preferences

router = APIRouter()

W_PREFERENCE = 0.75
W_RATING = 0.15
W_POPULARITY = 0.10

MIN_PREFERENCE = 0.15
DOMINANT_VOTE_SHARE = 0.15
MIN_DOMINANT_CLUSTERS = 1
MAX_DOMINANT_CLUSTERS = 5

DEFAULT_WIDEN_THRESHOLD = 5

MAX_RESULTS = 50


class RecommendRequest(BaseModel):
    user_id: int
    top_k: int = DEFAULT_WIDEN_THRESHOLD


# ---------------------------------------------------------------------------
# Dominant cluster detection
# ---------------------------------------------------------------------------

def _dominant_clusters(
    interaction_ids: set[int],
    favorite_ids: set[int],
    rated_map: dict[int, float],
    cache: ModelCache,
) -> list[int]:

    votes: dict[int, float] = defaultdict(float)

    for rid in interaction_ids:
        idx = cache.recipe_id_to_idx.get(rid)
        if idx is None:
            continue

        label = int(cache.cluster_labels[idx])
        if rid in favorite_ids:
            votes[label] += 1.0
        if rid in rated_map:
            votes[label] += rated_map[rid] / 5.0

    if not votes:
        return []

    ranked = sorted(votes.items(), key=lambda kv: kv[1], reverse=True)
    total = sum(v for _, v in ranked)

    above_share = [
        label for label, weight in ranked
        if total > 0 and (weight / total) >= DOMINANT_VOTE_SHARE
    ]

    if len(above_share) < MIN_DOMINANT_CLUSTERS:
        above_share = [label for label, _ in ranked[:MIN_DOMINANT_CLUSTERS]]

    return above_share[:MAX_DOMINANT_CLUSTERS]


def _score_candidates(
    db: Session,
    cache: ModelCache,
    cand_rids: list[int],
    ingredient_weights: dict[str, float],
    max_ing_weight: float,
) -> list[dict]:

    if not cand_rids:
        return []

    meta = batch_recipe_meta(db, cand_rids)
    cand_ing = batch_ingredients(db, cand_rids)
    rating_map = batch_rating_scores(db, cand_rids)
    fav_map = batch_favorite_counts(db, cand_rids)

    scored: list[dict] = []
    for rid in cand_rids:
        if rid not in meta:
            continue

        ingredients = cand_ing.get(rid, [])
        ingredient_set = {i.lower().strip() for i in ingredients}
        if not ingredient_set:
            continue

        matched_weight = sum(ingredient_weights.get(i, 0.0) for i in ingredient_set)
        max_possible = len(ingredient_set) * max_ing_weight
        preference = matched_weight / max_possible if max_possible > 0 else 0.0
        if preference < MIN_PREFERENCE:
            continue

        matched_ingredients = sorted(
            ing for ing in ingredients if ing.lower().strip() in ingredient_weights
        )
        row = meta[rid]
        scored.append(
            {
                "recipe_id": rid,
                "title": row[1],
                "description": row[2],
                "prep_time": row[4],
                "cook_time": row[5],
                "servings": row[6],
                "difficulty": row[7],
                "image_url": row[8],
                "ingredients": ingredients,
                "matched_ingredients": matched_ingredients[:8],
                "ingredient_preference_score": round(preference, 6),
                "rating_score": round(rating_map.get(rid, 0.0), 6),
                "favorite_count": fav_map.get(rid, 0),
                "cluster": int(cache.cluster_labels[cache.recipe_id_to_idx[rid]]),
            }
        )
    return scored


# ---------------------------------------------------------------------------
# Endpoint
# ---------------------------------------------------------------------------

@router.post("/recommend")
def recommend_recipes(
    payload: RecommendRequest,
    request: Request,
    db: Session = Depends(get_db),
):
    cache: ModelCache | None = getattr(request.app.state, "model_cache", None)
    if cache is None:
        raise HTTPException(status_code=503, detail="ML model not ready. Try again shortly.")

    uid = payload.user_id

    fav_rows = db.execute(
        text("SELECT recipe_id FROM favorites WHERE user_id = :uid"),
        {"uid": uid},
    ).fetchall()
    favorite_ids = {r[0] for r in fav_rows}

    rated_rows = db.execute(
        text(
            """
            SELECT recipe_id, AVG(rating) AS avg_rating
            FROM recipe_reviews
            WHERE created_by = :uid
            GROUP BY recipe_id
            """
        ),
        {"uid": uid},
    ).fetchall()
    rated_map = {
        r[0]: float(r[1])
        for r in rated_rows
        if r[1] is not None and float(r[1]) >= 3.0
    }
    rated_ids = set(rated_map.keys())
    interaction_ids = favorite_ids | rated_ids

    if not interaction_ids:
        return {
            "user_id": uid,
            "count": 0,
            "message": "No favourites or positive ratings found. Cannot generate recommendations.",
            "results": [],
        }

    interaction_ing = batch_ingredients(db, list(interaction_ids))
    ingredient_weights: dict[str, float] = defaultdict(float)
    for rid in interaction_ids:
        base_weight = 0.0
        if rid in favorite_ids:
            base_weight += 1.0
        if rid in rated_map:
            base_weight += rated_map[rid] / 5.0
        for ing in interaction_ing.get(rid, []):
            ingredient_weights[ing.lower().strip()] += base_weight

    if not ingredient_weights:
        return {
            "user_id": uid,
            "count": 0,
            "message": "No ingredient profile could be built from favourites or ratings.",
            "results": [],
        }

    max_ing_weight = max(ingredient_weights.values())

    prefs = load_user_preferences(db, uid)
    excluded = excluded_by_allergens(db, prefs["allergies"]) | excluded_by_dislikes(
        db, prefs["dislikes"]
    )

    def _preference_ok(rid: int) -> bool:
        return rid not in excluded

    dominant = _dominant_clusters(
        interaction_ids, favorite_ids, rated_map, cache
    )

    widen_threshold = max(1, payload.top_k)

    if dominant:
        pool_rids = [
            cache.recipe_ids[i]
            for i, lbl in enumerate(cache.cluster_labels)
            if int(lbl) in dominant
            and cache.recipe_ids[i] not in interaction_ids
            and _preference_ok(cache.recipe_ids[i])
        ]
    else:
        pool_rids = []

    candidates = _score_candidates(
        db, cache, pool_rids, ingredient_weights, max_ing_weight
    )

    widened = False
    if len(candidates) < widen_threshold:
        corpus_rids = [
            cache.recipe_ids[i]
            for i in range(len(cache.recipe_ids))
            if cache.recipe_ids[i] not in interaction_ids
            and _preference_ok(cache.recipe_ids[i])
        ]
        candidates = _score_candidates(
            db, cache, corpus_rids, ingredient_weights, max_ing_weight
        )
        widened = True

    if not candidates:
        return {
            "user_id": uid,
            "based_on_favorites": list(favorite_ids),
            "based_on_rated_recipes": list(rated_ids),
            "dominant_clusters": dominant,
            "widened_to_corpus": widened,
            "excluded_by_preferences": len(excluded),
            "count": 0,
            "message": "No suitable recommendations found from favourites and ratings.",
            "results": [],
        }

    max_fav = max(c["favorite_count"] for c in candidates) or 1
    for c in candidates:
        pop = c["favorite_count"] / max_fav
        c["popularity_score"] = round(pop, 6)
        c["final_score"] = round(
            W_PREFERENCE * c["ingredient_preference_score"]
            + W_RATING * c["rating_score"]
            + W_POPULARITY * pop,
            6,
        )

    candidates.sort(key=lambda x: x["final_score"], reverse=True)
    results = candidates[:MAX_RESULTS]

    return {
        "user_id": uid,
        "based_on_favorites": list(favorite_ids),
        "based_on_rated_recipes": list(rated_ids),
        "dominant_clusters": dominant,
        "widened_to_corpus": widened,
        "excluded_by_preferences": len(excluded),
        "count": len(results),
        "ranking_formula": {
            "ingredient_preference_score": W_PREFERENCE,
            "rating_score": W_RATING,
            "popularity_score": W_POPULARITY,
        },
        "results": results,
    }