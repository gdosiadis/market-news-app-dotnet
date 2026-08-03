namespace MarketNewsApp.Agents;

/// <summary>
/// Plain data describing which AI provider to use and its credentials/config.
/// Deliberately has no knowledge of where it came from (env vars, a database,
/// a config file, ...) — that's the job of IAgentSettingsProvider implementations.
/// </summary>
public record AgentSettings
{
    // "copilot" | "groq" | "azure" | "openai" | null (= auto-detect, same rules as before)
    public string? Provider { get; init; }

    public string? CopilotModel { get; init; }

    public string? GroqApiKey { get; init; }

    public string? OpenAiApiKey { get; init; }
    public string? OpenAiModel { get; init; }
    public string? OpenAiEndpoint { get; init; }

    public string? AzureEndpoint { get; init; }
    public string? AzureApiKey { get; init; }
    public string? AzureDeployment { get; init; }
    public string? AzureApiVersion { get; init; }
}
