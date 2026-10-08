using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;

namespace CookiMateWeb.Pages
{
    public class MyRecipesModel : PageModel
    {
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _environment;

        public MyRecipesModel(IConfiguration configuration, IWebHostEnvironment environment)
        {
            _configuration = configuration;
            _environment = environment;
        }

        public List<MyRecipeItem> Recipes { get; set; } = new();
        public string? ErrorMessage { get; set; }

        public string CurrentStatusFilter { get; set; } = "all";

        public int TotalRecipesCount { get; set; }
        public int PendingRecipesCount { get; set; }
        public int ApprovedRecipesCount { get; set; }
        public int RejectedRecipesCount { get; set; }

        public async Task<IActionResult> OnGetAsync(string? status)
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

            CurrentStatusFilter = string.IsNullOrWhiteSpace(status)
                ? "all"
                : status.Trim().ToLower();

            if (CurrentStatusFilter != "all" &&
                CurrentStatusFilter != "pending" &&
                CurrentStatusFilter != "approved" &&
                CurrentStatusFilter != "rejected")
            {
                CurrentStatusFilter = "all";
            }

            try
            {
                string connectionString =
                    _configuration.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException("Database connection string is missing.");

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                string countSql = @"
                    SELECT 
                        COUNT(*) AS total_count,
                        SUM(CASE WHEN status = 'pending' THEN 1 ELSE 0 END) AS pending_count,
                        SUM(CASE WHEN status = 'approved' THEN 1 ELSE 0 END) AS approved_count,
                        SUM(CASE WHEN status = 'rejected' THEN 1 ELSE 0 END) AS rejected_count
                    FROM recipes
                    WHERE created_by = @created_by;";

                using (var countCommand = new MySqlCommand(countSql, connection))
                {
                    countCommand.Parameters.AddWithValue("@created_by", userId);

                    using var countReader = await countCommand.ExecuteReaderAsync();
                    if (await countReader.ReadAsync())
                    {
                        TotalRecipesCount = countReader["total_count"] != DBNull.Value
                            ? Convert.ToInt32(countReader["total_count"])
                            : 0;

                        PendingRecipesCount = countReader["pending_count"] != DBNull.Value
                            ? Convert.ToInt32(countReader["pending_count"])
                            : 0;

                        ApprovedRecipesCount = countReader["approved_count"] != DBNull.Value
                            ? Convert.ToInt32(countReader["approved_count"])
                            : 0;

                        RejectedRecipesCount = countReader["rejected_count"] != DBNull.Value
                            ? Convert.ToInt32(countReader["rejected_count"])
                            : 0;
                    }
                }

                string sql = @"
                    SELECT
                        recipe_id,
                        title,
                        image_url,
                        status,
                        created_at,
                        updated_at
                    FROM recipes
                    WHERE created_by = @created_by";

                if (CurrentStatusFilter != "all")
                {
                    sql += " AND status = @status";
                }

                sql += " ORDER BY created_at DESC, recipe_id DESC;";

                using var command = new MySqlCommand(sql, connection);
                command.Parameters.AddWithValue("@created_by", userId);

                if (CurrentStatusFilter != "all")
                {
                    command.Parameters.AddWithValue("@status", CurrentStatusFilter);
                }

                using var reader = await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    Recipes.Add(new MyRecipeItem
                    {
                        RecipeId = reader["recipe_id"] != DBNull.Value
                            ? Convert.ToInt32(reader["recipe_id"])
                            : 0,

                        Title = reader["title"]?.ToString() ?? string.Empty,

                        ImageUrl = reader["image_url"] == DBNull.Value
                            ? null
                            : reader["image_url"]?.ToString(),

                        Status = reader["status"] == DBNull.Value
                            ? "pending"
                            : reader["status"]?.ToString(),

                        CreatedAt = reader["created_at"] != DBNull.Value
                            ? Convert.ToDateTime(reader["created_at"])
                            : DateTime.MinValue,

                        UpdatedAt = reader["updated_at"] != DBNull.Value
                            ? Convert.ToDateTime(reader["updated_at"])
                            : DateTime.MinValue
                    });
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to load your recipes: {ex.Message}";
            }

            return Page();
        }

        public async Task<IActionResult> OnPostDeleteAsync(int id)
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

            string? imageUrl = null;

            try
            {
                string connectionString =
                    _configuration.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException("Database connection string is missing.");

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                string getRecipeSql = @"
            SELECT image_url
            FROM recipes
            WHERE recipe_id = @recipe_id AND created_by = @created_by
            LIMIT 1;";

                using (var getCmd = new MySqlCommand(getRecipeSql, connection))
                {
                    getCmd.Parameters.AddWithValue("@recipe_id", id);
                    getCmd.Parameters.AddWithValue("@created_by", userId);

                    using var reader = await getCmd.ExecuteReaderAsync();

                    if (!await reader.ReadAsync())
                    {
                        ErrorMessage = "Recipe not found or you do not have permission to delete it.";
                        await OnGetAsync(CurrentStatusFilter);
                        return Page();
                    }

                    imageUrl = reader["image_url"] == DBNull.Value
                        ? null
                        : reader["image_url"]?.ToString();
                }

                using var transaction = await connection.BeginTransactionAsync();

                try
                {
                    string deleteRecipeTagsSql = "DELETE FROM recipe_tags WHERE recipe_id = @recipe_id;";
                    using (var cmd = new MySqlCommand(deleteRecipeTagsSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@recipe_id", id);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    string deleteRecipeIngredientsSql = "DELETE FROM recipe_ingredients WHERE recipe_id = @recipe_id;";
                    using (var cmd = new MySqlCommand(deleteRecipeIngredientsSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@recipe_id", id);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    string deleteRecipeSql = "DELETE FROM recipes WHERE recipe_id = @recipe_id AND created_by = @created_by;";
                    using (var cmd = new MySqlCommand(deleteRecipeSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@recipe_id", id);
                        cmd.Parameters.AddWithValue("@created_by", userId);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }

                DeleteRecipeImageFile(imageUrl);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to delete recipe: {ex.Message}";
                await OnGetAsync(CurrentStatusFilter);
                return Page();
            }

            TempData["SuccessMessage"] = "Recipe deleted successfully.";
            return RedirectToPage("/MyRecipes", new { status = CurrentStatusFilter });
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

        public class MyRecipeItem
        {
            public int RecipeId { get; set; }
            public string Title { get; set; } = string.Empty;
            public string? ImageUrl { get; set; }
            public string? Status { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
        }
    }
}