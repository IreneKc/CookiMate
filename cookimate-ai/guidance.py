from __future__ import annotations

import re

from fastapi import APIRouter, Depends, Request
from pydantic import BaseModel
from sqlalchemy import text
from sqlalchemy.orm import Session

from db import get_db
from llm_fallback import llm_extract_slots
from scoring_utils import (
    batch_favorite_counts,
    batch_ingredients,
    batch_rating_scores,
    batch_recipe_meta,
)
from search import MAX_RESULTS, SearchRequest, _apply_filters, search_recipes
from text_utils import preprocess, tokenize

router = APIRouter(prefix="/assistant")

DISPLAY_LIMIT = 5
WEAK_MATCH_TFIDF = 0.25

# ---------------------------------------------------------------------------
# Time constraint extraction
# ---------------------------------------------------------------------------

_TIME_PATTERNS = [
    re.compile(r"\bunder (\d{1,3})\s*(?:minutes|mins|min)\b"),
    re.compile(r"\bless than (\d{1,3})\s*(?:minutes|mins|min)\b"),
    re.compile(r"\bwithin (\d{1,3})\s*(?:minutes|mins|min)\b"),
    re.compile(r"\b(\d{1,3})\s*(?:minutes|mins|min) or less\b"),
    re.compile(r"\b(\d{1,3})\s*(?:minutes|mins|min) max\b"),
]

_QUICK_KEYWORDS = {"quick", "fast", "speedy"}
QUICK_DEFAULT_MINUTES = 20


def extract_time_limit(raw_text: str) -> int | None:
    lowered = raw_text.lower()
    for pattern in _TIME_PATTERNS:
        m = pattern.search(lowered)
        if m:
            return int(m.group(1))

    tokens = set(tokenize(raw_text))
    if tokens & _QUICK_KEYWORDS:
        return QUICK_DEFAULT_MINUTES

    return None


# ---------------------------------------------------------------------------
# Calorie constraint extraction  (structural twin of the time constraint)
# ---------------------------------------------------------------------------

_CALORIE_PATTERNS = [
    re.compile(r"\bunder (\d{1,4})\s*(?:calories|cals|cal|kcal)\b"),
    re.compile(r"\bless than (\d{1,4})\s*(?:calories|cals|cal|kcal)\b"),
    re.compile(r"\bwithin (\d{1,4})\s*(?:calories|cals|cal|kcal)\b"),
    re.compile(r"\b(\d{1,4})\s*(?:calories|cals|cal|kcal) or less\b"),
    re.compile(r"\b(\d{1,4})\s*(?:calories|cals|cal|kcal) max\b"),
]

_LOW_CAL_PHRASES = [
    re.compile(r"\blow[\s-]?calorie\b"),
    re.compile(r"\blow[\s-]?cal\b"),
]
LOW_CALORIE_DEFAULT = 400


def extract_calorie_limit(raw_text: str) -> int | None:
    lowered = raw_text.lower()
    for pattern in _CALORIE_PATTERNS:
        m = pattern.search(lowered)
        if m:
            return int(m.group(1))
    for phrase in _LOW_CAL_PHRASES:
        if phrase.search(lowered):
            return LOW_CALORIE_DEFAULT
    return None


# ---------------------------------------------------------------------------
# Tag matching — pulled live from the DB, not hardcoded
# ---------------------------------------------------------------------------

def _load_tags(db: Session) -> list[tuple[str, str]]:
    rows = db.execute(text("SELECT tag_name, tag_type FROM tags")).fetchall()
    return [(r[0], r[1]) for r in rows]


def extract_tags(db: Session, raw_text: str) -> tuple[dict[str, str], list[str]]:
    tokens = set(tokenize(raw_text))
    single: dict[str, str] = {}
    meals: list[str] = []

    for tag_name, tag_type in _load_tags(db):
        tag_tokens = set(tokenize(tag_name))
        if not tag_tokens or not (tag_tokens <= tokens):
            continue
        if tag_type == "meal":
            if tag_name not in meals:
                meals.append(tag_name)
        elif tag_type not in single:
            single[tag_type] = tag_name

    return single, meals


# ---------------------------------------------------------------------------
# Residual query — free text minus matched slot phrases
# ---------------------------------------------------------------------------

def strip_matched_phrases(
    raw_text: str,
    single_tags: dict[str, str],
    meal_tags: list[str],
    had_time: bool,
    had_calorie: bool = False,
) -> str:
    residual = raw_text
    for tag_name in list(single_tags.values()) + meal_tags:
        residual = re.sub(re.escape(tag_name), " ", residual, flags=re.IGNORECASE)
    if had_time:
        for pattern in _TIME_PATTERNS:
            residual = pattern.sub(" ", residual)
    if had_calorie:
        for pattern in _CALORIE_PATTERNS:
            residual = pattern.sub(" ", residual)
        for phrase in _LOW_CAL_PHRASES:
            residual = phrase.sub(" ", residual)
    return preprocess(residual)


# ---------------------------------------------------------------------------
# Parsed result + endpoints
# ---------------------------------------------------------------------------

class ParsedQuery(BaseModel):
    raw_text: str
    residual_query: str
    time_limit_minutes: int | None
    calorie_limit: int | None = None
    diet_tag: str | None
    cuisine: str | None
    meal_tags: list[str]
    other_tags: dict[str, str]
    used_llm: bool = False


class GuidanceMessage(BaseModel):
    message: str


def parse_message(
    db: Session, raw_text: str, allow_llm: bool = False
) -> ParsedQuery:
    time_limit = extract_time_limit(raw_text)
    calorie_limit = extract_calorie_limit(raw_text)
    single_tags, meal_tags = extract_tags(db, raw_text)
    residual = strip_matched_phrases(
        raw_text,
        single_tags,
        meal_tags,
        had_time=time_limit is not None,
        had_calorie=calorie_limit is not None,
    )

    diet = single_tags.pop("diet", None)
    cuisine = single_tags.pop("cuisine", None)

    used_llm = False
    rules_found_no_slots = (
        diet is None
        and cuisine is None
        and not meal_tags
        and time_limit is None
        and calorie_limit is None
    )
    if allow_llm and rules_found_no_slots:
        vocab = _vocab_from_tags(db)
        slots = llm_extract_slots(raw_text, vocab)
        if slots is not None:
            diet = slots["diet"]
            cuisine = slots["cuisine"]
            meal_tags = slots["meal"]
            time_limit = slots["max_minutes"]
            calorie_limit = slots["max_calories"]
            used_llm = True
            residual = ""

    return ParsedQuery(
        raw_text=raw_text,
        residual_query=residual,
        time_limit_minutes=time_limit,
        calorie_limit=calorie_limit,
        diet_tag=diet,
        cuisine=cuisine,
        meal_tags=meal_tags,
        other_tags=single_tags,
        used_llm=used_llm,
    )


def _vocab_from_tags(db: Session) -> dict[str, list[str]]:
    vocab: dict[str, list[str]] = {"diet": [], "cuisine": [], "meal": []}
    for tag_name, tag_type in _load_tags(db):
        if tag_type in vocab and tag_name not in vocab[tag_type]:
            vocab[tag_type].append(tag_name)
    return vocab


@router.post("/parse")
def parse_only(payload: GuidanceMessage, db: Session = Depends(get_db)):
    return parse_message(db, payload.message, allow_llm=True)


def _run_pipeline(
    parsed: ParsedQuery, request: Request, db: Session
) -> tuple[list[dict], bool]:
    has_filters = bool(
        parsed.diet_tag
        or parsed.cuisine
        or parsed.meal_tags
        or parsed.time_limit_minutes is not None
        or parsed.calorie_limit is not None
    )
    has_text_query = bool(parsed.residual_query.strip())

    results: list[dict] = []

    if has_text_query:
        search_payload = SearchRequest(
            query=parsed.residual_query,
            diet_tag=parsed.diet_tag,
            cuisine=parsed.cuisine,
            meal_tags=parsed.meal_tags or None,
        )
        search_result = search_recipes(search_payload, request, db)
        results = search_result["results"]
        if parsed.time_limit_minutes is not None:
            results = [
                r for r in results
                if (r["prep_time"] or 0) + (r["cook_time"] or 0)
                <= parsed.time_limit_minutes
            ]
        if parsed.calorie_limit is not None:
            results = [
                r for r in results
                if r.get("calories") is not None
                and r["calories"] <= parsed.calorie_limit
            ]

    used_filter_only = False
    if not results and has_filters:
        results = _filter_only_results(db, parsed)
        used_filter_only = True

    return results, used_filter_only


def _top_match_is_weak(results: list[dict]) -> bool:
    if not results:
        return True
    top_score = results[0].get("tfidf_score")
    if top_score is None:
        return False
    return top_score < WEAK_MATCH_TFIDF


@router.post("/message")
def guidance_message(
    payload: GuidanceMessage,
    request: Request,
    db: Session = Depends(get_db),
):
    parsed = parse_message(db, payload.message, allow_llm=False)
    results, used_filter_only = _run_pipeline(parsed, request, db)

    rules_found_no_slots = not (
        parsed.diet_tag
        or parsed.cuisine
        or parsed.meal_tags
        or parsed.time_limit_minutes is not None
        or parsed.calorie_limit is not None
    )
    relevance_is_weak = rules_found_no_slots and _top_match_is_weak(results)

    if relevance_is_weak:
        vocab = _vocab_from_tags(db)
        slots = llm_extract_slots(payload.message, vocab)
        if slots is not None:
            llm_parsed = ParsedQuery(
                raw_text=payload.message,
                residual_query="",
                time_limit_minutes=slots["max_minutes"],
                calorie_limit=slots["max_calories"],
                diet_tag=slots["diet"],
                cuisine=slots["cuisine"],
                meal_tags=slots["meal"],
                other_tags={},
                used_llm=True,
            )
            llm_results, llm_filter_only = _run_pipeline(llm_parsed, request, db)
            if llm_results:
                parsed = llm_parsed
                results = llm_results
                used_filter_only = llm_filter_only

    return {
        "parsed": parsed,
        "count": len(results),
        "display_limit": DISPLAY_LIMIT,
        "results": results,
        "filter_only": used_filter_only,
        "reply": _build_reply_text(parsed, results, used_filter_only, DISPLAY_LIMIT),
    }


W_RATING_ONLY = 0.6
W_POP_ONLY = 0.4


def _passes_time(row_meta: tuple, limit: int | None) -> bool:
    if limit is None:
        return True
    prep = row_meta[4] or 0
    cook = row_meta[5] or 0
    return (prep + cook) <= limit


def _passes_calorie(row_meta: tuple, limit: int | None) -> bool:
    if limit is None:
        return True
    calories = row_meta[9] if len(row_meta) > 9 else None
    if calories is None:
        return False
    return calories <= limit


def _filter_only_results(db: Session, parsed: ParsedQuery) -> list[dict]:
    filter_payload = SearchRequest(
        query="",
        diet_tag=parsed.diet_tag,
        cuisine=parsed.cuisine,
        meal_tags=parsed.meal_tags or None,
    )
    allowed = _apply_filters(db, filter_payload)

    if allowed is None:
        rows = db.execute(
            text("SELECT recipe_id FROM recipes WHERE status = 'approved'")
        ).fetchall()
        ids = [r[0] for r in rows]
    else:
        if not allowed:
            return []
        ids = list(allowed)

    meta = batch_recipe_meta(db, ids)
    rating_map = batch_rating_scores(db, ids)
    fav_map = batch_favorite_counts(db, ids)
    ing_map = batch_ingredients(db, ids)

    kept = [
        rid for rid in ids
        if rid in meta
        and _passes_time(meta[rid], parsed.time_limit_minutes)
        and _passes_calorie(meta[rid], parsed.calorie_limit)
    ]
    if not kept:
        return []

    max_fav = max((fav_map.get(rid, 0) for rid in kept), default=0) or 1

    results: list[dict] = []
    for rid in kept:
        row = meta[rid]
        rating = rating_map.get(rid, 0.0)
        pop = fav_map.get(rid, 0) / max_fav
        results.append(
            {
                "recipe_id": rid,
                "title": row[1],
                "description": row[2],
                "prep_time": row[4],
                "cook_time": row[5],
                "servings": row[6],
                "difficulty": row[7],
                "image_url": row[8],
                "calories": row[9] if len(row) > 9 else None,
                "ingredients": ing_map.get(rid, []),
                "tfidf_score": None,
                "rating_score": round(rating, 6),
                "favorite_count": fav_map.get(rid, 0),
                "popularity_score": round(pop, 6),
                "final_score": round(W_RATING_ONLY * rating + W_POP_ONLY * pop, 6),
                "cluster": None,
            }
        )

    results.sort(key=lambda x: x["final_score"], reverse=True)
    return results[:MAX_RESULTS]


def _join_or(items: list[str]) -> str:
    items = [i for i in items if i]
    if not items:
        return ""
    if len(items) == 1:
        return items[0]
    return f"{', '.join(items[:-1])} or {items[-1]}"


def _describe_constraints(parsed: ParsedQuery, plural: bool = True) -> str:
    parts: list[str] = []
    if parsed.cuisine:
        parts.append(parsed.cuisine)
    if parsed.diet_tag:
        parts.append(parsed.diet_tag)
    if parsed.meal_tags:
        parts.append(_join_or(parsed.meal_tags))

    noun_word = "recipes" if plural else "recipe"
    phrase = " ".join(parts).strip()
    noun = f"{phrase} {noun_word}" if phrase else noun_word

    extras: list[str] = []
    if parsed.time_limit_minutes is not None:
        extras.append(f"under {parsed.time_limit_minutes} minutes")
    if parsed.calorie_limit is not None:
        extras.append(f"under {parsed.calorie_limit} calories")
    if extras:
        noun += " " + " and ".join(extras)
    return noun


def _build_reply_text(
    parsed: ParsedQuery,
    results: list[dict],
    filter_only: bool,
    display_limit: int,
) -> str:
    if not results:
        return (
            "I couldn't find a recipe matching that — try loosening a "
            "constraint (time, diet, or cuisine)?"
        )

    total = len(results)
    top = results[0]["title"]
    shown = min(total, display_limit)
    has_more = total > display_limit

    if filter_only:
        if total == 1:
            return f"I found one {_describe_constraints(parsed, plural=False)}: {top}."
        desc = _describe_constraints(parsed, plural=True)
        if has_more:
            return (
                f"I found {total} {desc} — here are the top {shown}, like {top}."
            )
        return f"Here are {total} {desc} to try, like {top}."

    # Relevance path (a dish term was present and ranked by TF-IDF).
    if total == 1:
        return f"I found one match: {top}."
    if has_more:
        return (
            f"I found {total} matches — showing the top {shown}, top pick: {top}."
        )
    return f"I found {total} matches — top pick: {top}."