using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using System.ComponentModel.DataAnnotations;

namespace CookiMateWeb.Pages
{
    public class ViewRecipeModel : PageModel
    {
        private readonly IConfiguration _configuration;

        public ViewRecipeModel(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public RecipeDetailsViewModel? Recipe { get; set; }
        public List<IngredientViewModel> Ingredients { get; set; } = new();
        public List<string> Tags { get; set; } = new();
        public List<string> RecipeAllergens { get; set; } = new();
        public List<string> MatchedAllergens { get; set; } = new();
        public List<ReviewViewModel> Reviews { get; set; } = new();

        public string? ErrorMessage { get; set; }
        public string? SuccessMessage { get; set; }

        public bool IsLoggedIn { get; set; }
        public bool IsFavorited { get; set; }

        public bool IsManageReviewMode { get; set; }
        public bool CanShowUserActions { get; set; } = true;

        [BindProperty(SupportsGet = true)]
        public string? From { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? Q { get; set; }

        [BindProperty(SupportsGet = true)]
        [FromQuery(Name = "ingredients")]
        public string? SearchIngredients { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? DietType { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? Cuisine { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? TopK { get; set; }

        [BindProperty]
        public int? RatingInput { get; set; }

        [BindProperty]
        public string? CommentInput { get; set; }

        //[BindProperty]
        //[Required(ErrorMessage = "Meal plan title is required.")]
        //[StringLength(150, ErrorMessage = "Meal plan title cannot exceed 150 characters.")]
        //public string? MealPlanTitleInput { get; set; }

        [BindProperty]
        [Required(ErrorMessage = "Please select a meal plan date.")]
        [DataType(DataType.Date)]
        public DateTime? MealPlanDateInput { get; set; }

        [BindProperty]
        [Required(ErrorMessage = "Please select a meal type.")]
        public string? MealTypeInput { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? Status { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? P { get; set; }

        private bool IsAdmin()
        {
            string? userRole = HttpContext.Session.GetString("UserRole");
            return string.Equals(userRole, "admin", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            if (id <= 0)
            {
                ErrorMessage = "Invalid recipe ID.";
                return Page();
            }

            MealPlanDateInput ??= DateTime.Today;

            await LoadRecipeDetailsAsync(id);
            return Page();
        }

        public async Task<IActionResult> OnPostFavoriteAsync(int id)
        {
            if (id <= 0)
            {
                ErrorMessage = "Invalid recipe ID.";
                await LoadRecipeDetailsAsync(id);
                return Page();
            }

            string? userIdString = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrEmpty(userIdString) || !int.TryParse(userIdString, out int userId))
            {
                return RedirectToPage("/Login");
            }

            try
            {
                string connectionString =
                    _configuration.GetConnectionString("DefaultConnection")
                    ?? _configuration.GetConnectionString("Default")
                    ?? throw new InvalidOperationException("Database connection string is missing.");

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                string checkSql = @"
                    SELECT COUNT(*)
                    FROM favorites
                    WHERE user_id = @user_id AND recipe_id = @recipe_id;";

                using (var checkCmd = new MySqlCommand(checkSql, connection))
                {
                    checkCmd.Parameters.AddWithValue("@user_id", userId);
                    checkCmd.Parameters.AddWithValue("@recipe_id", id);

                    int exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

                    if (exists == 0)
                    {
                        string insertSql = @"
                            INSERT INTO favorites (user_id, recipe_id)
                            VALUES (@user_id, @recipe_id);";

                        using var insertCmd = new MySqlCommand(insertSql, connection);
                        insertCmd.Parameters.AddWithValue("@user_id", userId);
                        insertCmd.Parameters.AddWithValue("@recipe_id", id);
                        await insertCmd.ExecuteNonQueryAsync();

                        SuccessMessage = "Recipe saved to favorites.";
                    }
                    else
                    {
                        SuccessMessage = "Recipe is already in your favorites.";
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to save favorite: {ex.Message}";
            }

            await LoadRecipeDetailsAsync(id);
            return Page();
        }

        public async Task<IActionResult> OnPostCreateMealPlanAsync(int id)
        {
            if (id <= 0)
            {
                ErrorMessage = "Invalid recipe ID.";
                await LoadRecipeDetailsAsync(id);
                return Page();
            }

            string? userIdString = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrEmpty(userIdString) || !int.TryParse(userIdString, out int userId))
            {
                return RedirectToPage("/Login");
            }

            //if (string.IsNullOrWhiteSpace(MealPlanTitleInput))
            //{
            //    ErrorMessage = "Meal plan title is required.";
            //    await LoadRecipeDetailsAsync(id);
            //    return Page();
            //}

            if (!MealPlanDateInput.HasValue)
            {
                ErrorMessage = "Please select a meal plan date.";
                await LoadRecipeDetailsAsync(id);
                return Page();
            }

            var allowedMealTypes = new[] { "breakfast", "lunch", "dinner", "snack" };
            if (string.IsNullOrWhiteSpace(MealTypeInput) || !allowedMealTypes.Contains(MealTypeInput.Trim().ToLower()))
            {
                ErrorMessage = "Please select a valid meal type.";
                await LoadRecipeDetailsAsync(id);
                return Page();
            }

            try
            {
                string connectionString =
                    _configuration.GetConnectionString("DefaultConnection")
                    ?? _configuration.GetConnectionString("Default")
                    ?? throw new InvalidOperationException("Database connection string is missing.");

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                int planId;

                const string findPlanSql = @"
                    SELECT plan_id
                    FROM meal_plans
                    WHERE created_by = @created_by
                      AND date = @date
                    LIMIT 1;";

                using (var findPlanCmd = new MySqlCommand(findPlanSql, connection))
                {
                    findPlanCmd.Parameters.AddWithValue("@created_by", userId);
                    findPlanCmd.Parameters.AddWithValue("@date", MealPlanDateInput.Value.Date);

                    object? existingPlanId = await findPlanCmd.ExecuteScalarAsync();

                    if (existingPlanId != null)
                    {
                        planId = Convert.ToInt32(existingPlanId);

                        //const string updatePlanTitleSql = @"
                        //    UPDATE meal_plans
                        //    SET title = @title
                        //    WHERE plan_id = @plan_id;";

                        //using var updatePlanCmd = new MySqlCommand(updatePlanTitleSql, connection);
                        //updatePlanCmd.Parameters.AddWithValue("@title", MealPlanTitleInput.Trim());
                        //updatePlanCmd.Parameters.AddWithValue("@plan_id", planId);
                        //await updatePlanCmd.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        const string insertPlanSql = @"
                            INSERT INTO meal_plans (date, created_by, created_at)
                            VALUES (@date, @created_by, NOW());
                            SELECT LAST_INSERT_ID();";

                        using var insertPlanCmd = new MySqlCommand(insertPlanSql, connection);
                        //insertPlanCmd.Parameters.AddWithValue("@title", MealPlanTitleInput.Trim());
                        insertPlanCmd.Parameters.AddWithValue("@date", MealPlanDateInput.Value.Date);
                        insertPlanCmd.Parameters.AddWithValue("@created_by", userId);

                        planId = Convert.ToInt32(await insertPlanCmd.ExecuteScalarAsync());
                    }
                }

                const string checkMealRecipeSql = @"
                    SELECT COUNT(*)
                    FROM meal_plan_recipes
                    WHERE plan_id = @plan_id
                      AND recipe_id = @recipe_id
                      AND meal_type = @meal_type;";

                using (var checkMealRecipeCmd = new MySqlCommand(checkMealRecipeSql, connection))
                {
                    checkMealRecipeCmd.Parameters.AddWithValue("@plan_id", planId);
                    checkMealRecipeCmd.Parameters.AddWithValue("@recipe_id", id);
                    checkMealRecipeCmd.Parameters.AddWithValue("@meal_type", MealTypeInput.Trim().ToLower());

                    int exists = Convert.ToInt32(await checkMealRecipeCmd.ExecuteScalarAsync());

                    if (exists > 0)
                    {
                        SuccessMessage = $"This recipe is already added to {MealTypeInput.Trim().ToLower()} on {MealPlanDateInput.Value:dd MMM yyyy}.";
                        await LoadRecipeDetailsAsync(id);
                        return Page();
                    }
                }

                const string insertMealRecipeSql = @"
                    INSERT INTO meal_plan_recipes (plan_id, recipe_id, meal_type)
                    VALUES (@plan_id, @recipe_id, @meal_type);";

                using (var insertMealRecipeCmd = new MySqlCommand(insertMealRecipeSql, connection))
                {
                    insertMealRecipeCmd.Parameters.AddWithValue("@plan_id", planId);
                    insertMealRecipeCmd.Parameters.AddWithValue("@recipe_id", id);
                    insertMealRecipeCmd.Parameters.AddWithValue("@meal_type", MealTypeInput.Trim().ToLower());

                    await insertMealRecipeCmd.ExecuteNonQueryAsync();
                }

                SuccessMessage = $"Recipe added to your meal plan for {MealPlanDateInput.Value:dd MMM yyyy} ({MealTypeInput.Trim().ToLower()}).";

                //MealPlanTitleInput = null;
                MealPlanDateInput = DateTime.Today;
                MealTypeInput = null;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to create meal plan: {ex.Message}";
            }

            await LoadRecipeDetailsAsync(id);
            return Page();
        }

        public async Task<IActionResult> OnPostReviewAsync(int id)
        {
            if (id <= 0)
            {
                ErrorMessage = "Invalid recipe ID.";
                await LoadRecipeDetailsAsync(id);
                return Page();
            }

            string? userIdString = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrEmpty(userIdString) || !int.TryParse(userIdString, out int userId))
            {
                return RedirectToPage("/Login");
            }

            if (!RatingInput.HasValue || RatingInput < 1 || RatingInput > 5)
            {
                ErrorMessage = "Rating must be between 1 and 5.";
                await LoadRecipeDetailsAsync(id);
                return Page();
            }

            if (string.IsNullOrWhiteSpace(CommentInput))
            {
                ErrorMessage = "Comment is required.";
                await LoadRecipeDetailsAsync(id);
                return Page();
            }

            try
            {
                string connectionString =
                    _configuration.GetConnectionString("DefaultConnection")
                    ?? _configuration.GetConnectionString("Default")
                    ?? throw new InvalidOperationException("Database connection string is missing.");

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                string checkReviewSql = @"
                    SELECT review_id
                    FROM recipe_reviews
                    WHERE recipe_id = @recipe_id AND created_by = @created_by
                    LIMIT 1;";

                object? existingReviewId;

                using (var checkCmd = new MySqlCommand(checkReviewSql, connection))
                {
                    checkCmd.Parameters.AddWithValue("@recipe_id", id);
                    checkCmd.Parameters.AddWithValue("@created_by", userId);
                    existingReviewId = await checkCmd.ExecuteScalarAsync();
                }

                if (existingReviewId != null)
                {
                    string updateSql = @"
                        UPDATE recipe_reviews
                        SET rating = @rating,
                            comment = @comment,
                            created_at = NOW()
                        WHERE review_id = @review_id;";

                    using var updateCmd = new MySqlCommand(updateSql, connection);
                    updateCmd.Parameters.AddWithValue("@rating", RatingInput.Value);
                    updateCmd.Parameters.AddWithValue("@comment", CommentInput.Trim());
                    updateCmd.Parameters.AddWithValue("@review_id", Convert.ToInt32(existingReviewId));
                    await updateCmd.ExecuteNonQueryAsync();

                    SuccessMessage = "Your review has been updated.";
                }
                else
                {
                    string insertSql = @"
                        INSERT INTO recipe_reviews (rating, comment, created_at, created_by, recipe_id)
                        VALUES (@rating, @comment, NOW(), @created_by, @recipe_id);";

                    using var insertCmd = new MySqlCommand(insertSql, connection);
                    insertCmd.Parameters.AddWithValue("@rating", RatingInput.Value);
                    insertCmd.Parameters.AddWithValue("@comment", CommentInput.Trim());
                    insertCmd.Parameters.AddWithValue("@created_by", userId);
                    insertCmd.Parameters.AddWithValue("@recipe_id", id);
                    await insertCmd.ExecuteNonQueryAsync();

                    SuccessMessage = "Your review has been posted.";
                }

                RatingInput = null;
                CommentInput = null;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to save review: {ex.Message}";
            }

            await LoadRecipeDetailsAsync(id);
            return Page();
        }

        private async Task LoadRecipeDetailsAsync(int id)
        {
            Recipe = null;
            Ingredients = new List<IngredientViewModel>();
            Tags = new List<string>();
            Reviews = new List<ReviewViewModel>();
            IsFavorited = false;
            IsLoggedIn = false;

            string? userIdString = HttpContext.Session.GetString("UserID");
            int? userId = null;

            if (!string.IsNullOrEmpty(userIdString) && int.TryParse(userIdString, out int parsedUserId))
            {
                IsLoggedIn = true;
                userId = parsedUserId;
            }

            if (!MealPlanDateInput.HasValue)
            {
                MealPlanDateInput = DateTime.Today;
            }

            try
            {
                string connectionString =
                    _configuration.GetConnectionString("DefaultConnection")
                    ?? _configuration.GetConnectionString("Default")
                    ?? throw new InvalidOperationException("Database connection string is missing.");

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();
                bool isAdmin = IsAdmin();
                bool allowAdminManageView = isAdmin && string.Equals(From, "managerecipe", StringComparison.OrdinalIgnoreCase);
                IsManageReviewMode = allowAdminManageView;

                string recipeSql = allowAdminManageView
                    ? @"
                        SELECT r.recipe_id,
                               r.title,
                               r.description,
                               r.instructions,
                               r.prep_time,
                               r.cook_time,
                               r.servings,
                               r.difficulty,
                               r.image_url,
                               n.calories,
                               AVG(rr.rating) AS avg_rating
                        FROM recipes r
                        LEFT JOIN recipe_reviews rr ON r.recipe_id = rr.recipe_id
                        LEFT JOIN recipe_nutrition n ON n.recipe_id = r.recipe_id
                        WHERE r.recipe_id = @recipe_id
                        GROUP BY r.recipe_id, r.title, r.description, r.instructions,
                                 r.prep_time, r.cook_time, r.servings, r.difficulty, r.image_url, n.calories
                        LIMIT 1;"
                    : @"
                        SELECT r.recipe_id,
                               r.title,
                               r.description,
                               r.instructions,
                               r.prep_time,
                               r.cook_time,
                               r.servings,
                               r.difficulty,
                               r.image_url,
                               n.calories,
                               AVG(rr.rating) AS avg_rating
                        FROM recipes r
                        LEFT JOIN recipe_reviews rr ON r.recipe_id = rr.recipe_id
                        LEFT JOIN recipe_nutrition n ON n.recipe_id = r.recipe_id
                        WHERE r.recipe_id = @recipe_id
                          AND r.status = 'approved'
                        GROUP BY r.recipe_id, r.title, r.description, r.instructions,
                                 r.prep_time, r.cook_time, r.servings, r.difficulty, r.image_url, n.calories
                        LIMIT 1;";

                using (var recipeCmd = new MySqlCommand(recipeSql, connection))
                {
                    recipeCmd.Parameters.AddWithValue("@recipe_id", id);

                    using var reader = await recipeCmd.ExecuteReaderAsync();

                    if (!await reader.ReadAsync())
                    {
                        ErrorMessage = "Recipe not found.";
                        return;
                    }

                    Recipe = new RecipeDetailsViewModel
                    {
                        RecipeId = Convert.ToInt32(reader["recipe_id"]),
                        Title = reader["title"]?.ToString() ?? "Untitled Recipe",
                        Description = reader["description"] == DBNull.Value ? null : reader["description"]?.ToString(),
                        Instructions = reader["instructions"] == DBNull.Value ? null : reader["instructions"]?.ToString(),
                        PrepTime = reader["prep_time"] == DBNull.Value ? null : Convert.ToInt32(reader["prep_time"]),
                        CookTime = reader["cook_time"] == DBNull.Value ? null : Convert.ToInt32(reader["cook_time"]),
                        Servings = reader["servings"] == DBNull.Value ? null : Convert.ToInt32(reader["servings"]),
                        Calories = reader["calories"] == DBNull.Value ? null : Convert.ToInt32(reader["calories"]),
                        Difficulty = reader["difficulty"] == DBNull.Value ? null : reader["difficulty"]?.ToString(),
                        ImageUrl = reader["image_url"] == DBNull.Value ? null : reader["image_url"]?.ToString(),
                        AverageRating = reader["avg_rating"] == DBNull.Value ? null : Convert.ToDecimal(reader["avg_rating"])
                    };

                    CanShowUserActions = !IsManageReviewMode;
                }

                string ingredientSql = @"
                    SELECT ri.quantity, ri.unit, i.ingredient_name
                    FROM recipe_ingredients ri
                    INNER JOIN ingredients i ON ri.ingredient_id = i.ingredient_id
                    WHERE ri.recipe_id = @recipe_id
                    ORDER BY i.ingredient_name ASC;";

                using (var ingredientCmd = new MySqlCommand(ingredientSql, connection))
                {
                    ingredientCmd.Parameters.AddWithValue("@recipe_id", id);

                    using var reader = await ingredientCmd.ExecuteReaderAsync();

                    while (await reader.ReadAsync())
                    {
                        Ingredients.Add(new IngredientViewModel
                        {
                            Quantity = reader["quantity"] == DBNull.Value ? null : reader["quantity"]?.ToString(),
                            Unit = reader["unit"] == DBNull.Value ? null : reader["unit"]?.ToString(),
                            IngredientName = reader["ingredient_name"]?.ToString() ?? ""
                        });
                    }
                }

                string tagSql = @"
                    SELECT t.tag_name, t.tag_type
                    FROM recipe_tags rt
                    INNER JOIN tags t ON rt.tag_id = t.tag_id
                    WHERE rt.recipe_id = @recipe_id;";

                using (var tagCmd = new MySqlCommand(tagSql, connection))
                {
                    tagCmd.Parameters.AddWithValue("@recipe_id", id);

                    using var reader = await tagCmd.ExecuteReaderAsync();

                    while (await reader.ReadAsync())
                    {
                        string tagName = reader["tag_name"]?.ToString() ?? "";
                        string tagType = reader["tag_type"]?.ToString() ?? "";

                        if (tagType == "cuisine")
                            Recipe!.Cuisine = tagName;
                        else if (tagType == "diet")
                            Recipe!.DietType = tagName;
                        else if (tagType == "meal" && !string.IsNullOrWhiteSpace(tagName))
                            Recipe!.MealTypes.Add(tagName);
                        else if (tagType == "allergen" && !string.IsNullOrWhiteSpace(tagName))
                            RecipeAllergens.Add(tagName);
                        else if (!string.IsNullOrWhiteSpace(tagName))
                            Tags.Add(tagName);
                    }
                }

                string reviewSql = @"
                    SELECT rr.review_id,
                           rr.rating,
                           rr.comment,
                           rr.created_at,
                           rr.created_by,
                           u.name AS reviewer_name
                    FROM recipe_reviews rr
                    INNER JOIN users u ON rr.created_by = u.user_id
                    WHERE rr.recipe_id = @recipe_id
                    ORDER BY rr.created_at DESC;";

                using (var reviewCmd = new MySqlCommand(reviewSql, connection))
                {
                    reviewCmd.Parameters.AddWithValue("@recipe_id", id);

                    using var reader = await reviewCmd.ExecuteReaderAsync();

                    while (await reader.ReadAsync())
                    {
                        int createdBy = Convert.ToInt32(reader["created_by"]);
                        string reviewerName = reader["reviewer_name"]?.ToString() ?? "Unknown User";

                        Reviews.Add(new ReviewViewModel
                        {
                            ReviewId = Convert.ToInt32(reader["review_id"]),
                            Rating = Convert.ToInt32(reader["rating"]),
                            Comment = reader["comment"] == DBNull.Value ? null : reader["comment"]?.ToString(),
                            CreatedAt = reader["created_at"] == DBNull.Value
                                ? DateTime.MinValue
                                : Convert.ToDateTime(reader["created_at"]),
                            CreatedBy = createdBy,
                            CreatedByName = userId.HasValue && createdBy == userId.Value
                                ? "You"
                                : reviewerName
                        });
                    }
                }

                if (userId.HasValue)
                {
                    string favoriteSql = @"
                        SELECT COUNT(*)
                        FROM favorites
                        WHERE user_id = @user_id AND recipe_id = @recipe_id;";

                    using (var favoriteCmd = new MySqlCommand(favoriteSql, connection))
                    {
                        favoriteCmd.Parameters.AddWithValue("@user_id", userId.Value);
                        favoriteCmd.Parameters.AddWithValue("@recipe_id", id);

                        IsFavorited = Convert.ToInt32(await favoriteCmd.ExecuteScalarAsync()) > 0;
                    }

                    string myReviewSql = @"
                        SELECT rating, comment
                        FROM recipe_reviews
                        WHERE recipe_id = @recipe_id AND created_by = @created_by
                        LIMIT 1;";

                    using var myReviewCmd = new MySqlCommand(myReviewSql, connection);
                    myReviewCmd.Parameters.AddWithValue("@recipe_id", id);
                    myReviewCmd.Parameters.AddWithValue("@created_by", userId.Value);

                    using var myReviewReader = await myReviewCmd.ExecuteReaderAsync();

                    if (await myReviewReader.ReadAsync())
                    {
                        RatingInput = myReviewReader["rating"] == DBNull.Value
                            ? null
                            : Convert.ToInt32(myReviewReader["rating"]);

                        CommentInput = myReviewReader["comment"] == DBNull.Value
                            ? null
                            : myReviewReader["comment"]?.ToString();
                    }

                    await myReviewReader.CloseAsync();

                    if (RecipeAllergens.Any())
                    {
                        string allergyLoadSql = @"
                            SELECT term
                            FROM user_food_preferences
                            WHERE user_id = @user_id AND pref_type = 'allergy';";

                        using var allergyCmd = new MySqlCommand(allergyLoadSql, connection);
                        allergyCmd.Parameters.AddWithValue("@user_id", userId.Value);

                        using var allergyReader = await allergyCmd.ExecuteReaderAsync();

                        var userAllergies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        while (await allergyReader.ReadAsync())
                        {
                            userAllergies.Add(allergyReader.GetString(0));
                        }

                        MatchedAllergens = RecipeAllergens
                            .Where(a => userAllergies.Contains(a))
                            .ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to load recipe details: {ex.Message}";
            }
        }

        public class RecipeDetailsViewModel
        {
            public int RecipeId { get; set; }
            public string Title { get; set; } = string.Empty;
            public string? Description { get; set; }
            public string? Instructions { get; set; }
            public int? PrepTime { get; set; }
            public int? CookTime { get; set; }
            public int? Servings { get; set; }
            public int? Calories { get; set; }
            public string? Difficulty { get; set; }
            public string? ImageUrl { get; set; }
            public string? Cuisine { get; set; }
            public string? DietType { get; set; }
            public List<string> MealTypes { get; set; } = new();
            public decimal? AverageRating { get; set; }
        }

        public class IngredientViewModel
        {
            public string? Quantity { get; set; }
            public string? Unit { get; set; }
            public string? IngredientName { get; set; }
        }

        public class ReviewViewModel
        {
            public int ReviewId { get; set; }
            public int Rating { get; set; }
            public string? Comment { get; set; }
            public DateTime CreatedAt { get; set; }
            public int CreatedBy { get; set; }
            public string CreatedByName { get; set; } = string.Empty;
        }
    }
}