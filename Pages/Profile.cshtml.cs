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

        // The fixed allergen list — the standard "big 9" food allergen
        // categories. Keep Value in sync with AllowedAllergens below and
        // with whatever taxonomy the future detection script uses to tag
        // recipes, so a saved profile term always corresponds to a real tag.
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
                // No row yet is normal for a user who's never saved a profile —
                // Input.Diet stays "" (No preference).

                Input.SelectedAllergies = await LoadTermsAsync(connection, userId, "allergy");
                Input.Dislikes = string.Join(", ", await LoadTermsAsync(connection, userId, "dislike"));
            }
            catch (Exception)
            {
                ErrorMessage = "Couldn't load your profile right now. Please try again.";
            }

            return Page();
        }

        // Keep this in sync with the <select> options in Profile.cshtml —
        // these are the only diet_tag values that currently exist in `tags`
        // (tag_type = 'diet'). Empty string ("No preference") is allowed.
        private static readonly HashSet<string> AllowedDiets = new(StringComparer.OrdinalIgnoreCase)
        {
            "vegetarian", "vegan", "halal", "low-carb"
        };

        // Keep this in sync with the checkbox values in Profile.cshtml
        // (AllergenOptions above) — the only allergen terms a future
        // detection pass will actually be able to match against recipes.
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

            // Defense in depth: the checkboxes only ever submit known values,
            // but never trust that a POST came from the rendered form.
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

                // --- diet: single value, plain column on users (the row
                // already exists from Register, so this is always an
                // UPDATE, never an insert) ---
                const string updateDietSql = "UPDATE users SET diet = @Diet WHERE user_id = @UserID;";

                await using (var dietCmd = new MySqlCommand(updateDietSql, connection, transaction))
                {
                    dietCmd.Parameters.AddWithValue("@Diet",
                        string.IsNullOrWhiteSpace(Input.Diet) ? (object)DBNull.Value : Input.Diet.Trim().ToLowerInvariant());
                    dietCmd.Parameters.AddWithValue("@UserID", userId);
                    await dietCmd.ExecuteNonQueryAsync();
                }

                // --- allergies / dislikes: many values, replace-all per type ---
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

        // Deletes all of this user's rows for the given pref_type, then
        // re-inserts the current list. Simplest correct approach for a
        // "save whole form" page — avoids diffing add/remove sets, and the
        // per-user row count here is always small (a handful of allergies
        // at most), so the delete+reinsert cost is negligible.
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

        // Splits "peanut,  Peanut , shellfish,," into ["peanut", "shellfish"]
        // — trims, drops empties, lowercases, and de-duplicates so the same
        // term typed twice doesn't hit the (user_id, pref_type, term)
        // primary key twice in one insert batch.
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
                ?? "server=127.0.0.1;port=3306;database=cookimate;uid=root;pwd=;";
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
