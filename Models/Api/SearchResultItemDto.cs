using System.Text.Json.Serialization;

namespace CookiMateWeb.Models.Api
{
    public class SearchResultItemDto
    {
        [JsonPropertyName("recipe_id")]
        public int RecipeId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("tfidf_score")]
        public double TfidfScore { get; set; }

        [JsonPropertyName("ingredient_overlap_score")]
        public double IngredientOverlapScore { get; set; }

        [JsonPropertyName("rating_score")]
        public double RatingScore { get; set; }

        [JsonPropertyName("favorite_count")]
        public int FavoriteCount { get; set; }

        [JsonPropertyName("popularity_score")]
        public double PopularityScore { get; set; }

        [JsonPropertyName("final_score")]
        public double FinalScore { get; set; }
    }
}