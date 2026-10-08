using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;

namespace CookiMateWeb.Pages
{
    public class SearchModel : PageModel
    {
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;

        public SearchModel(IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
        }

        [BindProperty(SupportsGet = true)]
        public string? Q { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? Ingredients { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? DietType { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? Cuisine { get; set; }

        public List<SearchRecipeViewModel> Recipes { get; set; } = new();

        public string? ErrorMessage { get; set; }

        public int ResultCount { get; set; }
        public bool IsFallback { get; set; }
        public string? FallbackMessage { get; set; }

        public int ExcludedByPreferences { get; set; }

        public bool HasSearched =>
            !string.IsNullOrWhiteSpace(Q) ||
            !string.IsNullOrWhiteSpace(Ingredients) ||
            !string.IsNullOrWhiteSpace(DietType) ||
            !string.IsNullOrWhiteSpace(Cuisine);

        public async Task OnGetAsync()
        {
            int? userId = TryGetUserId();
            if (!Request.Query.ContainsKey("DietType") && userId.HasValue)
            {
                DietType = await LoadSavedDietAsync(userId.Value);
            }

            if (!HasSearched)
            {
                return;
            }

            try
            {
                var ingredientFilters = string.IsNullOrWhiteSpace(Ingredients)
                    ? null
                    : Ingredients
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                var payload = new SearchRequestDto
                {
                    query = Q?.Trim() ?? string.Empty,
                    top_k = 7,
                    diet_tag = string.IsNullOrWhiteSpace(DietType) ? null : DietType.Trim(),
                    ingredient_filters = ingredientFilters,
                    cuisine = string.IsNullOrWhiteSpace(Cuisine) ? null : Cuisine.Trim(),
                    user_id = userId
                };

                var client = _httpClientFactory.CreateClient("CookiMateApi");

                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await client.PostAsync("/search", content);

                if (!response.IsSuccessStatusCode)
                {
                    ErrorMessage = $"AI search request failed with status code {(int)response.StatusCode}.";
                    return;
                }

                var responseJson = await response.Content.ReadAsStringAsync();

                var apiResponse = JsonSerializer.Deserialize<SearchApiResponseDto>(
                    responseJson,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                if (apiResponse?.results == null)
                {
                    Recipes = new List<SearchRecipeViewModel>();
                    ResultCount = 0;
                    return;
                }

                Recipes = apiResponse.results.Select(r => new SearchRecipeViewModel
                {
                    RecipeId = r.recipe_id,
                    Title = r.title ?? "Untitled Recipe",
                    Description = r.description,
                    PrepTime = r.prep_time,
                    CookTime = r.cook_time,
                    Servings = r.servings,
                    Difficulty = r.difficulty,
                    ImageUrl = r.image_url,
                    Cuisine = r.cuisine,
                    DietType = r.diet_tag,
                    FinalScore = r.final_score,
                    TfidfScore = r.tfidf_score,
                    IngredientOverlapScore = r.ingredient_overlap_score,
                    RatingScore = r.rating_score,
                    PopularityScore = r.popularity_score
                }).ToList();

                ResultCount = apiResponse.count;
                FallbackMessage = apiResponse.fallback_message;
                IsFallback = apiResponse.is_fallback;
                ExcludedByPreferences = apiResponse.excluded_by_preferences;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to load search results: {ex.Message}";
            }
        }

        private int? TryGetUserId()
        {
            string? userIdString = HttpContext.Session.GetString("UserID");
            return int.TryParse(userIdString, out int userId) ? userId : (int?)null;
        }

        private async Task<string> LoadSavedDietAsync(int userId)
        {
            try
            {
                await using var connection = new MySqlConnection(GetConnectionString());
                await connection.OpenAsync();

                const string dietSql = "SELECT diet FROM users WHERE user_id = @UserID LIMIT 1;";
                await using var cmd = new MySqlCommand(dietSql, connection);
                cmd.Parameters.AddWithValue("@UserID", userId);
                var result = await cmd.ExecuteScalarAsync();
                return result as string ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private string GetConnectionString()
        {
            return _configuration.GetConnectionString("Default")
                ?? _configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Database connection string is missing.");
        }

        public class SearchRecipeViewModel
        {
            public int RecipeId { get; set; }
            public string Title { get; set; } = string.Empty;
            public string? Description { get; set; }
            public int? PrepTime { get; set; }
            public int? CookTime { get; set; }
            public int? Servings { get; set; }
            public string? Difficulty { get; set; }
            public string? ImageUrl { get; set; }
            public string? Cuisine { get; set; }
            public string? DietType { get; set; }
            public double? FinalScore { get; set; }
            public double? TfidfScore { get; set; }
            public double? IngredientOverlapScore { get; set; }
            public double? RatingScore { get; set; }
            public double? PopularityScore { get; set; }
        }

        public class SearchRequestDto
        {
            public string query { get; set; } = string.Empty;
            public int top_k { get; set; } = 7;
            public string? diet_tag { get; set; }
            public List<string>? ingredient_filters { get; set; }
            public string? cuisine { get; set; }
            public int? user_id { get; set; }
        }

        public class SearchApiResponseDto
        {
            public string? query { get; set; }
            public string? diet_tag { get; set; }
            public List<string>? ingredient_filters { get; set; }
            public string? cuisine { get; set; }
            public int count { get; set; }
            public List<SearchResultItemDto> results { get; set; } = new();
            public bool is_fallback { get; set; }
            public string? fallback_message { get; set; }
            public int excluded_by_preferences { get; set; }
        }

        public class SearchResultItemDto
        {
            public int recipe_id { get; set; }
            public string? title { get; set; }
            public string? description { get; set; }
            public int? prep_time { get; set; }
            public int? cook_time { get; set; }
            public int? servings { get; set; }
            public string? difficulty { get; set; }
            public string? image_url { get; set; }
            public string? cuisine { get; set; }
            public string? diet_tag { get; set; }
            public double? final_score { get; set; }
            public double? tfidf_score { get; set; }
            public double? ingredient_overlap_score { get; set; }
            public double? rating_score { get; set; }
            public double? popularity_score { get; set; }
        }
    }
}