using System.Text.Json;

namespace MarketNewsApp.Agents;

/// <summary>
/// Groq AI chat agent. Requires GROQ_API_KEY. Note: api.groq.com may be blocked by
/// corporate firewalls that allow only GitHub endpoints — see CopilotChatAgent.
/// </summary>
public sealed class GroqChatAgent : IChatAgent
{
    private static readonly string[] Models =
    [
        "llama-3.3-70b-versatile",
        "llama-3.1-8b-instant",
        "openai/gpt-oss-120b",
        "openai/gpt-oss-20b",
        "meta-llama/llama-4-scout-17b-16e-instruct",
        "qwen/qwen3-32b",
    ];

    private static readonly string[] SkipErrors =
        ["rate_limit", "429", "decommissioned", "deprecated", "context_length", "context window", "too long", "bad_request"];

    private readonly HttpClient _http;

    public string ProviderName => "Groq";

    public GroqChatAgent(string apiKey)
    {
        _http = new HttpClient { BaseAddress = new Uri("https://api.groq.com/"), Timeout = TimeSpan.FromMinutes(8) };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public async Task<string> ChatAsync(List<ChatMessage> messages, int maxTokens = 4096, double temperature = 0.3)
    {
        foreach (var model in Models)
        {
            try
            {
                var request = new
                {
                    model,
                    messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                    temperature,
                    max_tokens = maxTokens,
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await _http.PostAsync("openai/v1/chat/completions", content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = (await response.Content.ReadAsStringAsync()).ToLower();
                    if (SkipErrors.Any(e => errorBody.Contains(e)))
                    {
                        Console.WriteLine($"  ⚠️  {model} unavailable ({response.StatusCode}), trying next...");
                        continue;
                    }
                    throw new HttpRequestException($"Groq API error: {response.StatusCode} - {errorBody}");
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                return doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "";
            }
            catch (HttpRequestException) { throw; }
            catch (Exception ex)
            {
                var err = ex.Message.ToLower();
                if (SkipErrors.Any(e => err.Contains(e)))
                {
                    Console.WriteLine($"  ⚠️  {model} unavailable ({ex.GetType().Name}), trying next...");
                    continue;
                }
                throw;
            }
        }
        throw new InvalidOperationException("All Groq models rate-limited. Try again later or upgrade tier at https://console.groq.com/settings/billing");
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
