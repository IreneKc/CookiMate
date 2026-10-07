using MySqlConnector;

namespace CookiMateWeb.Pages
{
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