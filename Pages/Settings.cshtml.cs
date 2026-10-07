using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;

namespace CookiMateWeb.Pages
{
    public class SettingsModel : PageModel
    {
        private readonly IConfiguration _configuration;
        public const string ThemeCookieName = "cookimate_theme";

        public SettingsModel(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        [TempData]
        public string? SuccessMessage { get; set; }

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

                const string sql = "SELECT theme_preference FROM users WHERE user_id = @UserID LIMIT 1;";

                await using var cmd = new MySqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@UserID", userId);

                var result = await cmd.ExecuteScalarAsync();
                Input.Theme = (result as string) == "dark" ? "dark" : "light";
            }
            catch (Exception)
            {
                Input.Theme = "light";
            }

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            string? userIdString = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrEmpty(userIdString) || !int.TryParse(userIdString, out int userId))
            {
                return RedirectToPage("/Login");
            }

            string theme = Input.Theme == "dark" ? "dark" : "light";

            try
            {
                await using var connection = new MySqlConnection(GetConnectionString());
                await connection.OpenAsync();

                const string sql = "UPDATE users SET theme_preference = @Theme WHERE user_id = @UserID;";

                await using var cmd = new MySqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@Theme", theme);
                cmd.Parameters.AddWithValue("@UserID", userId);
                await cmd.ExecuteNonQueryAsync();

                Response.Cookies.Append(ThemeCookieName, theme, new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax
                });

                SuccessMessage = "Appearance updated.";
                return RedirectToPage("/Settings");
            }
            catch (Exception)
            {
                ModelState.AddModelError(string.Empty, "Couldn't save your appearance setting. Please try again.");
                Input.Theme = theme;
                return Page();
            }
        }

        private string GetConnectionString()
        {
            return _configuration.GetConnectionString("Default")
                ?? _configuration.GetConnectionString("DefaultConnection")
                ?? "server=127.0.0.1;port=3306;database=cookimate;uid=root;pwd=;";
        }

        public class InputModel
        {
            public string Theme { get; set; } = "light";
        }
    }
}
