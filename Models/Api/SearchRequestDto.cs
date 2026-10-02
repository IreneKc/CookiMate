using System.Text.Json.Serialization;

namespace CookiMateWeb.Models.Api
{
    public class SearchRequestDto
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;

        [JsonPropertyName("top_k")]
        public int TopK { get; set; } = 10;

        [JsonPropertyName("diet_tag")]
        public string? DietTag { get; set; }

        [JsonPropertyName("ingredient_filters")]
        public List<string>? IngredientFilters { get; set; }
    }
}