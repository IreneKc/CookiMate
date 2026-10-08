using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace CookiMateWeb.Pages
{
    public class UploadRecipeModel : PageModel
    {
        private readonly IWebHostEnvironment _environment;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;

        public UploadRecipeModel(IWebHostEnvironment environment, IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _environment = environment;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
        }

        [BindProperty]
        public UploadRecipeInputModel Input { get; set; } = new();

        public string? SuccessMessage { get; set; }
        public string? PendingReviewMessage { get; set; }
        public string? ErrorMessage { get; set; }

        public IActionResult OnGet()
        {
            string? userId = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrEmpty(userId))
            {
                return RedirectToPage("/Login");
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

            if (!int.TryParse(userIdString, out int createdBy))
            {
                ErrorMessage = "Unable to identify the current logged-in user.";
                return Page();
            }

            if (Input.Ingredients == null)
            {
                Input.Ingredients = new List<IngredientInputModel>();
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

            string normalizedTitle = NormalizeText(Input.Title, toLower: false);
            string normalizedDescription = NormalizeText(Input.Description, toLower: false);
            string normalizedInstructions = NormalizeText(Input.Instructions, toLower: false);
            string? normalizedCuisine = NormalizeText(Input.Cuisine, toLower: false, allowNull: true);
            string? normalizedDietType = NormalizeText(Input.DietType, toLower: false, allowNull: true);
            List<string> normalizedMealTypes = (Input.MealTypes ?? new List<string>())
                .Select(m => NormalizeText(m, toLower: false, allowNull: true))
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string imageUrl = "/images/recipes/default-food.jpg";
            bool duplicateSuspected = false;

            try
            {
                if (Input.ImageFile != null && Input.ImageFile.Length > 0)
                {
                    imageUrl = await SaveImageAsync(Input.ImageFile);
                }

                string connectionString =
                    _configuration.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException("Database connection string is missing.");

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                duplicateSuspected = await CheckDuplicateAsync(connection, normalizedTitle, Input.Ingredients);

                long recipeId = await InsertRecipeAsync(
                    connection,
                    normalizedTitle,
                    normalizedDescription,
                    normalizedInstructions,
                    Input.PrepTime,
                    Input.CookTime,
                    Input.Servings,
                    Input.Difficulty,
                    imageUrl,
                    createdBy,
                    duplicateSuspected
                );

                await SaveIngredientLinksAsync(connection, recipeId, Input.Ingredients);

                await DetectAllergensAsync(recipeId);

                await SaveMetadataTagsAsync(connection, recipeId, normalizedCuisine, normalizedDietType, normalizedMealTypes);

                await SaveNutritionAsync(connection, recipeId, Input.Calories);

                if (duplicateSuspected)
                {
                    PendingReviewMessage = "Recipe submitted successfully. A similar recipe was detected, so your upload is pending review.";
                }
                else
                {
                    SuccessMessage = "Recipe uploaded successfully and saved as pending approval.";
                }

                ModelState.Clear();
                Input = new UploadRecipeInputModel
                {
                    Ingredients = new List<IngredientInputModel>
                    {
                        new IngredientInputModel(),
                        new IngredientInputModel(),
                        new IngredientInputModel()
                    }
                };
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Recipe upload failed: {ex.Message}";
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

        private static async Task<bool> CheckDuplicateAsync(MySqlConnection connection, string normalizedTitle, List<IngredientInputModel> ingredients)
        {
            var newTitleWords = SplitTitleWords(normalizedTitle);
            var newIngredientSet = ingredients
                .Where(i => !string.IsNullOrWhiteSpace(i.IngredientName))
                .Select(i => NormalizeIngredientLine(i.IngredientName).ToLowerInvariant())
                .Where(i => !string.IsNullOrWhiteSpace(i))
                .Distinct()
                .ToHashSet();

            const string exactTitleSql = @"
        SELECT COUNT(*)
        FROM recipes
        WHERE LOWER(TRIM(title)) = LOWER(TRIM(@title));";

            using (var exactCmd = new MySqlCommand(exactTitleSql, connection))
            {
                exactCmd.Parameters.AddWithValue("@title", normalizedTitle);

                int exactCount = Convert.ToInt32(await exactCmd.ExecuteScalarAsync());
                if (exactCount > 0)
                {
                    return true;
                }
            }

            List<CandidateRecipe> candidates = new();

            if (newTitleWords.Count == 0)
            {
                return false;
            }

            var keywordConditions = new List<string>();
            using var candidateCmd = new MySqlCommand();
            candidateCmd.Connection = connection;

            int keywordIndex = 0;
            foreach (string word in newTitleWords)
            {
                if (word.Length < 3)
                    continue;

                string paramName = $"@kw{keywordIndex}";
                keywordConditions.Add($"LOWER(title) LIKE CONCAT('%', {paramName}, '%')");
                candidateCmd.Parameters.AddWithValue(paramName, word.ToLowerInvariant());
                keywordIndex++;
            }

            if (keywordConditions.Count == 0)
            {
                return false;
            }

            candidateCmd.CommandText = $@"
                SELECT recipe_id, title
                FROM recipes
                WHERE status IN ('pending', 'approved')
                  AND ({string.Join(" OR ", keywordConditions)})
                LIMIT 20;";

            using (var reader = await candidateCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    candidates.Add(new CandidateRecipe
                    {
                        RecipeId = reader.GetInt64("recipe_id"),
                        Title = reader["title"]?.ToString() ?? string.Empty
                    });
                }
            }

            foreach (var candidate in candidates)
            {
                string candidateTitle = NormalizeText(candidate.Title, toLower: false) ?? string.Empty;

                double titleSimilarity = CalculateWordSetSimilarity(
                    newTitleWords,
                    SplitTitleWords(candidateTitle)
                );

                if (titleSimilarity < 0.50)
                    continue;

                HashSet<string> candidateIngredients = await GetRecipeIngredientSetAsync(connection, candidate.RecipeId);

                double ingredientSimilarity = CalculateWordSetSimilarity(
                    newIngredientSet,
                    candidateIngredients
                );

                if (titleSimilarity >= 0.70 && ingredientSimilarity >= 0.60)
                {
                    return true;
                }
            }

            return false;
        }

        private static HashSet<string> SplitTitleWords(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return new HashSet<string>();

            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "the", "of", "with", "to", "for",
        "recipe", "homemade", "easy", "simple", "style"
    };

            return Regex.Split(title.ToLowerInvariant(), @"[^a-z0-9]+")
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Where(w => w.Length >= 2)
                .Where(w => !stopWords.Contains(w))
                .ToHashSet();
        }

        private static double CalculateWordSetSimilarity(HashSet<string> set1, HashSet<string> set2)
        {
            if (set1.Count == 0 && set2.Count == 0)
                return 1.0;

            if (set1.Count == 0 || set2.Count == 0)
                return 0.0;

            int intersection = set1.Intersect(set2).Count();
            int union = set1.Union(set2).Count();

            return union == 0 ? 0.0 : (double)intersection / union;
        }

        private static async Task<HashSet<string>> GetRecipeIngredientSetAsync(MySqlConnection connection, long recipeId)
        {
            const string ingredientSql = @"
        SELECT i.ingredient_name
        FROM recipe_ingredients ri
        INNER JOIN ingredients i ON ri.ingredient_id = i.ingredient_id
        WHERE ri.recipe_id = @recipe_id;";

            using var cmd = new MySqlCommand(ingredientSql, connection);
            cmd.Parameters.AddWithValue("@recipe_id", recipeId);

            HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string ingredientName = reader["ingredient_name"]?.ToString() ?? string.Empty;
                ingredientName = NormalizeIngredientLine(ingredientName).ToLowerInvariant();

                if (!string.IsNullOrWhiteSpace(ingredientName))
                {
                    result.Add(ingredientName);
                }
            }

            return result;
        }

        private class CandidateRecipe
        {
            public long RecipeId { get; set; }
            public string Title { get; set; } = string.Empty;
        }

        private static async Task<long> InsertRecipeAsync(
            MySqlConnection connection,
            string title,
            string description,
            string instructions,
            int? prepTime,
            int? cookTime,
            int? servings,
            string difficulty,
            string imageUrl,
            int createdBy,
            bool duplicateSuspected)
        {
            string insertSql = @"
                INSERT INTO recipes
                (
                    title,
                    description,
                    instructions,
                    prep_time,
                    cook_time,
                    servings,
                    difficulty,
                    image_url,
                    status,
                    is_duplicate_suspected,
                    created_by
                )
                VALUES
                (
                    @title,
                    @description,
                    @instructions,
                    @prep_time,
                    @cook_time,
                    @servings,
                    @difficulty,
                    @image_url,
                    @status,
                    @is_duplicate_suspected,
                    @created_by
                );
                SELECT LAST_INSERT_ID();";

            using var cmd = new MySqlCommand(insertSql, connection);
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@description", description);
            cmd.Parameters.AddWithValue("@instructions", instructions);
            cmd.Parameters.AddWithValue("@prep_time", (object?)prepTime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cook_time", (object?)cookTime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@servings", (object?)servings ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@difficulty", string.IsNullOrWhiteSpace(difficulty) ? "medium" : difficulty);
            cmd.Parameters.AddWithValue("@image_url", imageUrl);
            cmd.Parameters.AddWithValue("@status", "pending");
            cmd.Parameters.AddWithValue("@is_duplicate_suspected", duplicateSuspected ? 1 : 0);
            cmd.Parameters.AddWithValue("@created_by", createdBy);

            object? result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }

        private static async Task SaveIngredientLinksAsync(
            MySqlConnection connection,
            long recipeId,
            List<IngredientInputModel> ingredients)
        {
            if (ingredients == null || !ingredients.Any())
                return;

            foreach (var item in ingredients)
            {
                if (string.IsNullOrWhiteSpace(item.IngredientName))
                    continue;

                string ingredientName = NormalizeIngredientLine(item.IngredientName);

                if (string.IsNullOrWhiteSpace(ingredientName))
                    continue;

                long ingredientId = await GetOrCreateIngredientAsync(connection, ingredientName);

                string checkSql = @"
                    SELECT COUNT(*)
                    FROM recipe_ingredients
                    WHERE recipe_id = @recipe_id AND ingredient_id = @ingredient_id;";

                using (var checkCmd = new MySqlCommand(checkSql, connection))
                {
                    checkCmd.Parameters.AddWithValue("@recipe_id", recipeId);
                    checkCmd.Parameters.AddWithValue("@ingredient_id", ingredientId);

                    int exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
                    if (exists > 0)
                    {
                        continue;
                    }
                }

                string insertSql = @"
                    INSERT INTO recipe_ingredients (recipe_id, ingredient_id, quantity, unit)
                    VALUES (@recipe_id, @ingredient_id, @quantity, @unit);";

                using var insertCmd = new MySqlCommand(insertSql, connection);
                insertCmd.Parameters.AddWithValue("@recipe_id", recipeId);
                insertCmd.Parameters.AddWithValue("@ingredient_id", ingredientId);
                insertCmd.Parameters.AddWithValue("@quantity",
                    string.IsNullOrWhiteSpace(item.Quantity) ? DBNull.Value : item.Quantity.Trim());
                insertCmd.Parameters.AddWithValue("@unit",
                    string.IsNullOrWhiteSpace(item.Unit) ? DBNull.Value : item.Unit.Trim());

                await insertCmd.ExecuteNonQueryAsync();
            }
        }

        private static string NormalizeIngredientLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string cleaned = value.Trim();
            cleaned = Regex.Replace(cleaned, @"[ \t]+", " ");
            return cleaned;
        }

        private static async Task<long> GetOrCreateIngredientAsync(MySqlConnection connection, string ingredientName)
        {
            string findSql = @"
                SELECT ingredient_id
                FROM ingredients
                WHERE LOWER(TRIM(ingredient_name)) = LOWER(TRIM(@ingredient_name))
                LIMIT 1;";

            using (var findCmd = new MySqlCommand(findSql, connection))
            {
                findCmd.Parameters.AddWithValue("@ingredient_name", ingredientName);
                object? existing = await findCmd.ExecuteScalarAsync();

                if (existing != null)
                {
                    return Convert.ToInt64(existing);
                }
            }

            string insertSql = @"
                INSERT INTO ingredients (ingredient_name)
                VALUES (@ingredient_name);
                SELECT LAST_INSERT_ID();";

            using var insertCmd = new MySqlCommand(insertSql, connection);
            insertCmd.Parameters.AddWithValue("@ingredient_name", ingredientName);

            object? result = await insertCmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }

        private async Task DetectAllergensAsync(long recipeId)
        {
            try
            {
                var http = _httpClientFactory.CreateClient("CookiMateApi");
                http.Timeout = TimeSpan.FromSeconds(10);
                await http.PostAsync($"/allergen/detect/{recipeId}", null);
            }
            catch (Exception)
            {

            }
        }

        private static async Task SaveMetadataTagsAsync(
            MySqlConnection connection,
            long recipeId,
            string? cuisine,
            string? dietType,
            List<string>? mealTypes)
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
                .Select(g => g.First())
                .ToList();

            foreach (var item in uniqueItems)
            {
                long tagId = await GetOrCreateTagAsync(connection, item.TagName, item.TagType);
                await LinkRecipeTagAsync(connection, recipeId, tagId);
            }
        }

        private static async Task SaveNutritionAsync(MySqlConnection connection, long recipeId, int? calories)
        {
            if (calories is null)
                return;

            const string sql = @"
        INSERT INTO recipe_nutrition (recipe_id, calories)
        VALUES (@recipe_id, @calories)
        ON DUPLICATE KEY UPDATE calories = VALUES(calories);";

            using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@recipe_id", recipeId);
            cmd.Parameters.AddWithValue("@calories", calories.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<long> GetOrCreateTagAsync(MySqlConnection connection, string tagName, string tagType)
        {
            string findSql = @"
                SELECT tag_id
                FROM tags
                WHERE LOWER(TRIM(tag_name)) = LOWER(TRIM(@tag_name))
                  AND tag_type = @tag_type
                LIMIT 1;";

            using (var findCmd = new MySqlCommand(findSql, connection))
            {
                findCmd.Parameters.AddWithValue("@tag_name", tagName);
                findCmd.Parameters.AddWithValue("@tag_type", tagType);

                object? existing = await findCmd.ExecuteScalarAsync();
                if (existing != null)
                {
                    return Convert.ToInt64(existing);
                }
            }

            string insertSql = @"
                INSERT INTO tags (tag_name, tag_type)
                VALUES (@tag_name, @tag_type);
                SELECT LAST_INSERT_ID();";

            using var insertCmd = new MySqlCommand(insertSql, connection);
            insertCmd.Parameters.AddWithValue("@tag_name", tagName);
            insertCmd.Parameters.AddWithValue("@tag_type", tagType);

            object? result = await insertCmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }

        private static async Task LinkRecipeTagAsync(MySqlConnection connection, long recipeId, long tagId)
        {
            string checkSql = @"
                SELECT COUNT(*)
                FROM recipe_tags
                WHERE recipe_id = @recipe_id AND tag_id = @tag_id;";

            using (var checkCmd = new MySqlCommand(checkSql, connection))
            {
                checkCmd.Parameters.AddWithValue("@recipe_id", recipeId);
                checkCmd.Parameters.AddWithValue("@tag_id", tagId);

                int exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
                if (exists > 0)
                {
                    return;
                }
            }

            string insertSql = @"
                INSERT INTO recipe_tags (recipe_id, tag_id)
                VALUES (@recipe_id, @tag_id);";

            using var insertCmd = new MySqlCommand(insertSql, connection);
            insertCmd.Parameters.AddWithValue("@recipe_id", recipeId);
            insertCmd.Parameters.AddWithValue("@tag_id", tagId);
            await insertCmd.ExecuteNonQueryAsync();
        }

        private static string? NormalizeText(string? value, bool toLower = false, bool allowNull = false)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return allowNull ? null : string.Empty;
            }

            string cleaned = value.Trim();
            cleaned = Regex.Replace(cleaned, @"\s+", " ");

            if (toLower)
            {
                cleaned = cleaned.ToLowerInvariant();
            }

            return cleaned;
        }

        public class UploadRecipeInputModel
        {
            [Required(ErrorMessage = "Recipe title is required.")]
            [StringLength(255, ErrorMessage = "Recipe title cannot exceed 255 characters.")]
            public string Title { get; set; } = string.Empty;

            [Required(ErrorMessage = "Description is required.")]
            public string Description { get; set; } = string.Empty;

            public List<IngredientInputModel> Ingredients { get; set; } = new()
            {
                new IngredientInputModel(),
                new IngredientInputModel(),
                new IngredientInputModel()
            };

            [Required(ErrorMessage = "Instructions are required.")]
            public string Instructions { get; set; } = string.Empty;

            [Range(0, int.MaxValue, ErrorMessage = "Prep time must be 0 or above.")]
            public int? PrepTime { get; set; }

            [Range(0, int.MaxValue, ErrorMessage = "Cook time must be 0 or above.")]
            public int? CookTime { get; set; }

            [Range(1, int.MaxValue, ErrorMessage = "Servings must be at least 1.")]
            public int? Servings { get; set; }

            [Range(0, 3000, ErrorMessage = "Calories must be between 0 and 3000.")]
            public int? Calories { get; set; }

            [Required(ErrorMessage = "Difficulty is required.")]
            public string Difficulty { get; set; } = "medium";

            public string? Cuisine { get; set; }
            public string? DietType { get; set; }
            public List<string> MealTypes { get; set; } = new();

            public IFormFile? ImageFile { get; set; }
        }

        public class IngredientInputModel
        {
            public string? Quantity { get; set; }
            public string? Unit { get; set; }
            public string? IngredientName { get; set; }
        }
    }
}