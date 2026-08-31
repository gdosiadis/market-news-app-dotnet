namespace MarketNewsApp.Agents;

/// <summary>
/// Default settings source — reads from process environment variables / .env
/// (via DotNetEnv, loaded in Program.cs). This is today's behavior, extracted
/// so it's just one interchangeable implementation of IAgentSettingsProvider.
/// </summary>
public sealed class EnvAgentSettingsProvider : IAgentSettingsProvider
{
    public Task<AgentSettings> GetSettingsAsync()
    {
        var settings = new AgentSettings
        {
            Provider = Environment.GetEnvironmentVariable("AI_PROVIDER")?.Trim().ToLowerInvariant(),
            CopilotModel = Environment.GetEnvironmentVariable("COPILOT_MODEL"),
            OpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            OpenAiModel = Environment.GetEnvironmentVariable("OPENAI_MODEL"),
            OpenAiEndpoint = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT"),
            AzureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
            AzureApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
            AzureDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT"),
            AzureApiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION"),
        };
        return Task.FromResult(settings);
    }
}
