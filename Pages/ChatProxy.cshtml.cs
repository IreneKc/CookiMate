using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CookiMateWeb.Pages
{
    [IgnoreAntiforgeryToken]
    public class ChatProxyModel : PageModel
    {
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;

        public ChatProxyModel(IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<IActionResult> OnPostAsync()
        {
            string rawBody;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                rawBody = await reader.ReadToEndAsync();
            }

            string? message = null;
            if (!string.IsNullOrWhiteSpace(rawBody))
            {
                try
                {
                    using var doc = JsonDocument.Parse(rawBody);
                    if (doc.RootElement.TryGetProperty("message", out var m))
                    {
                        message = m.GetString();
                    }
                }
                catch (JsonException)
                {
                    return new JsonResult(new { error = "Invalid request body." }) { StatusCode = 400 };
                }
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return new JsonResult(new { error = "Message is required." }) { StatusCode = 400 };
            }

            try
            {
                var baseUrl =
                    _configuration["ApiSettings:BaseUrl"]
                    ?? "http://127.0.0.1:8000";

                var client = _httpClientFactory.CreateClient();

                var payload = JsonSerializer.Serialize(new { message = message.Trim() });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var response = await client.PostAsync($"{baseUrl}/assistant/message", content);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return new JsonResult(new
                    {
                        error = $"Assistant request failed with status code {(int)response.StatusCode}."
                    })
                    { StatusCode = 502 };
                }

                return Content(responseJson, "application/json");
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = $"Assistant unavailable: {ex.Message}" })
                { StatusCode = 502 };
            }
        }
    }
}