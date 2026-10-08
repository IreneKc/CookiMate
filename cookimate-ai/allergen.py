from __future__ import annotations

import logging
from typing import Optional

from fastapi import APIRouter, Depends
from sqlalchemy import text
from sqlalchemy.orm import Session

from allergen_taxonomy import ALL_TAGS
from db import get_db

logger = logging.getLogger(__name__)

router = APIRouter(prefix="/allergen")

# ---------------------------------------------------------------------------
# Pre-process the taxonomy into a lowercase lookup for fast matching.
# key = lowercase ingredient_name, value = set of allergen categories
# ---------------------------------------------------------------------------
_TAXONOMY_LOOKUP: dict[str, set[str]] = {}
for _name, _categories in ALL_TAGS.items():
    _TAXONOMY_LOOKUP[_name.lower()] = set(_categories)

ALLERGEN_CATEGORIES = [
    "peanut", "tree-nut", "shellfish", "fish",
    "egg", "dairy", "soy", "gluten", "sesame",
]


def _ensure_allergen_tags_exist(db: Session) -> dict[str, int]:
    tag_map: dict[str, int] = {}

    for category in ALLERGEN_CATEGORIES:
        row = db.execute(
            text("SELECT tag_id FROM tags WHERE tag_name = :name AND tag_type = 'allergen'"),
            {"name": category},
        ).fetchone()

        if row:
            tag_map[category] = row[0]
        else:
            db.execute(
                text("INSERT INTO tags (tag_name, tag_type) VALUES (:name, 'allergen')"),
                {"name": category},
            )
            db.flush()
            new_row = db.execute(
                text("SELECT tag_id FROM tags WHERE tag_name = :name AND tag_type = 'allergen'"),
                {"name": category},
            ).fetchone()
            tag_map[category] = new_row[0]

    return tag_map


def detect_allergens_for_recipe(db: Session, recipe_id: int, tag_map: dict[str, int]) -> list[str]:
    rows = db.execute(
        text("""
            SELECT i.ingredient_name
            FROM recipe_ingredients ri
            JOIN ingredients i ON i.ingredient_id = ri.ingredient_id
            WHERE ri.recipe_id = :rid
        """),
        {"rid": recipe_id},
    ).fetchall()

    ingredient_names = [r[0] for r in rows]

    matched: set[str] = set()
    for ing_name in ingredient_names:
        key = ing_name.lower()
        if key in _TAXONOMY_LOOKUP:
            matched.update(_TAXONOMY_LOOKUP[key])


    allergen_tag_ids = list(tag_map.values())
    if allergen_tag_ids:
        placeholders = ", ".join(f":tid{i}" for i in range(len(allergen_tag_ids)))
        params = {"rid": recipe_id}
        params.update({f"tid{i}": tid for i, tid in enumerate(allergen_tag_ids)})
        db.execute(
            text(f"""
                DELETE FROM recipe_tags
                WHERE recipe_id = :rid AND tag_id IN ({placeholders})
            """),
            params,
        )

    for category in matched:
        tag_id = tag_map.get(category)
        if tag_id:
            db.execute(
                text("INSERT IGNORE INTO recipe_tags (recipe_id, tag_id) VALUES (:rid, :tid)"),
                {"rid": recipe_id, "tid": tag_id},
            )

    return sorted(matched)


# ---------------------------------------------------------------------------
# Endpoint: tag one recipe
# ---------------------------------------------------------------------------
@router.post("/detect/{recipe_id}")
def detect_single(recipe_id: int, db: Session = Depends(get_db)):
    tag_map = _ensure_allergen_tags_exist(db)
    matched = detect_allergens_for_recipe(db, recipe_id, tag_map)
    db.commit()

    logger.info("Allergen detection for recipe %d: %s", recipe_id, matched or "none")

    return {
        "recipe_id": recipe_id,
        "allergens_found": matched,
        "tags_assigned": len(matched),
    }


# ---------------------------------------------------------------------------
# Endpoint: re-tag ALL recipes (batch)
# ---------------------------------------------------------------------------
@router.post("/detect-all")
def detect_all(db: Session = Depends(get_db)):

    tag_map = _ensure_allergen_tags_exist(db)

    rows = db.execute(
        text("SELECT DISTINCT recipe_id FROM recipe_ingredients ORDER BY recipe_id")
    ).fetchall()

    recipe_ids = [r[0] for r in rows]

    results = {}
    total_tags = 0

    for recipe_id in recipe_ids:
        matched = detect_allergens_for_recipe(db, recipe_id, tag_map)
        if matched:
            results[recipe_id] = matched
            total_tags += len(matched)

    db.commit()

    logger.info(
        "Batch allergen detection complete: %d recipes scanned, %d tagged, %d total tags",
        len(recipe_ids), len(results), total_tags,
    )

    return {
        "recipes_scanned": len(recipe_ids),
        "recipes_with_allergens": len(results),
        "total_tags_assigned": total_tags,
        "details": results,
    }

