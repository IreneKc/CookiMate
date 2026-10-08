from __future__ import annotations

import json
import logging
import os
import re
import threading

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------
_DEFAULT_MODEL_PATH = os.path.join(
    os.path.dirname(__file__), "models", "qwen2.5-1.5b-instruct-q4_k_m.gguf"
)
MODEL_PATH = os.environ.get("COOKIMATE_LLM_PATH", _DEFAULT_MODEL_PATH)

_N_CTX = 2048
_MAX_TOKENS = 128
_TEMPERATURE = 0.0

_MIN_TIME = 1
_MAX_TIME = 600

_MIN_CAL = 10
_MAX_CAL = 3000

# ---------------------------------------------------------------------------
# Lazy singleton model handle
# ---------------------------------------------------------------------------
_llm = None
_llm_lock = threading.Lock()
_llm_load_failed = False


def _get_llm():
    global _llm, _llm_load_failed

    if _llm is not None:
        return _llm
    if _llm_load_failed:
        return None

    with _llm_lock:
        if _llm is not None:
            return _llm
        if _llm_load_failed:
            return None

        if not os.path.exists(MODEL_PATH):
            logger.error("LLM fallback disabled — model file not found at %s", MODEL_PATH)
            _llm_load_failed = True
            return None

        try:
            from llama_cpp import Llama

            logger.info("Loading LLM fallback model (first use) from %s …", MODEL_PATH)
            _llm = Llama(
                model_path=MODEL_PATH,
                n_ctx=_N_CTX,
                verbose=False,
            )
            logger.info("LLM fallback model loaded.")
            return _llm
        except Exception as exc:
            logger.error("LLM fallback model failed to load: %s", exc, exc_info=True)
            _llm_load_failed = True
            return None


# ---------------------------------------------------------------------------
# Prompt construction
# ---------------------------------------------------------------------------

def _build_prompt(message: str, vocab: dict[str, list[str]]) -> list[dict]:
    diets = ", ".join(vocab["diet"]) or "(none)"
    cuisines = ", ".join(vocab["cuisine"]) or "(none)"
    meals = ", ".join(vocab["meal"]) or "(none)"

    system = (
        "You extract recipe-search filters from a user message. "
        "Respond with ONLY a JSON object, no prose, no markdown. "
        "The JSON has exactly these keys: diet, cuisine, meal, max_minutes, "
        "max_calories. "
        f"diet must be one of [{diets}] or null. "
        f"cuisine must be one of [{cuisines}] or null. "
        f"meal must be a list containing any of [{meals}] (empty list if none). "
        "max_minutes must be an integer number of minutes if the user implies a "
        "time limit, else null. "
        "max_calories must be an integer per-serving calorie ceiling if the user "
        "implies one (e.g. 'under 500 calories', 'low-calorie'), else null. "
        "Assign a value ONLY when the user's words directly name it or "
        "unambiguously imply it (e.g. 'no meat' -> a vegetarian diet; 'from "
        "China' -> Chinese cuisine; 'for the morning' -> breakfast). "
        "A specific dish name (e.g. 'chicken pasta', 'beef rendang') is NOT a "
        "filter — return all nulls; the search engine handles dish names itself. "
        "NEVER default the meal to 'dinner' (or any value) when the message does "
        "not clearly state a meal time — leave meal as an empty list instead. "
        "Vague descriptions of mood, feeling, texture, or occasion — such as "
        "'comforting', 'warm', 'hearty', 'light', 'for a cold night', "
        "'something nice' — do NOT imply any specific diet, cuisine, meal, or "
        "time; return null (or empty list) for those. "
        "When in doubt, return null. Returning all-null is better than guessing."
    )


    examples = [
        ("food from China",
         '{"diet": null, "cuisine": "Chinese", "meal": [], "max_minutes": null}'),
        ("I don't eat any meat",
         '{"diet": "vegetarian", "cuisine": null, "meal": [], "max_minutes": null}'),
        ("something warm and comforting for a cold night",
         '{"diet": null, "cuisine": null, "meal": [], "max_minutes": null}'),
        ("chicken pasta",
         '{"diet": null, "cuisine": null, "meal": [], "max_minutes": null}'),
        ("chocolate cake",
         '{"diet": null, "cuisine": null, "meal": [], "max_minutes": null}'),
        ("I'm really hungry",
         '{"diet": null, "cuisine": null, "meal": [], "max_minutes": null}'),
        ("something fancy for a date night",
         '{"diet": null, "cuisine": null, "meal": [], "max_minutes": null}'),
        ("a meal with 500 calories or fewer",
         '{"diet": null, "cuisine": null, "meal": [], "max_minutes": null, "max_calories": 500}'),
    ]

    messages = [{"role": "system", "content": system}]
    for ex_user, ex_json in examples:
        messages.append({"role": "user", "content": ex_user})
        messages.append({"role": "assistant", "content": ex_json})
    messages.append({"role": "user", "content": message})
    return messages


# ---------------------------------------------------------------------------
# Output parsing + validation (the trust boundary)
# ---------------------------------------------------------------------------

def _extract_json(raw: str) -> dict | None:
    if not raw:
        return None
    start = raw.find("{")
    end = raw.rfind("}")
    if start == -1 or end == -1 or end < start:
        return None
    try:
        return json.loads(raw[start : end + 1])
    except (json.JSONDecodeError, ValueError):
        return None


def _validate_against_vocab(
    parsed: dict, vocab: dict[str, list[str]]
) -> dict:
    def _match_single(value, allowed: list[str]) -> str | None:
        if not isinstance(value, str):
            return None
        low = {a.lower(): a for a in allowed}
        return low.get(value.strip().lower())

    diet = _match_single(parsed.get("diet"), vocab["diet"])
    cuisine = _match_single(parsed.get("cuisine"), vocab["cuisine"])

    meals_out: list[str] = []
    raw_meals = parsed.get("meal")
    if isinstance(raw_meals, list):
        allowed_low = {m.lower(): m for m in vocab["meal"]}
        for m in raw_meals:
            if isinstance(m, str):
                canon = allowed_low.get(m.strip().lower())
                if canon and canon not in meals_out:
                    meals_out.append(canon)

    minutes = parsed.get("max_minutes")
    if isinstance(minutes, bool):
        minutes = None
    elif isinstance(minutes, int):
        minutes = minutes if _MIN_TIME <= minutes <= _MAX_TIME else None
    else:
        minutes = None

    calories = parsed.get("max_calories")
    if isinstance(calories, bool):
        calories = None
    elif isinstance(calories, int):
        calories = calories if _MIN_CAL <= calories <= _MAX_CAL else None
    else:
        calories = None

    return {
        "diet": diet,
        "cuisine": cuisine,
        "meal": meals_out,
        "max_minutes": minutes,
        "max_calories": calories,
    }


# ---------------------------------------------------------------------------
# Public entry point
# ---------------------------------------------------------------------------

def llm_extract_slots(
    message: str, vocab: dict[str, list[str]]
) -> dict | None:
    llm = _get_llm()
    if llm is None:
        return None

    try:
        out = llm.create_chat_completion(
            messages=_build_prompt(message, vocab),
            max_tokens=_MAX_TOKENS,
            temperature=_TEMPERATURE,
        )
        raw = out["choices"][0]["message"]["content"]
    except Exception as exc:
        logger.error("LLM fallback inference failed: %s", exc, exc_info=True)
        return None

    parsed = _extract_json(raw)
    if parsed is None:
        logger.info("LLM fallback produced no parseable JSON for: %r", message)
        return None

    validated = _validate_against_vocab(parsed, vocab)

    if (
        validated["diet"] is None
        and validated["cuisine"] is None
        and not validated["meal"]
        and validated["max_minutes"] is None
        and validated["max_calories"] is None
    ):
        return None

    return validated
