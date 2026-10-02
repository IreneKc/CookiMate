using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace CookiMateWeb.Pages
{
    public class EditRecipeModel : PageModel
    {
        private readonly IWebHostEnvironment _environment;
        private readonly IConfiguration _configuration;

        public EditRecipeModel(IWebHostEnvironment environment, IConfiguration configuration)
        {
            _environment = environment;
            _configuration = configuration;
        }

        [BindProperty]
        public EditRecipeInputModel Input { get; set; } = new();

        public string? SuccessMessage { get; set; }
        public string? ErrorMessage { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            string? userIdString = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrEmpty(userIdString))
            {
                return RedirectToPage("/Login");
            }

            if (!int.TryParse(userIdString, out int userId))
            {
                HttpContext.Session.Clear();
                return RedirectToPage("/Login");
            }

            try
            {
                string connectionString =
                    _configuration.GetConnectionString("DefaultConnection")
                    ?? "server=127.0.0.1;port=3306;database=cookimate;uid=root;pwd=;";

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                string recipeSql = @"
                    SELECT r.recipe_id, r.title, r.description, r.instructions, r.prep_time, r.cook_time, r.servings,
                        r.difficulty, r.image_url, n.calories
                    FROM recipes r
                    LEFT JOIN recipe_nutrition n ON n.recipe_id = r.recipe_id
                    WHERE r.recipe_id = @recipe_id AND r.created_by = @created_by
                    LIMIT 1;";

                using (var cmd = new MySqlCommand(recipeSql, connection))
                {
                    cmd.Parameters.AddWithValue("@recipe_id", id);
                    cmd.Parameters.AddWithValue("@created_by", userId);

                    using var reader = await cmd.ExecuteReaderAsync();

                    if (!await reader.ReadAsync())
                    {
                        return RedirectToPage("/MyRecipes");
                    }

                    Input.RecipeId = Convert.ToInt32(reader["recipe_id"]);
                    Input.Title = reader["title"]?.ToString() ?? "";
                    Input.Description = reader["description"]?.ToString() ?? "";
                    Input.Instructions = reader["instructions"]?.ToString() ?? "";
                    Input.PrepTime = reader["prep_time"] == DBNull.Value ? null : Convert.ToInt32(reader["prep_time"]);
                    Input.CookTime = reader["cook_time"] == DBNull.Value ? null : Convert.ToInt32(reader["cook_time"]);
                    Input.Servings = reader["servings"] == DBNull.Value ? null : Convert.ToInt32(reader["servings"]);
                    Input.Calories = reader["calories"] == DBNull.Value ? null : Convert.ToInt32(reader["calories"]);
                    Input.Difficulty = reader["difficulty"]?.ToString() ?? "medium";
                    Input.ExistingImageUrl = reader["image_url"]?.ToString();
                }

                string ingredientSql = @"
                    SELECT ri.quantity, ri.unit, i.ingredient_name
                    FROM recipe_ingredients ri
                    INNER JOIN ingredients i ON ri.ingredient_id = i.ingredient_id
                    WHERE ri.recipe_id = @recipe_id;";

                using (var cmd = new MySqlCommand(ingredientSql, connection))
                {
                    cmd.Parameters.AddWithValue("@recipe_id", id);

                    using var reader = await cmd.ExecuteReaderAsync();
                    Input.Ingredients = new List<IngredientRowInput>();

                    while (await reader.ReadAsync())
                    {
                        Input.Ingredients.Add(new IngredientRowInput
                        {
                            Quantity = reader["quantity"] == DBNull.Value ? null : reader["quantity"]?.ToString(),
                            Unit = reader["unit"] == DBNull.Value ? null : reader["unit"]?.ToString(),
                            IngredientName = reader["ingredient_name"]?.ToString()
                        });
                    }
                }

                if (!Input.Ingredients.Any())
                {
                    Input.Ingredients = new List<IngredientRowInput>
                    {
                        new IngredientRowInput(),
                        new IngredientRowInput(),
                        new IngredientRowInput()
                    };
                }

                string tagSql = @"
                    SELECT t.tag_name, t.tag_type
                    FROM recipe_tags rt
                    INNER JOIN tags t ON rt.tag_id = t.tag_id
                    WHERE rt.recipe_id = @recipe_id;";

                using (var cmd = new MySqlCommand(tagSql, connection))
                {
                    cmd.Parameters.AddWithValue("@recipe_id", id);

                    using var reader = await cmd.ExecuteReaderAsync();

                    while (await reader.ReadAsync())
                    {
                        string tagName = reader["tag_name"]?.ToString() ?? "";
                        string tagType = reader["tag_type"]?.ToString() ?? "";

                        if (tagType == "cuisine")
                            Input.Cuisine = tagName;
                        else if (tagType == "diet")
                            Input.DietType = tagName;
                        else if (tagType == "meal" && !string.IsNullOrWhiteSpace(tagName))
                            Input.MealTypes.Add(tagName);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to load recipe: {ex.Message}";
            }

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            string? userIdString = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrEmpty(userIdString))
            {
                return RedirectToPage("/Login");
            }

            if (!int.TryParse(userIdString, out int userId))
            {
                HttpContext.Session.Clear();
                return RedirectToPage("/Login");
            }

            if (Input.Ingredients == null)
            {
                Input.Ingredients = new List<IngredientRowInput>();
            }

            bool hasAtLeastOneIngredient = Input.Ingredients.Any(i => !string.IsNullOrWhiteSpace(i.IngredientName));
            if (!hasAtLeastOneIngredient)
            {
                ModelState.AddModelError("Input.Ingredients", "At least one ingredient is required.");
            }

            for (int i = 0; i < Input.Ingredients.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(Input.Ingredients[i].Quantity) ||
                    !string.IsNullOrWhiteSpace(Input.Ingredients[i].Unit) ||
                    !string.IsNullOrWhiteSpace(Input.Ingredients[i].IngredientName))
                {
                    if (string.IsNullOrWhiteSpace(Input.Ingredients[i].IngredientName))
                    {
                        ModelState.AddModelError($"Input.Ingredients[{i}].IngredientName", "Ingredient name is required.");
                    }
                }
            }

            if (!ModelState.IsValid)
            {
                ErrorMessage = "Please correct the validation errors and submit again.";
                return Page();
            }

            try
            {
                string connectionString =
                    _configuration.GetConnectionString("DefaultConnection")
                    ?? "server=127.0.0.1;port=3306;database=cookimate;uid=root;pwd=;";

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                string checkSql = "SELECT COUNT(*) FROM recipes WHERE recipe_id = @recipe_id AND created_by = @created_by;";
                using (var checkCmd = new MySqlCommand(checkSql, connection))
                {
                    checkCmd.Parameters.AddWithValue("@recipe_id", Input.RecipeId);
                    checkCmd.Parameters.AddWithValue("@created_by", userId);

                    int exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
                    if (exists == 0)
                    {
                        return RedirectToPage("/MyRecipes");
                    }
                }

                string currentImageUrl = "/images/recipes/default-food.jpg";

                string getImageSql = @"
                    SELECT image_url
                    FROM recipes
                    WHERE recipe_id = @recipe_id AND created_by = @created_by
                    LIMIT 1;";

                using (var imgCmd = new MySqlCommand(getImageSql, connection))
                {
                    imgCmd.Parameters.AddWithValue("@recipe_id", Input.RecipeId);
                    imgCmd.Parameters.AddWithValue("@created_by", userId);

                    object? result = await imgCmd.ExecuteScalarAsync();
                    if (result != null && result != DBNull.Value)
                    {
                        currentImageUrl = result.ToString()!;
                    }
                }

                string imageUrl = currentImageUrl;
                string? oldImageUrl = currentImageUrl;

                if (Input.ImageFile != null && Input.ImageFile.Length > 0)
                {
                    imageUrl = await SaveImageAsync(Input.ImageFile);
                }

                using var transaction = await connection.BeginTransactionAsync();

                try
                {
                    string updateRecipeSql = @"
                        UPDATE recipes
                        SET title = @title,
                            description = @description,
                            instructions = @instructions,
                            prep_time = @prep_time,
                            cook_time = @cook_time,
                            servings = @servings,
                            difficulty = @difficulty,
                            image_url = @image_url
                        WHERE recipe_id = @recipe_id AND created_by = @created_by;";

                    using (var cmd = new MySqlCommand(updateRecipeSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@title", NormalizeText(Input.Title));
                        cmd.Parameters.AddWithValue("@description", NormalizeText(Input.Description));
                        cmd.Parameters.AddWithValue("@instructions", NormalizeText(Input.Instructions));
                        cmd.Parameters.AddWithValue("@prep_time", (object?)Input.PrepTime ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@cook_time", (object?)Input.CookTime ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@servings", (object?)Input.Servings ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@difficulty", string.IsNullOrWhiteSpace(Input.Difficulty) ? "medium" : Input.Difficulty);
                        cmd.Parameters.AddWithValue("@image_url", imageUrl);
                        cmd.Parameters.AddWithValue("@recipe_id", Input.RecipeId);
                        cmd.Parameters.AddWithValue("@created_by", userId);

                        await cmd.ExecuteNonQueryAsync();
                    }

                    string deleteIngredientsSql = "DELETE FROM recipe_ingredients WHERE recipe_id = @recipe_id;";
                    using (var cmd = new MySqlCommand(deleteIngredientsSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@recipe_id", Input.RecipeId);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    foreach (var item in Input.Ingredients)
                    {
                        if (string.IsNullOrWhiteSpace(item.IngredientName))
                            continue;

                        string ingredientName = NormalizeIngredientLine(item.IngredientName);

                        if (string.IsNullOrWhiteSpace(ingredientName))
                            continue;

                        long ingredientId = await GetOrCreateIngredientAsync(connection, ingredientName, transaction);

                        string insertIngredientLinkSql = @"
                            INSERT INTO recipe_ingredients (recipe_id, ingredient_id, quantity, unit)
                            VALUES (@recipe_id, @ingredient_id, @quantity, @unit);";

                        using var ingredientCmd = new MySqlCommand(insertIngredientLinkSql, connection, transaction);
                        ingredientCmd.Parameters.AddWithValue("@recipe_id", Input.RecipeId);
                        ingredientCmd.Parameters.AddWithValue("@ingredient_id", ingredientId);
                        ingredientCmd.Parameters.AddWithValue("@quantity",
                            string.IsNullOrWhiteSpace(item.Quantity) ? DBNull.Value : item.Quantity.Trim());
                        ingredientCmd.Parameters.AddWithValue("@unit",
                            string.IsNullOrWhiteSpace(item.Unit) ? DBNull.Value : item.Unit.Trim());

                        await ingredientCmd.ExecuteNonQueryAsync();
                    }

                    string deleteTagsSql = "DELETE FROM recipe_tags WHERE recipe_id = @recipe_id;";
                    using (var cmd = new MySqlCommand(deleteTagsSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@recipe_id", Input.RecipeId);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    await SaveMetadataTagsAsync(connection, Input.RecipeId, Input.Cuisine, Input.DietType, Input.MealTypes, transaction);
                    
                    // Nutrition (separate table): upsert when provided, remove when cleared.
                    if (Input.Calories.HasValue)
                    {
                        string upsertNutritionSql = @"
                        INSERT INTO recipe_nutrition (recipe_id, calories)
                        VALUES (@recipe_id, @calories)
                        ON DUPLICATE KEY UPDATE calories = VALUES(calories);";
                        using var cmd = new MySqlCommand(upsertNutritionSql, connection, transaction);
                        cmd.Parameters.AddWithValue("@recipe_id", Input.RecipeId);
                        cmd.Parameters.AddWithValue("@calories", Input.Calories.Value);
                        await cmd.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        string deleteNutritionSql = "DELETE FROM recipe_nutrition WHERE recipe_id = @recipe_id;";
                        using var cmd = new MySqlCommand(deleteNutritionSql, connection, transaction);
                        cmd.Parameters.AddWithValue("@recipe_id", Input.RecipeId);
                        await cmd.ExecuteNonQueryAsync();
                    }
                    await transaction.CommitAsync();

                    if (!string.IsNullOrWhiteSpace(oldImageUrl) &&
                        !oldImageUrl.Equals(imageUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        DeleteRecipeImageFile(oldImageUrl);
                    }

                    TempData["SuccessMessage"] = "Recipe updated successfully.";
                    return RedirectToPage("/MyRecipes");
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to update recipe: {ex.Message}";
            }

            return Page();
        }

        private async Task<string> SaveImageAsync(IFormFile imageFile)
        {
            string[] allowedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };
            string extension = Path.GetExtension(imageFile.FileName).ToLowerInvariant();

            if (!allowedExtensions.Contains(extension))
            {
                throw new Exception("Only JPG, JPEG, PNG, and WEBP images are allowed.");
            }

            string uploadsFolder = Path.Combine(_environment.WebRootPath, "images", "recipes");
            Directory.CreateDirectory(uploadsFolder);

            string fileName = $"{Guid.NewGuid()}{extension}";
            string filePath = Path.Combine(uploadsFolder, fileName);

            using var stream = new FileStream(filePath, FileMode.Create);
            await imageFile.CopyToAsync(stream);

            return $"/images/recipes/{fileName}";
        }

        private static async Task<long> GetOrCreateIngredientAsync(MySqlConnection connection, string ingredientName, MySqlTransaction transaction)
        {
            string findSql = @"
                SELECT ingredient_id
                FROM ingredients
                WHERE LOWER(TRIM(ingredient_name)) = LOWER(TRIM(@ingredient_name))
                LIMIT 1;";

            using (var cmd = new MySqlCommand(findSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@ingredient_name", ingredientName);
                object? existing = await cmd.ExecuteScalarAsync();

                if (existing != null)
                    return Convert.ToInt64(existing);
            }

            string insertSql = @"
                INSERT INTO ingredients (ingredient_name)
                VALUES (@ingredient_name);
                SELECT LAST_INSERT_ID();";

            using var insertCmd = new MySqlCommand(insertSql, connection, transaction);
            insertCmd.Parameters.AddWithValue("@ingredient_name", ingredientName);
            object? result = await insertCmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }

        private static async Task SaveMetadataTagsAsync(
            MySqlConnection connection,
            long recipeId,
            string? cuisine,
            string? dietType,
            List<string>? mealTypes,
            MySqlTransaction transaction)
        {
            var metadataItems = new List<(string TagName, string TagType)>();

            if (!string.IsNullOrWhiteSpace(cuisine))
                metadataItems.Add((cuisine.Trim(), "cuisine"));

            if (!string.IsNullOrWhiteSpace(dietType))
                metadataItems.Add((dietType.Trim(), "diet"));

            if (mealTypes != null)
            {
                foreach (var meal in mealTypes)
                {
                    if (!string.IsNullOrWhiteSpace(meal))
                        metadataItems.Add((meal.Trim(), "meal"));
                }
            }

            var uniqueItems = metadataItems
                .GroupBy(x => new { Name = x.TagName.ToLowerInvariant(), x.TagType })
                .Select(g => g.First());

            foreach (var item in uniqueItems)
            {
                long tagId = await GetOrCreateTagAsync(connection, item.TagName, item.TagType, transaction);

                string insertSql = "INSERT INTO recipe_tags (recipe_id, tag_id) VALUES (@recipe_id, @tag_id);";
                using var cmd = new MySqlCommand(insertSql, connection, transaction);
                cmd.Parameters.AddWithValue("@recipe_id", recipeId);
                cmd.Parameters.AddWithValue("@tag_id", tagId);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        private static async Task<long> GetOrCreateTagAsync(MySqlConnection connection, string tagName, string tagType, MySqlTransaction transaction)
        {
            string findSql = @"
                SELECT tag_id
                FROM tags
                WHERE LOWER(TRIM(tag_name)) = LOWER(TRIM(@tag_name))
                  AND tag_type = @tag_type
                LIMIT 1;";

            using (var cmd = new MySqlCommand(findSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@tag_name", tagName);
                cmd.Parameters.AddWithValue("@tag_type", tagType);

                object? existing = await cmd.ExecuteScalarAsync();
                if (existing != null)
                    return Convert.ToInt64(existing);
            }

            string insertSql = @"
                INSERT INTO tags (tag_name, tag_type)
                VALUES (@tag_name, @tag_type);
                SELECT LAST_INSERT_ID();";

            using var insertCmd = new MySqlCommand(insertSql, connection, transaction);
            insertCmd.Parameters.AddWithValue("@tag_name", tagName);
            insertCmd.Parameters.AddWithValue("@tag_type", tagType);
            object? result = await insertCmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }

        private static string NormalizeIngredientLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string cleaned = value.Trim();
            cleaned = Regex.Replace(cleaned, @"[ \t]+", " ");
            return cleaned;
        }

        private static string NormalizeText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string cleaned = value.Trim();
            cleaned = Regex.Replace(cleaned, @"\s+", " ");
            return cleaned;
        }

        private void DeleteRecipeImageFile(string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return;

            if (imageUrl.Equals("/images/recipes/default-food.jpg", StringComparison.OrdinalIgnoreCase))
                return;

            string relativePath = imageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.Combine(_environment.WebRootPath, relativePath);

            if (System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
            }
        }

        public class EditRecipeInputModel
        {
            public int RecipeId { get; set; }

            [Required(ErrorMessage = "Recipe title is required.")]
            public string Title { get; set; } = string.Empty;

            [Required(ErrorMessage = "Description is required.")]
            public string Description { get; set; } = string.Empty;

            [Required(ErrorMessage = "Instructions are required.")]
            public string Instructions { get; set; } = string.Empty;

            public int? PrepTime { get; set; }
            public int? CookTime { get; set; }
            public int? Servings { get; set; }

            [Range(0, 3000, ErrorMessage = "Calories must be between 0 and 3000.")]
            public int? Calories { get; set; }
            public string Difficulty { get; set; } = "medium";
            public string? Cuisine { get; set; }
            public string? DietType { get; set; }
            public List<string> MealTypes { get; set; } = new();

            public string? ExistingImageUrl { get; set; }
            public IFormFile? ImageFile { get; set; }

            public List<IngredientRowInput> Ingredients { get; set; } = new();
        }

        public class IngredientRowInput
        {
            public string? Quantity { get; set; }
            public string? Unit { get; set; }
            public string? IngredientName { get; set; }
        }
    }
}