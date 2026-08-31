using System.Text.Json;

namespace MarketNewsApp.Agents;

/// <summary>
/// Azure OpenAI / Azure AI Foundry chat agent. Requires AZURE_OPENAI_ENDPOINT,
/// AZURE_OPENAI_API_KEY and AZURE_OPENAI_DEPLOYMENT.
/// </summary>
public sealed class AzureOpenAiChatAgent : IChatAgent
{
    private readonly HttpClient _http;
    private readonly string _deployment;
    private readonly string _apiVersion;

    public string ProviderName => "Azure OpenAI";

    public AzureOpenAiChatAgent(string endpoint, string apiKey, string deployment, string apiVersion)
    {
        _deployment = deployment;
        _apiVersion = apiVersion;
        _http = new HttpClient { BaseAddress = new Uri(EnsureTrailingSlash(endpoint)), Timeout = TimeSpan.FromMinutes(8) };
        _http.DefaultRequestHeaders.Add("api-key", apiKey);
    }

    public async Task<string> ChatAsync(List<ChatMessage> messages, int maxTokens = 4096, double temperature = 0.3)
    {
        var request = new
        {
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            temperature,
            max_tokens = maxTokens,
        };

        var json = JsonSerializer.Serialize(request);
        var path = $"openai/deployments/{_deployment}/chat/completions?api-version={_apiVersion}";
        return await OpenAiResilience.TransportRetry.ExecuteAsync(async _ =>
        {
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(path, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Azure OpenAI API error: {response.StatusCode} - {errorBody}", null, response.StatusCode);
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        });
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
}
