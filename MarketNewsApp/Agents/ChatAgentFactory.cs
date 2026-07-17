namespace MarketNewsApp.Agents;

/// <summary>
/// Picks and constructs the configured IChatAgent from an AgentSettings value.
/// Knows nothing about where settings came from — pass an IAgentSettingsProvider
/// (env vars today, a database tomorrow) to CreateAsync, or call Create(settings)
/// directly if you already have an AgentSettings instance.
/// </summary>
public static class ChatAgentFactory
{
    public static async Task<IChatAgent> CreateAsync(IAgentSettingsProvider? settingsProvider = null)
    {
        settingsProvider ??= new EnvAgentSettingsProvider();
        var settings = await settingsProvider.GetSettingsAsync();
        return Create(settings);
    }

    public static IChatAgent Create(AgentSettings settings)
    {
        var provider = settings.Provider;

        // Azure OpenAI (explicit or fully-configured fallback)
        if (provider != "copilot" && provider != "groq" &&
            !string.IsNullOrWhiteSpace(settings.AzureEndpoint) &&
            !string.IsNullOrWhiteSpace(settings.AzureApiKey) &&
            !string.IsNullOrWhiteSpace(settings.AzureDeployment))
        {
            return new AzureOpenAiChatAgent(
                settings.AzureEndpoint, settings.AzureApiKey, settings.AzureDeployment,
                string.IsNullOrWhiteSpace(settings.AzureApiVersion) ? "2024-10-21" : settings.AzureApiVersion);
        }

        // Groq (explicit via AI_PROVIDER=groq)
        if (provider == "groq" && !string.IsNullOrWhiteSpace(settings.GroqApiKey))
        {
            return new GroqChatAgent(settings.GroqApiKey);
        }

        // GitHub Copilot SDK (default) — uses the logged-in Copilot user,
        // routed through GitHub endpoints that the corporate firewall allows.
        return new CopilotChatAgent(settings.CopilotModel);
    }
}
