using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;

namespace CookiMateWeb.Pages
{
    public class FavoritesModel : PageModel
    {
        private readonly IConfiguration _configuration;

        public FavoritesModel(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public List<FavoriteRecipeViewModel> Recipes { get; set; } = new();
        public string? ErrorMessage { get; set; }
        public string? SuccessMessage { get; set; }

        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }
        public int TotalRecipes { get; set; }

        public async Task<IActionResult> OnGetAsync(int p = 1)
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

            if (p < 1)
            {
                p = 1;
            }

            CurrentPage = p;

            const int pageSize = 30;
            int offset = (CurrentPage - 1) * pageSize;

            SuccessMessage = TempData["SuccessMessage"]?.ToString();
            ErrorMessage = TempData["ErrorMessage"]?.ToString();

            try
            {
                string connectionString =
                    _configuration.GetConnectionString("DefaultConnection")
                    ?? "server=127.0.0.1;port=3306;database=cookimate;uid=root;pwd=;";

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                string countSql = @"
                    SELECT COUNT(*)
                    FROM favorites f
                    INNER JOIN recipes r ON f.recipe_id = r.recipe_id
                    WHERE f.user_id = @user_id
                      AND r.status = 'approved';";

                using (var countCmd = new MySqlCommand(countSql, connection))
                {
                    countCmd.Parameters.AddWithValue("@user_id", userId);
                    TotalRecipes = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
                }

                TotalPages = TotalRecipes == 0
                    ? 1
                    : (int)Math.Ceiling(TotalRecipes / (double)pageSize);

                if (CurrentPage > TotalPages)
                {
                    CurrentPage = TotalPages;
                    offset = (CurrentPage - 1) * pageSize;
                }

                string recipeSql = @"
                    SELECT r.recipe_id,
                           r.title,
                           r.description,
                           r.prep_time,
                           r.cook_time,
                           r.servings,
                           r.difficulty,
                           r.image_url,
                           f.created_at
                    FROM favorites f
                    INNER JOIN recipes r ON f.recipe_id = r.recipe_id
                    WHERE f.user_id = @user_id
                      AND r.status = 'approved'
                    ORDER BY f.created_at DESC, r.recipe_id DESC
                    LIMIT @limit OFFSET @offset;";

                using var recipeCmd = new MySqlCommand(recipeSql, connection);
                recipeCmd.Parameters.AddWithValue("@user_id", userId);
                recipeCmd.Parameters.AddWithValue("@limit", pageSize);
                recipeCmd.Parameters.AddWithValue("@offset", offset);

                using var reader = await recipeCmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    Recipes.Add(new FavoriteRecipeViewModel
                    {
                        RecipeId = Convert.ToInt32(reader["recipe_id"]),
                        Title = reader["title"]?.ToString() ?? "Untitled Recipe",
                        Description = reader["description"] == DBNull.Value ? null : reader["description"]?.ToString(),
                        PrepTime = reader["prep_time"] == DBNull.Value ? null : Convert.ToInt32(reader["prep_time"]),
                        CookTime = reader["cook_time"] == DBNull.Value ? null : Convert.ToInt32(reader["cook_time"]),
                        Servings = reader["servings"] == DBNull.Value ? null : Convert.ToInt32(reader["servings"]),
                        Difficulty = reader["difficulty"] == DBNull.Value ? null : reader["difficulty"]?.ToString(),
                        ImageUrl = reader["image_url"] == DBNull.Value ? null : reader["image_url"]?.ToString(),
                        FavoritedAt = reader["created_at"] == DBNull.Value ? null : Convert.ToDateTime(reader["created_at"])
                    });
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to load favorite recipes: {ex.Message}";
            }

            return Page();
        }

        public async Task<IActionResult> OnPostRemoveAsync(int id)
        {
            string? userIdString = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrEmpty(userIdString) || !int.TryParse(userIdString, out int userId))
            {
                return RedirectToPage("/Login");
            }

            try
            {
                string connectionString =
                    _configuration.GetConnectionString("DefaultConnection")
                    ?? "server=127.0.0.1;port=3306;database=cookimate;uid=root;pwd=;";

                using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                string deleteSql = @"
                    DELETE FROM favorites
                    WHERE user_id = @user_id AND recipe_id = @recipe_id;";

                using var cmd = new MySqlCommand(deleteSql, connection);
                cmd.Parameters.AddWithValue("@user_id", userId);
                cmd.Parameters.AddWithValue("@recipe_id", id);

                int rowsAffected = await cmd.ExecuteNonQueryAsync();

                if (rowsAffected > 0)
                {
                    TempData["SuccessMessage"] = "Removed from favorites.";
                }
                else
                {
                    TempData["ErrorMessage"] = "Recipe was not found in favorites.";
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Failed to remove favorite: {ex.Message}";
            }

            return RedirectToPage("/Favorites");
        }

        public class FavoriteRecipeViewModel
        {
            public int RecipeId { get; set; }
            public string Title { get; set; } = string.Empty;
            public string? Description { get; set; }
            public int? PrepTime { get; set; }
            public int? CookTime { get; set; }
            public int? Servings { get; set; }
            public string? Difficulty { get; set; }
            public string? ImageUrl { get; set; }
            public DateTime? FavoritedAt { get; set; }
        }
    }
}