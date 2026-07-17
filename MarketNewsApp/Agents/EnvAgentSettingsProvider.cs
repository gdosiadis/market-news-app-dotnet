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
            GroqApiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY"),
            AzureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
            AzureApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
            AzureDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT"),
            AzureApiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION"),
        };
        return Task.FromResult(settings);
    }
}
