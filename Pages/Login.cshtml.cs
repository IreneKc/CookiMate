using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;

namespace CookiMateWeb.Pages
{
    public class LoginModel : PageModel
    {
        private readonly IConfiguration _configuration;

        public LoginModel(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [BindProperty]
        public LoginInputModel Input { get; set; } = new();

        [TempData]
        public string? SuccessMessage { get; set; }

        public void OnGet()
        {
        }

        public IActionResult OnPost()
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            try
            {
                string? connStr =
                    _configuration.GetConnectionString("Default")
                    ?? _configuration.GetConnectionString("DefaultConnection");

                if (string.IsNullOrWhiteSpace(connStr))
                {
                    ModelState.AddModelError(string.Empty, "Connection string was not found.");
                    return Page();
                }

                using var conn = new MySqlConnection(connStr);
                conn.Open();

                const string query = @"
                    SELECT user_id, name, email, password, role
                    FROM users
                    WHERE email = @Email
                    LIMIT 1;";

                string userId = "";
                string userName = "";
                string userEmail = "";
                string userRole = "";
                string storedPassword = "";

                using (var cmd = new MySqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Email", Input.Email.Trim());

                    using var reader = cmd.ExecuteReader();

                    if (!reader.Read())
                    {
                        ModelState.AddModelError(string.Empty, "Invalid email or password.");
                        return Page();
                    }

                    userId = reader["user_id"]?.ToString() ?? "";
                    userName = reader["name"]?.ToString() ?? "";
                    userEmail = reader["email"]?.ToString() ?? "";
                    userRole = reader["role"]?.ToString() ?? "";
                    storedPassword = reader["password"]?.ToString() ?? "";
                }

                bool isPasswordValid = false;
                bool needsUpgradeToHash = false;

                if (!string.IsNullOrWhiteSpace(storedPassword) &&
                    (storedPassword.StartsWith("$2a$") ||
                     storedPassword.StartsWith("$2b$") ||
                     storedPassword.StartsWith("$2y$")))
                {
                    isPasswordValid = BCrypt.Net.BCrypt.Verify(Input.Password, storedPassword);
                }
                else
                {
                    isPasswordValid = Input.Password == storedPassword;
                    needsUpgradeToHash = isPasswordValid;
                }

                if (!isPasswordValid)
                {
                    ModelState.AddModelError(string.Empty, "Invalid email or password.");
                    return Page();
                }

                if (needsUpgradeToHash)
                {
                    string newHashedPassword = BCrypt.Net.BCrypt.HashPassword(Input.Password);

                    const string upgradePasswordQuery = @"
                        UPDATE users
                        SET password = @Password
                        WHERE user_id = @UserID;";

                    using var upgradeCmd = new MySqlCommand(upgradePasswordQuery, conn);
                    upgradeCmd.Parameters.AddWithValue("@Password", newHashedPassword);
                    upgradeCmd.Parameters.AddWithValue("@UserID", userId);
                    upgradeCmd.ExecuteNonQuery();
                }

                HttpContext.Session.SetString("UserID", userId);
                HttpContext.Session.SetString("UserName", userName);
                HttpContext.Session.SetString("UserEmail", userEmail);
                HttpContext.Session.SetString("UserRole", userRole);

                const string updateLastLoginQuery = @"
                    UPDATE users
                    SET last_login = NOW()
                    WHERE user_id = @UserID;";

                using (var updateCmd = new MySqlCommand(updateLastLoginQuery, conn))
                {
                    updateCmd.Parameters.AddWithValue("@UserID", userId);
                    updateCmd.ExecuteNonQuery();
                }

                return RedirectToPage("/Index");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, "Login failed: " + ex.Message);
                return Page();
            }
        }

        public class LoginInputModel
        {
            [Required(ErrorMessage = "Email is required.")]
            [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
            public string Email { get; set; } = string.Empty;

            [Required(ErrorMessage = "Password is required.")]
            [DataType(DataType.Password)]
            public string Password { get; set; } = string.Empty;
        }
    }
}