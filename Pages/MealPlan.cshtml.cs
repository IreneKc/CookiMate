using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;

namespace CookiMateWeb.Pages
{
    public class MealPlanModel : PageModel
    {
        private readonly IConfiguration _configuration;

        public MealPlanModel(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public List<DatePillViewModel> DatePills { get; set; } = new();
        public Dictionary<string, List<MealRecipeCardViewModel>> MealGroups { get; set; } = new();
        public string? ErrorMessage { get; set; }
        public string? SuccessMessage { get; set; }

        public DateTime SelectedDate { get; set; }
        public string SelectedDateDisplay { get; set; } = string.Empty;
        public string PlanTitle { get; set; } = "My Meal Plan";

        public async Task<IActionResult> OnGetAsync([FromQuery] string? date = null)
        {
            string? userIdString = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrWhiteSpace(userIdString))
            {
                return RedirectToPage("/Login");
            }

            if (!int.TryParse(userIdString, out int userId))
            {
                HttpContext.Session.Clear();
                return RedirectToPage("/Login");
            }

            SelectedDate = DateTime.Today;

            if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParse(date, out var parsedDate))
            {
                SelectedDate = parsedDate.Date;
            }

            SelectedDateDisplay = SelectedDate.ToString("dddd, dd MMM yyyy");
            SuccessMessage = TempData["SuccessMessage"]?.ToString();
            ErrorMessage = TempData["ErrorMessage"]?.ToString();

            BuildDatePills();
            InitializeMealGroups();

            await LoadMealPlanAsync(userId);

            return Page();
        }

        public async Task<IActionResult> OnPostRemoveAsync(int mealPlanRecipeId, string? date)
        {
            string? userIdString = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrWhiteSpace(userIdString))
            {
                return RedirectToPage("/Login");
            }

            if (!int.TryParse(userIdString, out int userId))
            {
                HttpContext.Session.Clear();
                return RedirectToPage("/Login");
            }

            if (mealPlanRecipeId <= 0)
            {
                TempData["ErrorMessage"] = "Invalid meal plan item.";
                return RedirectToPage("/MealPlan", new { date });
            }

            try
            {
                string connectionString =
                    _configuration.GetConnectionString("DefaultConnection")
                    ?? _configuration.GetConnectionString("Default")
                    ?? "server=127.0.0.1;port=3306;database=cookimate;uid=root;pwd=;";

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                int? planId = null;

                const string findItemSql = @"
                    SELECT mpr.plan_id
                    FROM meal_plan_recipes mpr
                    INNER JOIN meal_plans mp ON mpr.plan_id = mp.plan_id
                    WHERE mpr.meal_plan_recipe_id = @mealPlanRecipeId
                      AND mp.created_by = @userId
                    LIMIT 1;";

                using (var findItemCmd = new MySqlCommand(findItemSql, connection))
                {
                    findItemCmd.Parameters.AddWithValue("@mealPlanRecipeId", mealPlanRecipeId);
                    findItemCmd.Parameters.AddWithValue("@userId", userId);

                    object? result = await findItemCmd.ExecuteScalarAsync();

                    if (result == null)
                    {
                        TempData["ErrorMessage"] = "Meal plan item not found.";
                        return RedirectToPage("/MealPlan", new { date });
                    }

                    planId = Convert.ToInt32(result);
                }

                const string deleteMealSql = @"
                    DELETE mpr
                    FROM meal_plan_recipes mpr
                    INNER JOIN meal_plans mp ON mpr.plan_id = mp.plan_id
                    WHERE mpr.meal_plan_recipe_id = @mealPlanRecipeId
                      AND mp.created_by = @userId;";

                int rowsAffected;

                using (var deleteMealCmd = new MySqlCommand(deleteMealSql, connection))
                {
                    deleteMealCmd.Parameters.AddWithValue("@mealPlanRecipeId", mealPlanRecipeId);
                    deleteMealCmd.Parameters.AddWithValue("@userId", userId);

                    rowsAffected = await deleteMealCmd.ExecuteNonQueryAsync();
                }

                if (rowsAffected <= 0)
                {
                    TempData["ErrorMessage"] = "Meal plan item could not be removed.";
                    return RedirectToPage("/MealPlan", new { date });
                }

                if (planId.HasValue)
                {
                    const string countRemainingSql = @"
                        SELECT COUNT(*)
                        FROM meal_plan_recipes
                        WHERE plan_id = @planId;";

                    int remainingCount;

                    using (var countCmd = new MySqlCommand(countRemainingSql, connection))
                    {
                        countCmd.Parameters.AddWithValue("@planId", planId.Value);
                        remainingCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
                    }

                    if (remainingCount == 0)
                    {
                        const string deletePlanSql = @"
                            DELETE FROM meal_plans
                            WHERE plan_id = @planId
                              AND created_by = @userId;";

                        using var deletePlanCmd = new MySqlCommand(deletePlanSql, connection);
                        deletePlanCmd.Parameters.AddWithValue("@planId", planId.Value);
                        deletePlanCmd.Parameters.AddWithValue("@userId", userId);
                        await deletePlanCmd.ExecuteNonQueryAsync();
                    }
                }

                TempData["SuccessMessage"] = "Recipe removed from meal plan.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Failed to remove meal plan item: {ex.Message}";
            }

            return RedirectToPage("/MealPlan", new { date });
        }

        private async Task LoadMealPlanAsync(int userId)
        {
            try
            {
                string connectionString =
                    _configuration.GetConnectionString("DefaultConnection")
                    ?? _configuration.GetConnectionString("Default")
                    ?? "server=127.0.0.1;port=3306;database=cookimate;uid=root;pwd=;";

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                const string planSql = @"
                    SELECT plan_id, title
                    FROM meal_plans
                    WHERE created_by = @UserId
                      AND date = @SelectedDate
                    ORDER BY created_at DESC
                    LIMIT 1;";

                int? planId = null;

                using (var planCmd = new MySqlCommand(planSql, connection))
                {
                    planCmd.Parameters.AddWithValue("@UserId", userId);
                    planCmd.Parameters.AddWithValue("@SelectedDate", SelectedDate.ToString("yyyy-MM-dd"));

                    using var planReader = await planCmd.ExecuteReaderAsync();

                    if (await planReader.ReadAsync())
                    {
                        planId = Convert.ToInt32(planReader["plan_id"]);
                        PlanTitle = planReader["title"] == DBNull.Value
                            ? "My Meal Plan"
                            : planReader["title"]?.ToString() ?? "My Meal Plan";
                    }
                }

                if (!planId.HasValue)
                {
                    return;
                }

                const string mealsSql = @"
                    SELECT
                        mpr.meal_plan_recipe_id,
                        mpr.meal_type,
                        r.recipe_id,
                        r.title,
                        r.description,
                        r.prep_time,
                        r.cook_time,
                        r.servings,
                        r.difficulty,
                        r.image_url
                    FROM meal_plan_recipes mpr
                    INNER JOIN recipes r ON mpr.recipe_id = r.recipe_id
                    WHERE mpr.plan_id = @PlanId
                    ORDER BY
                        FIELD(mpr.meal_type, 'breakfast', 'lunch', 'dinner', 'snack'),
                        r.title ASC;";

                using var mealsCmd = new MySqlCommand(mealsSql, connection);
                mealsCmd.Parameters.AddWithValue("@PlanId", planId.Value);

                using var reader = await mealsCmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    string mealType = reader["meal_type"]?.ToString()?.ToLower() ?? "lunch";

                    if (!MealGroups.ContainsKey(mealType))
                    {
                        MealGroups[mealType] = new List<MealRecipeCardViewModel>();
                    }

                    MealGroups[mealType].Add(new MealRecipeCardViewModel
                    {
                        MealPlanRecipeId = Convert.ToInt32(reader["meal_plan_recipe_id"]),
                        RecipeId = Convert.ToInt32(reader["recipe_id"]),
                        Title = reader["title"]?.ToString() ?? "Untitled Recipe",
                        Description = reader["description"] == DBNull.Value ? null : reader["description"]?.ToString(),
                        PrepTime = reader["prep_time"] == DBNull.Value ? null : Convert.ToInt32(reader["prep_time"]),
                        CookTime = reader["cook_time"] == DBNull.Value ? null : Convert.ToInt32(reader["cook_time"]),
                        Servings = reader["servings"] == DBNull.Value ? null : Convert.ToInt32(reader["servings"]),
                        Difficulty = reader["difficulty"] == DBNull.Value ? null : reader["difficulty"]?.ToString(),
                        ImageUrl = reader["image_url"] == DBNull.Value ? null : reader["image_url"]?.ToString(),
                        MealType = mealType
                    });
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to load meal plan: {ex.Message}";
            }
        }

        private void InitializeMealGroups()
        {
            MealGroups = new Dictionary<string, List<MealRecipeCardViewModel>>
            {
                { "breakfast", new List<MealRecipeCardViewModel>() },
                { "lunch", new List<MealRecipeCardViewModel>() },
                { "dinner", new List<MealRecipeCardViewModel>() },
                { "snack", new List<MealRecipeCardViewModel>() }
            };
        }

        private void BuildDatePills()
        {
            DatePills = new List<DatePillViewModel>();

            for (int i = 0; i < 30; i++)
            {
                var futureDate = DateTime.Today.AddDays(i);

                DatePills.Add(new DatePillViewModel
                {
                    DateValue = futureDate.ToString("yyyy-MM-dd"),
                    ShortDay = futureDate.ToString("ddd"),
                    DayNumber = futureDate.Day.ToString(),
                    ShortMonth = futureDate.ToString("MMM"),
                    IsSelected = futureDate.Date == SelectedDate.Date,
                    IsToday = futureDate.Date == DateTime.Today
                });
            }
        }

        public class DatePillViewModel
        {
            public string DateValue { get; set; } = string.Empty;
            public string ShortDay { get; set; } = string.Empty;
            public string DayNumber { get; set; } = string.Empty;
            public string ShortMonth { get; set; } = string.Empty;
            public bool IsSelected { get; set; }
            public bool IsToday { get; set; }
        }

        public class MealRecipeCardViewModel
        {
            public int MealPlanRecipeId { get; set; }
            public int RecipeId { get; set; }
            public string Title { get; set; } = string.Empty;
            public string? Description { get; set; }
            public int? PrepTime { get; set; }
            public int? CookTime { get; set; }
            public int? Servings { get; set; }
            public string? Difficulty { get; set; }
            public string? ImageUrl { get; set; }
            public string MealType { get; set; } = string.Empty;
        }
    }
}