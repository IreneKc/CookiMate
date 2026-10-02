using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;

namespace CookiMateWeb.Pages
{
    public class ManageRecipeModel : PageModel
    {
        private readonly IConfiguration _configuration;
        private const int PageSize = 30;

        public ManageRecipeModel(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public List<RecipeCardViewModel> Recipes { get; set; } = new();
        public string? ErrorMessage { get; set; }

        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }
        public int TotalRecipes { get; set; }

        public string StatusFilter { get; set; } = "pending";

        public int PendingCount { get; set; }
        public int RejectedCount { get; set; }
        public int SuspectedDuplicateCount { get; set; }

        public async Task<IActionResult> OnGetAsync([FromQuery(Name = "status")] string? status, [FromQuery(Name = "p")] int page = 1)
        {
            if (!IsAdmin())
            {
                return RedirectToPage("/Index");
            }

            StatusFilter = NormalizeFilter(status);

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

                await LoadFilterCountsAsync(connection);

                string whereClause = BuildWhereClause(StatusFilter);

                string countSql = $@"
                    SELECT COUNT(*)
                    FROM recipes
                    WHERE {whereClause};";

                using (var countCmd = new MySqlCommand(countSql, connection))
                {
                    TotalRecipes = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
                }

                TotalPages = TotalRecipes == 0
                    ? 1
                    : (int)Math.Ceiling(TotalRecipes / (double)PageSize);

                if (CurrentPage > TotalPages)
                    CurrentPage = TotalPages;

                int offset = (CurrentPage - 1) * PageSize;

                string sql = $@"
                    SELECT recipe_id, title, description, prep_time, cook_time, servings, difficulty, image_url, status, is_duplicate_suspected
                    FROM recipes
                    WHERE {whereClause}
                    ORDER BY updated_at DESC, created_at DESC
                    LIMIT @pageSize OFFSET @offset;";

                using var cmd = new MySqlCommand(sql, connection);
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
                        ImageUrl = reader["image_url"] == DBNull.Value ? null : reader["image_url"]?.ToString(),
                        Status = reader["status"] == DBNull.Value ? "pending" : reader["status"]?.ToString() ?? "pending",
                        IsDuplicateSuspected = reader["is_duplicate_suspected"] != DBNull.Value && Convert.ToBoolean(reader["is_duplicate_suspected"])
                    });
                }

                return Page();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to load recipes: {ex.Message}";
                Recipes = new List<RecipeCardViewModel>();
                TotalRecipes = 0;
                TotalPages = 1;
                CurrentPage = 1;
                return Page();
            }
        }

        public async Task<IActionResult> OnPostApproveAsync(int id, string? status, int p = 1)
        {
            if (!IsAdmin())
            {
                return RedirectToPage("/Index");
            }

            await UpdateRecipeStatusAsync(id, "approved");

            return RedirectToPage("/ManageRecipe", new
            {
                status = NormalizeFilter(status),
                p
            });
        }

        public async Task<IActionResult> OnPostRejectAsync(int id, string? status, int p = 1)
        {
            if (!IsAdmin())
            {
                return RedirectToPage("/Index");
            }

            await UpdateRecipeStatusAsync(id, "rejected");

            return RedirectToPage("/ManageRecipe", new
            {
                status = NormalizeFilter(status),
                p
            });
        }

        public async Task<IActionResult> OnPostSetPendingAsync(int id, string? status, int p = 1)
        {
            if (!IsAdmin())
            {
                return RedirectToPage("/Index");
            }

            await UpdateRecipeStatusAsync(id, "pending");

            return RedirectToPage("/ManageRecipe", new
            {
                status = NormalizeFilter(status),
                p
            });
        }

        private bool IsAdmin()
        {
            string? userRole = HttpContext.Session.GetString("UserRole");
            return string.Equals(userRole, "admin", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFilter(string? status)
        {
            return status?.Trim().ToLowerInvariant() switch
            {
                "pending" => "pending",
                "rejected" => "rejected",
                "suspected-duplicate" => "suspected-duplicate",
                _ => "pending"
            };
        }

        private static string BuildWhereClause(string statusFilter)
        {
            return statusFilter switch
            {
                "pending" => "status = 'pending'",
                "rejected" => "status = 'rejected'",
                "suspected-duplicate" => "is_duplicate_suspected = 1",
                _ => "status = 'pending'"
            };
        }

        private async Task LoadFilterCountsAsync(MySqlConnection connection)
        {
            const string sql = @"
                SELECT
                    SUM(CASE WHEN status = 'pending' THEN 1 ELSE 0 END) AS pending_count,
                    SUM(CASE WHEN status = 'rejected' THEN 1 ELSE 0 END) AS rejected_count,
                    SUM(CASE WHEN is_duplicate_suspected = 1 THEN 1 ELSE 0 END) AS duplicate_count
                FROM recipes;";

            using var cmd = new MySqlCommand(sql, connection);
            using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                PendingCount = reader["pending_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["pending_count"]);
                RejectedCount = reader["rejected_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["rejected_count"]);
                SuspectedDuplicateCount = reader["duplicate_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["duplicate_count"]);
            }
        }

        private async Task UpdateRecipeStatusAsync(int recipeId, string newStatus)
        {
            string connectionString =
                _configuration.GetConnectionString("DefaultConnection")
                ?? "server=127.0.0.1;port=3306;database=cookimate;uid=root;pwd=;";

            using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = @"
                UPDATE recipes
                SET status = @status,
                    updated_at = NOW()
                WHERE recipe_id = @recipe_id;";

            using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@status", newStatus);
            cmd.Parameters.AddWithValue("@recipe_id", recipeId);

            await cmd.ExecuteNonQueryAsync();
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
            public string Status { get; set; } = "pending";
            public bool IsDuplicateSuspected { get; set; }
        }
    }
}