import pandas as pd
import pymysql

conn = pymysql.connect(
    host=os.getenv("DB_HOST", "127.0.0.1"),
    user=os.environ["DB_USER"],
    password=os.environ["DB_PASSWORD"],
    database=os.getenv("DB_NAME", "cookimate"),
    charset="utf8mb4"
)

cursor = conn.cursor()

df = pd.read_csv("Asian Food Ingredient.csv")

print("Dataset loaded:", df.shape)

# ---------------------------
# STEP 1: Collect ingredients
# ---------------------------

ingredient_columns = df.columns[1:]
unique_ingredients = [col.lower().strip() for col in ingredient_columns]

print("Unique ingredients:", len(unique_ingredients))

# ---------------------------
# STEP 2: Insert ingredients
# ---------------------------

ingredient_map = {}

for ingredient in unique_ingredients:

    cursor.execute(
        "INSERT INTO ingredients (ingredient_name) VALUES (%s)",
        (ingredient,)
    )

    ingredient_id = cursor.lastrowid
    ingredient_map[ingredient] = ingredient_id

conn.commit()

print("Ingredients inserted")

# ---------------------------
# STEP 3: Insert recipes
# ---------------------------

for _, row in df.iterrows():

    title = row["Menu"]

    cursor.execute(
        """
        INSERT INTO recipes (title, instructions, prep_minutes, cook_minutes, servings)
        VALUES (%s,%s,%s,%s,%s)
        """,
        (
            title,
            "Cook ingredients using Asian cooking method.",
            10,
            15,
            2
        )
    )

    recipe_id = cursor.lastrowid

    for ingredient in ingredient_columns:

        if pd.notna(row[ingredient]):

            ingredient_name = ingredient.lower().strip()
            ingredient_id = ingredient_map[ingredient_name]

            cursor.execute(
                """
                INSERT INTO recipe_ingredients (recipe_id, ingredient_id)
                VALUES (%s,%s)
                """,
                (recipe_id, ingredient_id)
            )

conn.commit()
conn.close()

print("✅ Dataset imported successfully")