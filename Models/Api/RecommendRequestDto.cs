using System.Text.Json.Serialization;

namespace CookiMateWeb.Models.Api
{
    public class RecommendRequestDto
    {
        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("top_k")]
        public int TopK { get; set; } = 10;
    }
}