namespace MarketNewsApp.Agents;

/// <summary>
/// A single AI chat backend (provider). Each provider (Copilot, Groq, Azure OpenAI)
/// lives in its own file under Agents/ and implements this same contract so
/// AiSummarizer can talk to whichever one is configured without caring which it is.
/// </summary>
public interface IChatAgent : IAsyncDisposable
{
    string ProviderName { get; }

    Task<string> ChatAsync(List<ChatMessage> messages, int maxTokens = 4096, double temperature = 0.3);
}

public record ChatMessage(string Role, string Content);
