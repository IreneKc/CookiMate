using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;

namespace CookiMateWeb.Pages
{
    public class IndexModel : PageModel
    {
        private readonly IConfiguration _configuration;
        private const int PageSize = 30;

        public IndexModel(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public List<RecipeCardViewModel> Recipes { get; set; } = new();
        public string? ErrorMessage { get; set; }

        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }
        public int TotalRecipes { get; set; }

        public async Task OnGetAsync([FromQuery(Name = "p")] int page = 1)
        {
            if (page < 1)
                page = 1;

            CurrentPage = page;

            try
            {
                string connectionString =
                    _configuration.GetConnectionString("DefaultConnection")
                    ?? "server=127.0.0.1;port=3306;database=cookimate;uid=root;pwd=;";

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                // Auto-apply saved diet + auto-exclude allergens/dislikes for a
                // logged-in user. Anonymous visitors see the unfiltered feed —
                // there's no Profile to read preferences from. No override here
                // (Index has no filter UI at all), same posture as /recommend.
                string? userIdString = HttpContext.Session.GetString("UserID");
                UserFoodPreferencesHelper.Prefs? prefs = null;
                if (int.TryParse(userIdString, out int sessionUserId))
                {
                    prefs = await UserFoodPreferencesHelper.LoadAsync(connection, sessionUserId);
                }

                // Build the shared WHERE clause + params once, reused by both the
                // COUNT query and the page query so pagination stays consistent
                // with what's actually being shown.
                var (whereSql, whereParams) = BuildPreferenceWhereClause(prefs);

                string countSql = $@"
                    SELECT COUNT(*)
                    FROM recipes
                    WHERE status = 'approved'{whereSql};";

                using (var countCmd = new MySqlCommand(countSql, connection))
                {
                    foreach (var p in whereParams)
                        countCmd.Parameters.AddWithValue(p.Key, p.Value);
                    TotalRecipes = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
                }

                TotalPages = TotalRecipes == 0
                    ? 1
                    : (int)Math.Ceiling(TotalRecipes / (double)PageSize);

                if (CurrentPage > TotalPages)
                    CurrentPage = TotalPages;

                int offset = (CurrentPage - 1) * PageSize;

                string dailySeed = GetMalaysiaDateSeed();

                string sql = $@"
                    SELECT recipe_id, title, description, prep_time, cook_time, servings, difficulty, image_url
                    FROM recipes
                    WHERE status = 'approved'{whereSql}
                    ORDER BY MD5(CONCAT(recipe_id, @seed))
                    LIMIT @pageSize OFFSET @offset;";
                //ORDER BY updated_at DESC, created_at DESC
                //ORDER BY RAND() display random
                using var cmd = new MySqlCommand(sql, connection);
                foreach (var p in whereParams)
                    cmd.Parameters.AddWithValue(p.Key, p.Value);
                cmd.Parameters.AddWithValue("@seed", dailySeed);
                cmd.Parameters.AddWithValue("@pageSize", PageSize);
                cmd.Parameters.AddWithValue("@offset", offset);

                using var reader = await cmd.ExecuteReaderAsync();

                Recipes = new List<RecipeCardViewModel>();

                while (await reader.ReadAsync())
                {
                    Recipes.Add(new RecipeCardViewModel
                    {
                        RecipeId = Convert.ToInt32(reader["recipe_id"]),
                        Title = reader["title"]?.ToString() ?? "Untitled Recipe",
                        Description = reader["description"] == DBNull.Value ? null : reader["description"]?.ToString(),
                        PrepTime = reader["prep_time"] == DBNull.Value ? null : Convert.ToInt32(reader["prep_time"]),
                        CookTime = reader["cook_time"] == DBNull.Value ? null : Convert.ToInt32(reader["cook_time"]),
                        Servings = reader["servings"] == DBNull.Value ? null : Convert.ToInt32(reader["servings"]),
                        Difficulty = reader["difficulty"] == DBNull.Value ? null : reader["difficulty"]?.ToString(),
                        ImageUrl = reader["image_url"] == DBNull.Value ? null : reader["image_url"]?.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to load recipes: {ex.Message}";
                Recipes = new List<RecipeCardViewModel>();
                TotalRecipes = 0;
                TotalPages = 1;
                CurrentPage = 1;
            }
        }

        // Builds an appendable " AND ..." fragment (or "" if the user has no
        // preferences / isn't logged in) plus its bound parameters. Same query
        // shape as search.py's diet_tag filter and the allergen/dislike
        // exclusion in user_preferences.py, just expressed directly in SQL
        // since Index never goes through the FastAPI layer.
        private static (string Sql, Dictionary<string, object> Params) BuildPreferenceWhereClause(
            UserFoodPreferencesHelper.Prefs? prefs)
        {
            var clauses = new List<string>();
            var parms = new Dictionary<string, object>();

            if (prefs == null)
            {
                return ("", parms);
            }

            if (!string.IsNullOrWhiteSpace(prefs.Diet))
            {
                clauses.Add(@"recipe_id IN (
                    SELECT rt.recipe_id FROM recipe_tags rt
                    JOIN tags t ON t.tag_id = rt.tag_id
                    WHERE t.tag_type = 'diet' AND LOWER(t.tag_name) = LOWER(@dietPref)
                )");
                parms["@dietPref"] = prefs.Diet;
            }

            if (prefs.Allergies.Count > 0)
            {
                var names = new List<string>();
                for (int i = 0; i < prefs.Allergies.Count; i++)
                {
                    string key = $"@allergen{i}";
                    names.Add(key);
                    parms[key] = prefs.Allergies[i];
                }
                clauses.Add($@"recipe_id NOT IN (
                    SELECT rt.recipe_id FROM recipe_tags rt
                    JOIN tags t ON t.tag_id = rt.tag_id
                    WHERE t.tag_type = 'allergen' AND LOWER(t.tag_name) IN ({string.Join(",", names)})
                )");
            }

            if (prefs.Dislikes.Count > 0)
            {
                var likeConditions = new List<string>();
                for (int i = 0; i < prefs.Dislikes.Count; i++)
                {
                    string key = $"@dislike{i}";
                    likeConditions.Add($"LOWER(i.ingredient_name) LIKE {key}");
                    parms[key] = $"%{prefs.Dislikes[i]}%";
                }
                clauses.Add($@"recipe_id NOT IN (
                    SELECT ri.recipe_id FROM recipe_ingredients ri
                    JOIN ingredients i ON i.ingredient_id = ri.ingredient_id
                    WHERE {string.Join(" OR ", likeConditions)}
                )");
            }

            if (clauses.Count == 0)
            {
                return ("", parms);
            }

            return (" AND " + string.Join(" AND ", clauses), parms);
        }

        private static string GetMalaysiaDateSeed()
        {
            TimeZoneInfo malaysiaTz;
            try
            {
                // Linux / ICU time zone ID
                malaysiaTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuala_Lumpur");
            }
            catch (TimeZoneNotFoundException)
            {
                // Windows time zone ID fallback (same UTC+8 offset, no DST)
                malaysiaTz = TimeZoneInfo.FindSystemTimeZoneById("Singapore Standard Time");
            }

            DateTime malaysiaNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, malaysiaTz);
            return malaysiaNow.ToString("yyyyMMdd");
        }

        public class RecipeCardViewModel
        {
            public int RecipeId { get; set; }
            public string Title { get; set; } = string.Empty;
            public string? Description { get; set; }
            public int? PrepTime { get; set; }
            public int? CookTime { get; set; }
            public int? Servings { get; set; }
            public string? Difficulty { get; set; }
            public string? ImageUrl { get; set; }
        }
    }
}