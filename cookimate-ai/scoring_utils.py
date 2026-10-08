from __future__ import annotations

from sqlalchemy import bindparam, text
from sqlalchemy.orm import Session


def _expanding(sql: str):
    return text(sql).bindparams(bindparam("ids", expanding=True))


def batch_ingredients(db: Session, recipe_ids) -> dict[int, list[str]]:
    ids = list(recipe_ids)
    result: dict[int, list[str]] = {rid: [] for rid in ids}
    if not ids:
        return result

    stmt = _expanding(
        """
        SELECT ri.recipe_id, i.ingredient_name
        FROM recipe_ingredients ri
        JOIN ingredients i ON i.ingredient_id = ri.ingredient_id
        WHERE ri.recipe_id IN :ids
        ORDER BY ri.recipe_id, i.ingredient_name
        """
    )
    for rid, name in db.execute(stmt, {"ids": ids}).fetchall():
        result[rid].append(name)
    return result


def batch_rating_scores(db: Session, recipe_ids) -> dict[int, float]:
    ids = list(recipe_ids)
    result: dict[int, float] = {rid: 0.0 for rid in ids}
    if not ids:
        return result

    stmt = _expanding(
        """
        SELECT recipe_id, AVG(rating)
        FROM recipe_reviews
        WHERE recipe_id IN :ids
        GROUP BY recipe_id
        """
    )
    for rid, avg in db.execute(stmt, {"ids": ids}).fetchall():
        result[rid] = (float(avg) / 5.0) if avg is not None else 0.0
    return result


def batch_favorite_counts(db: Session, recipe_ids) -> dict[int, int]:
    ids = list(recipe_ids)
    result: dict[int, int] = {rid: 0 for rid in ids}
    if not ids:
        return result

    stmt = _expanding(
        """
        SELECT recipe_id, COUNT(*)
        FROM favorites
        WHERE recipe_id IN :ids
        GROUP BY recipe_id
        """
    )
    for rid, cnt in db.execute(stmt, {"ids": ids}).fetchall():
        result[rid] = int(cnt)
    return result


def batch_recipe_meta(db: Session, recipe_ids) -> dict[int, tuple]:
    ids = list(recipe_ids)
    result: dict[int, tuple] = {}
    if not ids:
        return result

    stmt = _expanding(
        """
        SELECT r.recipe_id, r.title, r.description, r.instructions,
               r.prep_time, r.cook_time, r.servings, r.difficulty, r.image_url,
               n.calories
        FROM recipes r
        LEFT JOIN recipe_nutrition n ON n.recipe_id = r.recipe_id
        WHERE r.recipe_id IN :ids AND r.status = 'approved'
        """
    )
    for row in db.execute(stmt, {"ids": ids}).fetchall():
        result[row[0]] = row
    return result


def ingredient_overlap_score(query_tokens: list[str], ingredients: list[str]) -> float:
    if not query_tokens:
        return 0.0
    blob = " ".join(i.lower() for i in ingredients)
    matched = sum(1 for tok in query_tokens if tok in blob)
    return matched / len(query_tokens)