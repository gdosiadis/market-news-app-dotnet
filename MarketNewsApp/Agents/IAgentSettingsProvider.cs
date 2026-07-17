namespace MarketNewsApp.Agents;

/// <summary>
/// Supplies the AgentSettings used to pick/construct a chat agent. Implement this
/// against whatever backing store you want (env vars today, a database or remote
/// config service tomorrow) without touching ChatAgentFactory or the agents themselves.
/// </summary>
public interface IAgentSettingsProvider
{
    Task<AgentSettings> GetSettingsAsync();
}
