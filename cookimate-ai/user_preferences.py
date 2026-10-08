from __future__ import annotations

from sqlalchemy import bindparam, text
from sqlalchemy.orm import Session


def load_user_preferences(db: Session, user_id: int) -> dict:

    diet_row = db.execute(
        text("SELECT diet FROM users WHERE user_id = :uid LIMIT 1"),
        {"uid": user_id},
    ).fetchone()
    diet = (diet_row[0] or "").strip() if diet_row else ""

    pref_rows = db.execute(
        text("SELECT pref_type, term FROM user_food_preferences WHERE user_id = :uid"),
        {"uid": user_id},
    ).fetchall()

    return {
        "diet": diet or None,
        "allergies": [r[1] for r in pref_rows if r[0] == "allergy" and r[1]],
        "dislikes": [r[1] for r in pref_rows if r[0] == "dislike" and r[1]],
    }


def excluded_by_allergens(db: Session, allergies: list[str]) -> set[int]:
    terms = [a.strip().lower() for a in allergies if a and a.strip()]
    if not terms:
        return set()
    stmt = text(
        """
        SELECT DISTINCT rt.recipe_id FROM recipe_tags rt
        JOIN tags t ON t.tag_id = rt.tag_id
        WHERE t.tag_type = 'allergen' AND LOWER(t.tag_name) IN :terms
        """
    ).bindparams(bindparam("terms", expanding=True))
    rows = db.execute(stmt, {"terms": terms}).fetchall()
    return {r[0] for r in rows}


def excluded_by_dislikes(db: Session, dislikes: list[str]) -> set[int]:
    terms = [d.strip().lower() for d in dislikes if d and d.strip()]
    if not terms:
        return set()

    conditions = " OR ".join(f"LOWER(i.ingredient_name) LIKE :d{i}" for i in range(len(terms)))
    params = {f"d{i}": f"%{t}%" for i, t in enumerate(terms)}
    rows = db.execute(
        text(
            f"""
            SELECT DISTINCT ri.recipe_id FROM recipe_ingredients ri
            JOIN ingredients i ON i.ingredient_id = ri.ingredient_id
            WHERE {conditions}
            """
        ),
        params,
    ).fetchall()
    return {r[0] for r in rows}
