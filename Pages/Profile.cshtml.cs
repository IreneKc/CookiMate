using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using System.ComponentModel.DataAnnotations;

namespace CookiMateWeb.Pages
{
    public class ProfileModel : PageModel
    {
        private readonly IConfiguration _configuration;

        public ProfileModel(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        [TempData]
        public string? SuccessMessage { get; set; }

        public string? ErrorMessage { get; set; }

        public readonly List<(string Value, string Label)> AllergenOptions = new()
        {
            ("peanut", "Peanut"),
            ("tree-nut", "Tree Nuts"),
            ("shellfish", "Shellfish"),
            ("fish", "Fish"),
            ("egg", "Egg"),
            ("dairy", "Dairy"),
            ("soy", "Soy"),
            ("gluten", "Wheat / Gluten"),
            ("sesame", "Sesame"),
        };

        public async Task<IActionResult> OnGetAsync()
        {
            string? userIdString = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrEmpty(userIdString) || !int.TryParse(userIdString, out int userId))
            {
                return RedirectToPage("/Login");
            }

            try
            {
                await using var connection = new MySqlConnection(GetConnectionString());
                await connection.OpenAsync();

                const string dietSql = "SELECT diet FROM users WHERE user_id = @UserID LIMIT 1;";
                await using (var dietCmd = new MySqlCommand(dietSql, connection))
                {
                    dietCmd.Parameters.AddWithValue("@UserID", userId);
                    var result = await dietCmd.ExecuteScalarAsync();
                    Input.Diet = result as string ?? "";
                }

                Input.SelectedAllergies = await LoadTermsAsync(connection, userId, "allergy");
                Input.Dislikes = string.Join(", ", await LoadTermsAsync(connection, userId, "dislike"));
            }
            catch (Exception)
            {
                ErrorMessage = "Couldn't load your profile right now. Please try again.";
            }

            return Page();
        }

        private static readonly HashSet<string> AllowedDiets = new(StringComparer.OrdinalIgnoreCase)
        {
            "vegetarian", "vegan", "halal", "low-carb"
        };

        private static readonly HashSet<string> AllowedAllergens = new(StringComparer.OrdinalIgnoreCase)
        {
            "peanut", "tree-nut", "shellfish", "fish", "egg", "dairy", "soy", "gluten", "sesame"
        };

        public async Task<IActionResult> OnPostAsync()
        {
            string? userIdString = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrEmpty(userIdString) || !int.TryParse(userIdString, out int userId))
            {
                return RedirectToPage("/Login");
            }

            if (!string.IsNullOrWhiteSpace(Input.Diet) && !AllowedDiets.Contains(Input.Diet.Trim()))
            {
                ErrorMessage = "Please choose a valid diet option.";
                return Page();
            }

            var invalidAllergen = Input.SelectedAllergies.FirstOrDefault(a => !AllowedAllergens.Contains(a));
            if (invalidAllergen != null)
            {
                ErrorMessage = "Please choose a valid allergy option.";
                return Page();
            }

            if (!ModelState.IsValid)
            {
                return Page();
            }

            try
            {
                await using var connection = new MySqlConnection(GetConnectionString());
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();

                const string updateDietSql = "UPDATE users SET diet = @Diet WHERE user_id = @UserID;";

                await using (var dietCmd = new MySqlCommand(updateDietSql, connection, transaction))
                {
                    dietCmd.Parameters.AddWithValue("@Diet",
                        string.IsNullOrWhiteSpace(Input.Diet) ? (object)DBNull.Value : Input.Diet.Trim().ToLowerInvariant());
                    dietCmd.Parameters.AddWithValue("@UserID", userId);
                    await dietCmd.ExecuteNonQueryAsync();
                }

                var normalizedAllergies = Input.SelectedAllergies
                    .Select(a => a.ToLowerInvariant())
                    .Distinct()
                    .ToList();
                await ReplaceTermsAsync(connection, transaction, userId, "allergy", normalizedAllergies);
                await ReplaceTermsAsync(connection, transaction, userId, "dislike", ParseTerms(Input.Dislikes));

                await transaction.CommitAsync();

                SuccessMessage = "Profile saved.";
                return RedirectToPage("/Profile");
            }
            catch (Exception)
            {
                ErrorMessage = "Couldn't save your profile right now. Please try again.";
                return Page();
            }
        }

        private static async Task<List<string>> LoadTermsAsync(MySqlConnection connection, int userId, string prefType)
        {
            const string sql = @"
                SELECT term FROM user_food_preferences
                WHERE user_id = @UserID AND pref_type = @PrefType
                ORDER BY term;";

            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@UserID", userId);
            cmd.Parameters.AddWithValue("@PrefType", prefType);

            var terms = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                terms.Add(reader.GetString(0));
            }
            return terms;
        }

        private static async Task ReplaceTermsAsync(
            MySqlConnection connection,
            MySqlTransaction transaction,
            int userId,
            string prefType,
            List<string> terms)
        {
            const string deleteSql = @"
                DELETE FROM user_food_preferences
                WHERE user_id = @UserID AND pref_type = @PrefType;";

            await using (var deleteCmd = new MySqlCommand(deleteSql, connection, transaction))
            {
                deleteCmd.Parameters.AddWithValue("@UserID", userId);
                deleteCmd.Parameters.AddWithValue("@PrefType", prefType);
                await deleteCmd.ExecuteNonQueryAsync();
            }

            if (terms.Count == 0)
            {
                return;
            }

            const string insertSql = @"
                INSERT INTO user_food_preferences (user_id, pref_type, term)
                VALUES (@UserID, @PrefType, @Term);";

            foreach (var term in terms)
            {
                await using var insertCmd = new MySqlCommand(insertSql, connection, transaction);
                insertCmd.Parameters.AddWithValue("@UserID", userId);
                insertCmd.Parameters.AddWithValue("@PrefType", prefType);
                insertCmd.Parameters.AddWithValue("@Term", term);
                await insertCmd.ExecuteNonQueryAsync();
            }
        }

        private static List<string> ParseTerms(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new List<string>();
            }

            return raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant())
                .Where(s => s.Length > 0)
                .Distinct()
                .ToList();
        }

        private string GetConnectionString()
        {
            return _configuration.GetConnectionString("Default")
                ?? _configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Database connection string is missing.");
        }

        public class InputModel
        {
            public string? Diet { get; set; }

            public List<string> SelectedAllergies { get; set; } = new();

            [StringLength(500, ErrorMessage = "Dislikes list is too long.")]
            public string? Dislikes { get; set; }
        }
    }
}
