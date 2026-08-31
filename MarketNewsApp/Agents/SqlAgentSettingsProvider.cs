using MarketNewsApp.Data;
using MarketNewsApp.Services;

namespace MarketNewsApp.Agents;

public sealed class SqlAgentSettingsProvider(RuntimeConfiguration configuration) : IAgentSettingsProvider
{
    public async Task<AgentSettings> GetSettingsAsync()
    {
        var secrets = await new EnvAgentSettingsProvider().GetSettingsAsync();
        return secrets with
        {
            Provider = secrets.Provider ?? configuration.Agent.Provider,
            CopilotModel = configuration.Agent.CopilotModel ?? secrets.CopilotModel,
            AzureEndpoint = configuration.Agent.AzureEndpoint,
            AzureDeployment = configuration.Agent.AzureDeployment,
            AzureApiVersion = configuration.Agent.AzureApiVersion,
        };
    }
}