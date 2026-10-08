using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using System.ComponentModel.DataAnnotations;

namespace CookiMateWeb.Pages
{
    public class AccountModel : PageModel
    {
        private readonly IConfiguration _configuration;

        public AccountModel(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [BindProperty]
        public DetailsInputModel DetailsInput { get; set; } = new();

        [BindProperty]
        public PasswordInputModel PasswordInput { get; set; } = new();

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

                const string sql = "SELECT name, email FROM users WHERE user_id = @UserID LIMIT 1;";

                await using var cmd = new MySqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@UserID", userId);

                await using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    DetailsInput.Name = reader["name"] as string ?? "";
                    DetailsInput.Email = reader["email"] as string ?? "";
                }
            }
            catch (Exception)
            {
                ModelState.AddModelError(string.Empty, "Couldn't load your account details right now.");
            }

            return Page();
        }

        public async Task<IActionResult> OnPostDetailsAsync()
        {
            string? userIdString = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrEmpty(userIdString) || !int.TryParse(userIdString, out int userId))
            {
                return RedirectToPage("/Login");
            }

            ModelState.Clear();
            if (!TryValidateModel(DetailsInput, nameof(DetailsInput)))
            {
                return Page();
            }

            try
            {
                await using var connection = new MySqlConnection(GetConnectionString());
                await connection.OpenAsync();

                const string checkEmailSql = @"
                    SELECT COUNT(*)
                    FROM users
                    WHERE LOWER(email) = LOWER(@Email) AND user_id <> @UserID;";

                await using (var checkCmd = new MySqlCommand(checkEmailSql, connection))
                {
                    checkCmd.Parameters.AddWithValue("@Email", DetailsInput.Email.Trim());
                    checkCmd.Parameters.AddWithValue("@UserID", userId);

                    var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
                    if (count > 0)
                    {
                        ModelState.AddModelError("DetailsInput.Email", "This email is already registered to another account.");
                        return Page();
                    }
                }

                const string updateSql = @"
                    UPDATE users
                    SET name = @Name, email = @Email
                    WHERE user_id = @UserID;";

                await using var updateCmd = new MySqlCommand(updateSql, connection);
                updateCmd.Parameters.AddWithValue("@Name", DetailsInput.Name.Trim());
                updateCmd.Parameters.AddWithValue("@Email", DetailsInput.Email.Trim());
                updateCmd.Parameters.AddWithValue("@UserID", userId);
                await updateCmd.ExecuteNonQueryAsync();

                HttpContext.Session.SetString("UserName", DetailsInput.Name.Trim());
                HttpContext.Session.SetString("UserEmail", DetailsInput.Email.Trim());

                SuccessMessage = "Account details updated.";
                return RedirectToPage("/Account");
            }
            catch (Exception)
            {
                ModelState.AddModelError(string.Empty, "Couldn't save your details right now. Please try again.");
                return Page();
            }
        }

        public async Task<IActionResult> OnPostPasswordAsync()
        {
            string? userIdString = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrEmpty(userIdString) || !int.TryParse(userIdString, out int userId))
            {
                return RedirectToPage("/Login");
            }

            await LoadDetailsForRedisplayAsync(userId);

            ModelState.Clear();
            if (!TryValidateModel(PasswordInput, nameof(PasswordInput)))
            {
                return Page();
            }

            try
            {
                await using var connection = new MySqlConnection(GetConnectionString());
                await connection.OpenAsync();

                const string sql = "SELECT password FROM users WHERE user_id = @UserID LIMIT 1;";

                string storedHash = "";
                await using (var cmd = new MySqlCommand(sql, connection))
                {
                    cmd.Parameters.AddWithValue("@UserID", userId);
                    var result = await cmd.ExecuteScalarAsync();
                    storedHash = result as string ?? "";
                }

                bool currentPasswordValid = !string.IsNullOrEmpty(storedHash)
                    && BCrypt.Net.BCrypt.Verify(PasswordInput.CurrentPassword, storedHash);

                if (!currentPasswordValid)
                {
                    ModelState.AddModelError("PasswordInput.CurrentPassword", "Current password is incorrect.");
                    return Page();
                }

                string newHash = BCrypt.Net.BCrypt.HashPassword(PasswordInput.NewPassword);

                const string updateSql = "UPDATE users SET password = @Password WHERE user_id = @UserID;";

                await using var updateCmd = new MySqlCommand(updateSql, connection);
                updateCmd.Parameters.AddWithValue("@Password", newHash);
                updateCmd.Parameters.AddWithValue("@UserID", userId);
                await updateCmd.ExecuteNonQueryAsync();

                SuccessMessage = "Password updated.";
                return RedirectToPage("/Account");
            }
            catch (Exception)
            {
                ModelState.AddModelError(string.Empty, "Couldn't update your password right now. Please try again.");
                return Page();
            }
        }

        private async Task LoadDetailsForRedisplayAsync(int userId)
        {
            try
            {
                await using var connection = new MySqlConnection(GetConnectionString());
                await connection.OpenAsync();

                const string sql = "SELECT name, email FROM users WHERE user_id = @UserID LIMIT 1;";

                await using var cmd = new MySqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@UserID", userId);

                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    DetailsInput.Name = reader["name"] as string ?? "";
                    DetailsInput.Email = reader["email"] as string ?? "";
                }
            }
            catch (Exception)
            {

            }
        }

        private string GetConnectionString()
        {
            return _configuration.GetConnectionString("Default")
                ?? _configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Database connection string is missing.");
        }

        public class DetailsInputModel
        {
            [Required(ErrorMessage = "Full name is required.")]
            [StringLength(100, ErrorMessage = "Name cannot exceed 100 characters.")]
            public string Name { get; set; } = string.Empty;

            [Required(ErrorMessage = "Email is required.")]
            [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
            [StringLength(255, ErrorMessage = "Email cannot exceed 255 characters.")]
            public string Email { get; set; } = string.Empty;
        }

        public class PasswordInputModel
        {
            [Required(ErrorMessage = "Current password is required.")]
            [DataType(DataType.Password)]
            public string CurrentPassword { get; set; } = string.Empty;

            [Required(ErrorMessage = "New password is required.")]
            [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters.")]
            [DataType(DataType.Password)]
            public string NewPassword { get; set; } = string.Empty;

            [Required(ErrorMessage = "Please confirm your new password.")]
            [Compare("NewPassword", ErrorMessage = "Passwords do not match.")]
            [DataType(DataType.Password)]
            public string ConfirmNewPassword { get; set; } = string.Empty;
        }
    }
}
