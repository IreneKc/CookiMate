using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CookiMateWeb.Pages
{
    // Same-origin JSON proxy for the chat widget. The browser POSTs { message }
    // here; this handler forwards it server-side to the FastAPI AI engine's
    // /assistant/message endpoint and streams the JSON response straight back.
    //
    // Why a proxy (not a direct browser -> :8000 fetch):
    //   * Consistency — Search/Recommend already broker AI-engine calls through
    //     C# HttpClient (see Search.cshtml.cs). This keeps every AI call on the
    //     same server-side path.
    //   * No CORS — the browser only ever talks to its own origin.
    //   * The FastAPI engine stays unexposed to the public/browser.
    //
    // [IgnoreAntiforgeryToken]: this is a non-mutating, read-only guidance query
    // (no DB writes, no state change), called via fetch from the widget without a
    // form token. Safe to exempt. Do NOT copy this attribute onto any handler
    // that writes data.
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
            // Read the raw JSON body ourselves rather than relying on [FromBody]
            // model binding, which behaves inconsistently on Razor Page handlers.
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

                // Pass the FastAPI JSON through unchanged.
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