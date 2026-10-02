using System.Text.Json.Serialization;

namespace CookiMateWeb.Models.Api
{
    public class SearchResponseDto
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;

        [JsonPropertyName("diet_tag")]
        public string? DietTag { get; set; }

        [JsonPropertyName("ingredient_filters")]
        public List<string>? IngredientFilters { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("results")]
        public List<SearchResultItemDto> Results { get; set; } = new();
    }
}