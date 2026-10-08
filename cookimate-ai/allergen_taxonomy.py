# ---------------------------------------------------------------------------
# TIER A — explicit / direct name matches
# ---------------------------------------------------------------------------
TIER_A_EXPLICIT: dict[str, list[str]] = {
    # --- peanut ---
    "peanut": ["peanut"],
    "crushed peanut": ["peanut"],
    "roasted peanuts": ["peanut"],

    # --- tree-nut ---
    "almonds": ["tree-nut"],
    "cashew nuts": ["tree-nut"],
    "cashews": ["tree-nut"],
    "crushed pistachios": ["tree-nut"],
    "coconut milk": ["tree-nut"],
    "coconut leaf": ["tree-nut"],
    "grated coconut": ["tree-nut"],
    "toasted grated coconut": ["tree-nut"],

    # --- shellfish ---
    "shrimp": ["shellfish"],
    "dried shrimp": ["shellfish"],
    "fermented shrimp": ["shellfish"],
    "prawns": ["shellfish"],
    "prawn paste": ["shellfish"],
    "lobster tails": ["shellfish"],
    "belacan (shrimp paste)": ["shellfish"],
    "canned clams": ["shellfish"],

    # --- fish ---
    "fish": ["fish"],
    "fish cake": ["fish"],
    "fish steaks": ["fish"],
    "dried anchovies (ikan bilis)": ["fish"],
    "fermented fish stomach": ["fish"],
    "mackerel": ["fish"],
    "tilapia": ["fish"],
    "tuna steak": ["fish"],
    "canned tuna": ["fish"],
    "white fish fillets": ["fish"],
    "Salmon Fillet": ["fish"],
    "stingray": ["fish"],
    "seafood broth": ["fish", "shellfish"],

    # --- egg ---
    "egg": ["egg"],
    "Eggs": ["egg"],
    "egg yolks": ["egg"],
    "yellow egg noodles": ["egg"],

    # --- dairy ---
    "butter": ["dairy"],
    "unsalted butter": ["dairy"],
    "cold butter": ["dairy"],
    "buttermilk": ["dairy"],
    "Cheddar Cheese": ["dairy"],
    "Cream Cheese": ["dairy"],
    "mascarpone cheese": ["dairy"],
    "feta cheese": ["dairy"],
    "fresh mozzarella": ["dairy"],
    "parmesan cheese": ["dairy"],
    "Parmesan": ["dairy"],
    "Gruyère Cheese": ["dairy"],
    "GruyÃ¨re cheese": ["dairy"],
    "Heavy Cream": ["dairy"],
    "sour cream": ["dairy"],
    "plain yogurt": ["dairy"],
    "Greek yogurt": ["dairy"],
    "khoya": ["dairy"],
    "paneer": ["dairy"],
    "ghee": ["dairy"],
    "milk": ["dairy"],
    "full-fat milk": ["dairy"],
    "warm milk": ["dairy"],
    "vanilla ice cream": ["dairy"],
    "Chocolate Ice": ["dairy"],
    "tenzo matcha powder milk ice": ["dairy"],
    "Lemon Butter": ["dairy"],

    # --- soy ---
    "tofu": ["soy"],
    "fried tofu": ["soy"],
    "tofu puffs": ["soy"],
    "bean curd": ["soy"],
    "fermented white tofu (nam yu)": ["soy"],
    "tempeh": ["soy"],
    "spicy bean paste": ["soy"],
    "fermented black beans": ["soy"], 

    # --- gluten (wheat) ---
    "flour": ["gluten"],
    "whole wheat flour": ["gluten"],
    "bread": ["gluten"],
    "white bread": ["gluten"],
    "breadcrumbs": ["gluten"],
    "macaroni": ["gluten"],
    "pasta": ["gluten"],
    "penne pasta": ["gluten"],
    "Spaghetti": ["gluten"],
    "Lasagna Sheets": ["gluten"],
    "pizza dough": ["gluten"],
    "wonton wrappers": ["gluten"],
    "brioche buns": ["gluten", "dairy", "egg"],
    "Brioche Bun": ["gluten", "dairy", "egg"],
    "roti": ["gluten"],
    "shortcrust pastry": ["gluten", "dairy"],
    "fine semolina": ["gluten"],
    "wheat noodles": ["gluten"],
    "croutons": ["gluten"],
    "Graham Cracker Crust": ["gluten", "dairy"],

    # --- sesame ---
    "sesame oil": ["sesame"],
    "sesame seeds": ["sesame"],

    # --- strict-per-instruction compound sauces ---
    "soy sauce": ["soy", "gluten"],
    "sweet soy sauce": ["soy", "gluten"],
    "oyster sauce": ["shellfish"],
    "malt vinegar": ["gluten"],
}

# ---------------------------------------------------------------------------
# TIER B — inferred from standard/traditional recipe composition
# (kept separate from Tier A so the "why is this tagged" answer is always
# available — these are judgment calls, not literal string matches)
# ---------------------------------------------------------------------------
TIER_B_INFERRED: dict[str, list[str]] = {
    "mayonnaise": ["egg"],
    "Caesar Dressing": ["egg", "dairy", "fish"],
    "Béchamel Sauce": ["dairy", "gluten"],
}

# ---------------------------------------------------------------------------
# Deliberately excluded — flagged here so it's a documented decision, not an
# oversight. Composition varies too much across recipes to tag safely.
# ---------------------------------------------------------------------------
DELIBERATELY_EXCLUDED_NOTES = """
- muffins, ladyfinger biscuits: baked goods that MAY contain egg/dairy, but
  recipes vary (vegan versions exist). Not tagged.
- pav buns, noodles (plain), corn tortillas, vermicelli noodles: base
  starches whose allergen content depends on the specific recipe/brand
  (e.g. "noodles" could be wheat, rice, or egg noodles — "yellow egg
  noodles" is the only noodle entry specific enough to tag). Not tagged.
- glutinous rice: NOT gluten — the name refers to stickiness, not wheat
  content. Explicitly excluded to prevent a keyword-matching script from
  false-positiving on the substring "glut".
- hoisin sauce: often contains wheat in commercial recipes, but home/other
  recipes vary more than soy sauce's traditional brewing process does.
  Left untagged rather than assumed — revisit if this becomes a problem
  case during testing.
"""

ALL_TAGS: dict[str, list[str]] = {**TIER_A_EXPLICIT, **TIER_B_INFERRED}
