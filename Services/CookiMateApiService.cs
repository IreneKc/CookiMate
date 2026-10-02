using System.Text;
using System.Text.Json;
using CookiMateWeb.Models.Api;

namespace CookiMateWeb.Services
{
    public class CookiMateApiService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly JsonSerializerOptions _jsonOptions;

        public CookiMateApiService(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        //public async Task<SearchResponseDto?> SearchAsync(SearchRequestDto request)
        //{
        //    var client = _httpClientFactory.CreateClient("CookiMateApi");
        //    var json = JsonSerializer.Serialize(request);
        //    var content = new StringContent(json, Encoding.UTF8, "application/json");

        //    var response = await client.PostAsync("/search", content);

        //    var responseJson = await response.Content.ReadAsStringAsync();

        //    Console.WriteLine("Search API status: " + response.StatusCode);
        //    Console.WriteLine("Search API response: " + responseJson);

        //    if (!response.IsSuccessStatusCode)
        //        return null;

        //    return JsonSerializer.Deserialize<SearchResponseDto>(responseJson, _jsonOptions);
        //}
        public async Task<SearchResponseDto?> SearchAsync(SearchRequestDto request)
        {
            var client = _httpClientFactory.CreateClient("CookiMateApi");
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("/search", content);

            if (!response.IsSuccessStatusCode)
                return null;

            var responseJson = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<SearchResponseDto>(responseJson, _jsonOptions);
        }

        public async Task<RecommendResponseDto?> RecommendAsync(RecommendRequestDto request)
        {
            var client = _httpClientFactory.CreateClient("CookiMateApi");
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("/recommend", content);

            if (!response.IsSuccessStatusCode)
                return null;

            var responseJson = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<RecommendResponseDto>(responseJson, _jsonOptions);
        }
    }
}