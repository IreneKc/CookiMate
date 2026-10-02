using System.Text.Json.Serialization;

namespace CookiMateWeb.Models.Api
{
    public class RecommendResponseDto
    {
        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("based_on_favorites")]
        public List<int> BasedOnFavorites { get; set; } = new();

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("results")]
        public List<RecommendResultItemDto> Results { get; set; } = new();
    }
}