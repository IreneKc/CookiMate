from __future__ import annotations

import re

_NON_ALNUM = re.compile(r"[^a-zA-Z0-9\s]")
_MULTISPACE = re.compile(r"\s+")


def preprocess(value: str | None) -> str:
    """Lowercase, strip punctuation, collapse whitespace."""
    value = (value or "").lower().strip()
    value = _NON_ALNUM.sub(" ", value)
    return _MULTISPACE.sub(" ", value).strip()


def tokenize(value: str | None) -> list[str]:
    """Whitespace tokens of the preprocessed text (empty tokens removed)."""
    return [tok for tok in preprocess(value).split() if tok]


def build_recipe_text(
    title: str,
    description: str | None,
    ingredients: list[str],
    instructions: str | None,
) -> str:

    ing_text = " ".join(ingredients)
    return (
        f"{title}\n"
        f"{description or ''}\n"
        f"{ing_text}\n{ing_text}\n{ing_text}\n"
        f"{instructions or ''}"
    )


def build_clustering_text(
    title: str,
    description: str | None,
    ingredients: list[str],
    instructions: str | None,
) -> str:

    ing_text = " ".join(ingredients)
    return (
        f"{title}\n"
        f"{description or ''}\n"
        f"{ing_text}\n"
        f"{instructions or ''}"
    )