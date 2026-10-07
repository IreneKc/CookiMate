using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using System.ComponentModel.DataAnnotations;

namespace CookiMateWeb.Pages
{
    public class RegisterModel : PageModel
    {
        private readonly IConfiguration _configuration;

        public RegisterModel(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        [TempData]
        public string? SuccessMessage { get; set; }

        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            string connectionString = _configuration.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

            try
            {
                await using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();

                const string checkEmailSql = @"
                    SELECT COUNT(*)
                    FROM users
                    WHERE LOWER(email) = LOWER(@Email);";

                await using (var checkCmd = new MySqlCommand(checkEmailSql, connection))
                {
                    checkCmd.Parameters.AddWithValue("@Email", Input.Email.Trim());

                    var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

                    if (count > 0)
                    {
                        ModelState.AddModelError(string.Empty, "This email is already registered.");
                        return Page();
                    }
                }

                string hashedPassword = BCrypt.Net.BCrypt.HashPassword(Input.Password);

                const string insertSql = @"
                    INSERT INTO users (name, email, password)
                    VALUES (@Name, @Email, @Password);";

                await using (var insertCmd = new MySqlCommand(insertSql, connection))
                {
                    insertCmd.Parameters.AddWithValue("@Name", Input.Name.Trim());
                    insertCmd.Parameters.AddWithValue("@Email", Input.Email.Trim());
                    insertCmd.Parameters.AddWithValue("@Password", hashedPassword);

                    int rowsAffected = await insertCmd.ExecuteNonQueryAsync();

                    if (rowsAffected <= 0)
                    {
                        ModelState.AddModelError(string.Empty, "Registration failed. Please try again.");
                        return Page();
                    }
                }

                TempData["SuccessMessage"] = "Account created successfully. Please login.";
                return RedirectToPage("/Login");
            }
            catch (Exception)
            {
                ModelState.AddModelError(string.Empty, "An unexpected error occurred. Please try again.");
                return Page();
            }
        }

        public class InputModel
        {
            [Required(ErrorMessage = "Full name is required.")]
            [StringLength(100, ErrorMessage = "Name cannot exceed 100 characters.")]
            public string Name { get; set; } = string.Empty;

            [Required(ErrorMessage = "Email is required.")]
            [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
            [StringLength(255, ErrorMessage = "Email cannot exceed 255 characters.")]
            public string Email { get; set; } = string.Empty;

            [Required(ErrorMessage = "Password is required.")]
            [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters.")]
            [DataType(DataType.Password)]
            public string Password { get; set; } = string.Empty;

            [Required(ErrorMessage = "Please confirm your password.")]
            [Compare("Password", ErrorMessage = "Passwords do not match.")]
            [DataType(DataType.Password)]
            public string ConfirmPassword { get; set; } = string.Empty;
        }
    }
}