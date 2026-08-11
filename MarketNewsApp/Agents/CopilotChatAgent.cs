using System.Text.Json;

namespace MarketNewsApp.Agents;

/// <summary>
/// GitHub Copilot SDK chat agent (default provider) — uses the logged-in Copilot
/// session, no API key needed, and is reachable even when corporate firewalls
/// block api.groq.com / OpenAI endpoints.
/// </summary>
public sealed class CopilotChatAgent : IChatAgent
{
    // Transient errors seen under this environment's corporate TLS-inspecting proxy:
    // concurrent session.create calls occasionally fail the CLI's internal GitHub
    // auth check even though the credentials are valid. A short retry clears it up
    // almost always, since it's a proxy/connection hiccup, not a real auth problem.
    // "this organization has been disabled" has also been observed to be one of these
    // flaky hiccups (confirmed by the same source succeeding again on a later run
    // within minutes with no account/config changes in between) rather than a genuine,
    // persistent account/org suspension — so it's retried too instead of failing fast.
    private static readonly string[] TransientCopilotErrors =
    [
        "fetch oauth user login",
        "network fetch failed",
        "communication error with copilot cli",
        "this organization has been disabled",
    ];

    // Corporate TLS-inspecting proxies (e.g. Fortinet) re-sign HTTPS traffic with a
    // private root CA that's trusted by Windows but not by Node's bundled CA store,
    // which breaks the Copilot CLI's fetch() calls to api.github.com. If a CA bundle
    // has been exported to this path, point Node at it so the CLI trusts the proxy.
    private static readonly string CorporateCaBundlePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarketNewsApp", "corporate-ca-bundle.pem");

    private readonly string? _model;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private GitHub.Copilot.CopilotClient? _client;

    public string ProviderName => "GitHub Copilot";

    public CopilotChatAgent(string? model)
    {
        _model = model;
    }

    private static void EnsureCorporateCaTrust()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODE_EXTRA_CA_CERTS")))
            return;
        if (File.Exists(CorporateCaBundlePath))
            Environment.SetEnvironmentVariable("NODE_EXTRA_CA_CERTS", CorporateCaBundlePath);
    }

    private async Task<GitHub.Copilot.CopilotClient> GetClientAsync()
    {
        if (_client is not null) return _client;
        await _initLock.WaitAsync();
        try
        {
            if (_client is null)
            {
                EnsureCorporateCaTrust();
                var client = new GitHub.Copilot.CopilotClient();
                await client.StartAsync();
                _client = client;
            }
        }
        finally { _initLock.Release(); }
        return _client;
    }

    public async Task<string> ChatAsync(List<ChatMessage> messages, int maxTokens = 4096, double temperature = 0.3)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ChatAttemptAsync(messages);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientError(ex.Message))
            {
                var delay = TimeSpan.FromMilliseconds(1500 * attempt);
                Console.WriteLine($"     ⏳  Transient Copilot error (attempt {attempt}/{maxAttempts}), retrying in {delay.TotalSeconds:F1}s...");
                await Task.Delay(delay);
            }
        }
    }

    private static bool IsTransientError(string message)
    {
        var normalizedMessage = message.ToLowerInvariant();
        return !normalizedMessage.Contains("model ") &&
               !normalizedMessage.Contains("is not available") &&
               TransientCopilotErrors.Any(error => normalizedMessage.Contains(error));
    }

    private async Task<string> ChatAttemptAsync(List<ChatMessage> messages)
    {
        var client = await GetClientAsync();

        var systemContent = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
        var userContent = string.Join("\n\n", messages.Where(m => m.Role != "system").Select(m => m.Content));

        var config = new GitHub.Copilot.SessionConfig
        {
            OnPermissionRequest = GitHub.Copilot.PermissionHandler.ApproveAll,
        };
        if (!string.IsNullOrWhiteSpace(_model))
            config.Model = _model;
        if (!string.IsNullOrWhiteSpace(systemContent))
            config.SystemMessage = new GitHub.Copilot.SystemMessageConfig
            {
                Mode = GitHub.Copilot.SystemMessageMode.Append,
                Content = systemContent,
            };

        await using var session = await client.CreateSessionAsync(config);

        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buffer = new System.Text.StringBuilder();

        using var sub = session.On<GitHub.Copilot.SessionEvent>(evt =>
        {
            switch (evt)
            {
                case GitHub.Copilot.AssistantMessageEvent msg:
                    buffer.Clear();
                    buffer.Append(msg.Data.Content);
                    break;
                case GitHub.Copilot.SessionErrorEvent err:
                    done.TrySetException(new InvalidOperationException($"Copilot session error: {err.Data.Message}"));
                    break;
                case GitHub.Copilot.SessionIdleEvent:
                    done.TrySetResult(buffer.ToString());
                    break;
            }
        });

        await session.SendAsync(new GitHub.Copilot.MessageOptions { Prompt = userContent });
        return await done.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            try { await _client.StopAsync(); }
            catch { try { await _client.ForceStopAsync(); } catch { } }
            _client = null;
        }
        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
