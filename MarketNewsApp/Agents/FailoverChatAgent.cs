using System.Net.Sockets;

namespace MarketNewsApp.Agents;

/// <summary>
/// Uses the primary provider unless its endpoint is unreachable. Authentication and
/// response failures remain visible because retrying them through another provider
/// would conceal a configuration or content problem.
/// </summary>
public sealed class FailoverChatAgent(IChatAgent primary, IChatAgent fallback) : IChatAgent
{
    public string ProviderName => $"{primary.ProviderName} (Copilot fallback)";

    public async Task<string> ChatAsync(List<ChatMessage> messages, int maxTokens = 4096, double temperature = 0.3)
    {
        try
        {
            return await primary.ChatAsync(messages, maxTokens, temperature);
        }
        catch (Exception ex) when (IsConnectivityFailure(ex))
        {
            Console.WriteLine($"     OpenAI is unavailable ({ex.Message}); using GitHub Copilot.");
            return await fallback.ChatAsync(messages, maxTokens, temperature);
        }
    }

    private static bool IsConnectivityFailure(Exception exception) =>
        exception is SocketException or TimeoutException ||
        exception is HttpRequestException { InnerException: SocketException } ||
        exception.InnerException is not null && IsConnectivityFailure(exception.InnerException);

    public async ValueTask DisposeAsync()
    {
        await primary.DisposeAsync();
        await fallback.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}