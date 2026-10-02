using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CookiMateWeb.Pages
{
    public class RecommendationModel : PageModel
    {
        private readonly IHttpClientFactory _httpClientFactory;

        private const int WidenThreshold = 5;
        public RecommendationModel(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public bool IsLoggedIn { get; set; }
        public string? ErrorMessage { get; set; }
        public string? InfoMessage { get; set; }

        public RecommendationResponse RecommendationData { get; set; } = new();

        public async Task<IActionResult> OnGetAsync()
        {
            
            string? userIdString = HttpContext.Session.GetString("UserID");

            if (string.IsNullOrEmpty(userIdString))
            {
                IsLoggedIn = false;
                InfoMessage = "Please log in to view your personalized recommendations.";
                return Page();
            }

            if (!int.TryParse(userIdString, out int userId))
            {
                HttpContext.Session.Clear();
                return RedirectToPage("/Login");
            }

            IsLoggedIn = true;

            try
            {
                var client = _httpClientFactory.CreateClient("CookiMateApi");

                var payload = new RecommendRequest
                {
                    UserId = userId,
                    TopK = WidenThreshold
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync("/recommend", content);

                if (!response.IsSuccessStatusCode)
                {
                    ErrorMessage = $"Unable to load recommendations. API returned status code {(int)response.StatusCode}.";
                    return Page();
                }

                var responseBody = await response.Content.ReadAsStringAsync();

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                RecommendationData = JsonSerializer.Deserialize<RecommendationResponse>(responseBody, options) ?? new RecommendationResponse();

                if (RecommendationData.Count == 0 && !string.IsNullOrWhiteSpace(RecommendationData.Message))
                {
                    InfoMessage = RecommendationData.Message;
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to load recommendations: {ex.Message}";
            }

            return Page();
        }

        public class RecommendRequest
        {
            [JsonPropertyName("user_id")]
            public int UserId { get; set; }

            [JsonPropertyName("top_k")]
            public int TopK { get; set; } = 5;
        }

        public class RecommendationResponse
        {
            [JsonPropertyName("user_id")]
            public int UserId { get; set; }

            [JsonPropertyName("based_on_favorites")]
            public List<int> BasedOnFavorites { get; set; } = new();

            [JsonPropertyName("based_on_rated_recipes")]
            public List<int> BasedOnRatedRecipes { get; set; } = new();

            [JsonPropertyName("count")]
            public int Count { get; set; }

            [JsonPropertyName("message")]
            public string? Message { get; set; }

            [JsonPropertyName("ranking_formula")]
            public RankingFormula? RankingFormula { get; set; }

            [JsonPropertyName("results")]
            public List<RecommendedRecipe> Results { get; set; } = new();
        }

        public class RankingFormula
        {
            [JsonPropertyName("ingredient_preference_score")]
            public double IngredientPreferenceScore { get; set; }

            [JsonPropertyName("rating_score")]
            public double RatingScore { get; set; }

            [JsonPropertyName("popularity_score")]
            public double PopularityScore { get; set; }
        }

        public class RecommendedRecipe
        {
            [JsonPropertyName("recipe_id")]
            public int RecipeId { get; set; }

            [JsonPropertyName("title")]
            public string Title { get; set; } = string.Empty;

            [JsonPropertyName("description")]
            public string? Description { get; set; }

            [JsonPropertyName("prep_time")]
            public int? PrepTime { get; set; }

            [JsonPropertyName("cook_time")]
            public int? CookTime { get; set; }

            [JsonPropertyName("servings")]
            public int? Servings { get; set; }

            [JsonPropertyName("difficulty")]
            public string? Difficulty { get; set; }

            [JsonPropertyName("image_url")]
            public string? ImageUrl { get; set; }

            [JsonPropertyName("ingredients")]
            public List<string> Ingredients { get; set; } = new();

            [JsonPropertyName("matched_ingredients")]
            public List<string> MatchedIngredients { get; set; } = new();

            [JsonPropertyName("ingredient_preference_score")]
            public double IngredientPreferenceScore { get; set; }

            [JsonPropertyName("rating_score")]
            public double RatingScore { get; set; }

            [JsonPropertyName("popularity_score")]
            public double PopularityScore { get; set; }

            [JsonPropertyName("final_score")]
            public double FinalScore { get; set; }
        }
    }
}