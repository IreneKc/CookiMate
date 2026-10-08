import pymysql
import random

# -----------------------------
# Database connection
# -----------------------------
conn = pymysql.connect(
    host=os.getenv("DB_HOST", "127.0.0.1"),
    user=os.environ["DB_USER"],
    password=os.environ["DB_PASSWORD"],
    database=os.getenv("DB_NAME", "cookimate"),
    charset="utf8mb4",
    autocommit=False
)

cursor = conn.cursor()

# -----------------------------
# Ingredient quantity/unit rules
# -----------------------------
INGREDIENT_RULES = {
    "pork": ("200", "g"),
    "chicken": ("200", "g"),
    "beef": ("200", "g"),
    "fish": ("200", "g"),
    "shrimp": ("150", "g"),
    "prawn": ("150", "g"),
    "egg": ("2", "pcs"),
    "eggs": ("2", "pcs"),
    "tofu": ("150", "g"),
    "cabbage": ("100", "g"),
    "lettuce": ("80", "g"),
    "tomato": ("2", "pcs"),
    "potato": ("1", "pcs"),
    "onion": ("1", "pcs"),
    "spring onion": ("2", "stalks"),
    "garlic": ("3", "cloves"),
    "ginger": ("1", "tbsp"),
    "chili": ("2", "pcs"),
    "pepper": ("1", "tsp"),
    "salt": ("1", "tsp"),
    "sugar": ("1", "tsp"),
    "soy sauce": ("2", "tbsp"),
    "oyster sauce": ("1", "tbsp"),
    "vinegar": ("1", "tbsp"),
    "sesame oil": ("1", "tbsp"),
    "oil": ("2", "tbsp"),
    "rice": ("1", "cup"),
    "noodles": ("200", "g"),
    "water": ("250", "ml"),
    "stock": ("250", "ml"),
    "milk": ("200", "ml"),
}

DEFAULT_RULE = ("1", "portion")


def guess_quantity_unit(ingredient_name: str):
    name = ingredient_name.lower().strip()

    for key, value in INGREDIENT_RULES.items():
        if key in name:
            return value

    # fallback by rough category keywords
    if any(word in name for word in ["sauce", "oil", "vinegar"]):
        return ("1", "tbsp")
    if any(word in name for word in ["salt", "sugar", "pepper", "powder"]):
        return ("1", "tsp")
    if any(word in name for word in ["garlic", "shallot"]):
        return ("2", "cloves")
    if any(word in name for word in ["onion", "tomato", "potato", "egg"]):
        return ("1", "pcs")
    if any(word in name for word in ["water", "broth", "stock", "milk"]):
        return ("200", "ml")

    return DEFAULT_RULE


def guess_description(title: str, ingredients: list[str]):
    top_ings = ", ".join(ingredients[:3]) if ingredients else "selected ingredients"
    return f"{title} is an Asian-style dish prepared with {top_ings}. It is a simple and flavorful recipe suitable for everyday meals."


def guess_instructions(title: str, ingredients: list[str]):
    aromatics = [i for i in ingredients if any(k in i.lower() for k in ["garlic", "ginger", "onion", "shallot"])]
    mains = [i for i in ingredients if i not in aromatics]

    aromatics_text = ", ".join(aromatics[:2]) if aromatics else "aromatics"
    main_text = ", ".join(mains[:3]) if mains else "main ingredients"

    steps = [
        f"Prepare all ingredients needed for {title}. Wash, cut, and measure them properly.",
        f"Heat a pan or pot and sauté {aromatics_text} until fragrant.",
        f"Add {main_text} and cook until they are evenly mixed and partially cooked.",
        f"Season to taste and continue cooking until the dish is fully done.",
        f"Serve {title} hot and enjoy."
    ]
    return " ".join(steps)


def guess_prep_time(ingredients: list[str]):
    count = len(ingredients)
    if count <= 3:
        return 10
    elif count <= 6:
        return 15
    else:
        return 20


def guess_cook_time(title: str, ingredients: list[str]):
    title_lower = title.lower()

    if "soup" in title_lower:
        return 25
    if "fried" in title_lower or "stir" in title_lower:
        return 15
    if "braised" in title_lower or "stew" in title_lower:
        return 30
    if "salad" in title_lower:
        return 10

    count = len(ingredients)
    if count <= 3:
        return 10
    elif count <= 6:
        return 15
    else:
        return 20


def guess_servings(title: str, ingredients: list[str]):
    if "soup" in title.lower():
        return 3
    return 2 if len(ingredients) <= 5 else 3


def guess_difficulty(title: str, ingredients: list[str]):
    title_lower = title.lower()

    if "braised" in title_lower or "stew" in title_lower:
        return "hard"
    if len(ingredients) >= 7:
        return "hard"
    if len(ingredients) >= 5:
        return "medium"
    return "easy"


try:
    # -----------------------------------
    # 1. Get all recipes
    # -----------------------------------
    cursor.execute("""
        SELECT recipe_id, title, description, instructions, prep_time, cook_time, servings, difficulty
        FROM recipes
    """)
    recipes = cursor.fetchall()

    print(f"Found {len(recipes)} recipes")

    for recipe in recipes:
        recipe_id, title, description, instructions, prep_time, cook_time, servings, difficulty = recipe

        cursor.execute("""
            SELECT i.ingredient_name, ri.quantity, ri.unit
            FROM recipe_ingredients ri
            JOIN ingredients i ON ri.ingredient_id = i.ingredient_id
            WHERE ri.recipe_id = %s
            ORDER BY i.ingredient_name
        """, (recipe_id,))
        ingredient_rows = cursor.fetchall()

        ingredient_names = [row[0] for row in ingredient_rows]

        # -----------------------------
        # Update recipes table
        # -----------------------------
        new_description = description if description else guess_description(title, ingredient_names)
        new_instructions = instructions if instructions and instructions.strip() and instructions.strip() != "Cook ingredients using Asian cooking method." else guess_instructions(title, ingredient_names)
        new_prep_time = prep_time if prep_time is not None else guess_prep_time(ingredient_names)
        new_cook_time = cook_time if cook_time is not None else guess_cook_time(title, ingredient_names)
        new_servings = servings if servings is not None else guess_servings(title, ingredient_names)
        new_difficulty = difficulty if difficulty else guess_difficulty(title, ingredient_names)

        cursor.execute("""
            UPDATE recipes
            SET description = %s,
                instructions = %s,
                prep_time = %s,
                cook_time = %s,
                servings = %s,
                difficulty = %s
            WHERE recipe_id = %s
        """, (
            new_description,
            new_instructions,
            new_prep_time,
            new_cook_time,
            new_servings,
            new_difficulty,
            recipe_id
        ))

        # -----------------------------
        # Update recipe_ingredients
        # -----------------------------
        for ingredient_name, quantity, unit in ingredient_rows:
            if quantity is None or unit is None or str(quantity).strip() == "" or str(unit).strip() == "":
                new_quantity, new_unit = guess_quantity_unit(ingredient_name)

                cursor.execute("""
                    UPDATE recipe_ingredients ri
                    JOIN ingredients i ON ri.ingredient_id = i.ingredient_id
                    SET ri.quantity = %s,
                        ri.unit = %s
                    WHERE ri.recipe_id = %s
                      AND i.ingredient_name = %s
                """, (
                    new_quantity,
                    new_unit,
                    recipe_id,
                    ingredient_name
                ))

    conn.commit()
    print("✅ Missing recipe data filled successfully.")

except Exception as e:
    conn.rollback()
    print("❌ Error:", e)

finally:
    conn.close()