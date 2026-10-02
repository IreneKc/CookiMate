using MySqlConnector;

namespace CookiMateWeb.Pages
{
    // Loads the same three things Profile.cshtml.cs saves (users.diet +
    // user_food_preferences), for pages that need them but don't go through
    // the FastAPI /search or /recommend endpoints (currently just Index,
    // which queries MariaDB directly). Search's own diet lookup stays inline
    // in Search_cshtml.cs since it only needs the single Diet value; this
    // helper exists for the pages that need the full set.
    public static class UserFoodPreferencesHelper
    {
        public class Prefs
        {
            public string? Diet { get; set; }
            public List<string> Allergies { get; set; } = new();
            public List<string> Dislikes { get; set; } = new();
        }

        public static async Task<Prefs> LoadAsync(MySqlConnection connection, int userId)
        {
            var prefs = new Prefs();

            const string dietSql = "SELECT diet FROM users WHERE user_id = @UserID LIMIT 1;";
            await using (var cmd = new MySqlCommand(dietSql, connection))
            {
                cmd.Parameters.AddWithValue("@UserID", userId);
                var result = await cmd.ExecuteScalarAsync();
                var diet = result as string;
                prefs.Diet = string.IsNullOrWhiteSpace(diet) ? null : diet.Trim();
            }

            const string termSql = @"
                SELECT pref_type, term FROM user_food_preferences
                WHERE user_id = @UserID;";
            await using (var cmd = new MySqlCommand(termSql, connection))
            {
                cmd.Parameters.AddWithValue("@UserID", userId);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var type = reader["pref_type"]?.ToString();
                    var term = reader["term"]?.ToString();
                    if (string.IsNullOrWhiteSpace(term))
                        continue;

                    if (type == "allergy")
                        prefs.Allergies.Add(term.Trim());
                    else if (type == "dislike")
                        prefs.Dislikes.Add(term.Trim());
                }
            }

            return prefs;
        }
    }
}