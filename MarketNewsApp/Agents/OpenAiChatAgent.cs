using System.ClientModel;
using OpenAI;
using OpenAI.Chat;
using OpenAiSdkChatMessage = OpenAI.Chat.ChatMessage;

namespace MarketNewsApp.Agents;

/// <summary>
/// OpenAI-compatible chat agent. OPENAI_ENDPOINT is optional and defaults to
/// api.openai.com; use it for a proxy that exposes the Chat Completions API.
/// </summary>
public sealed class OpenAiChatAgent : IChatAgent
{
    private readonly ChatClient _client;

    public string ProviderName => "OpenAI";

    public OpenAiChatAgent(string apiKey, string? model = null, string? endpoint = null)
    {
        var selectedModel = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
        _client = string.IsNullOrWhiteSpace(endpoint)
            ? new ChatClient(selectedModel, apiKey)
            : new ChatClient(selectedModel, new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(endpoint) });
    }

    public async Task<string> ChatAsync(List<ChatMessage> messages, int maxTokens = 4096, double temperature = 0.3)
    {
        var sdkMessages = messages.Select(message => (OpenAiSdkChatMessage)(message.Role switch
        {
            "system" => OpenAiSdkChatMessage.CreateSystemMessage(message.Content),
            "assistant" => OpenAiSdkChatMessage.CreateAssistantMessage(message.Content),
            _ => OpenAiSdkChatMessage.CreateUserMessage(message.Content),
        }));

        var completion = await _client.CompleteChatAsync(sdkMessages, new ChatCompletionOptions
        {
            MaxOutputTokenCount = maxTokens,
            Temperature = (float)temperature,
        });

        return completion.Value.Content.FirstOrDefault()?.Text ?? string.Empty;
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
