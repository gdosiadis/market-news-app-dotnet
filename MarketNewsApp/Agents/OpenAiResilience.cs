using Polly;
using Polly.Retry;
using Serilog;

namespace MarketNewsApp.Agents;

internal static class OpenAiResilience
{
    public static readonly ResiliencePipeline TransportRetry = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TimeoutException>()
                .Handle<TaskCanceledException>(),
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            OnRetry = args =>
            {
                Log.Warning(
                    args.Outcome.Exception,
                    "Transient OpenAI transport failure. Retrying attempt {Attempt} after {Delay}",
                    args.AttemptNumber + 1,
                    args.RetryDelay);
                return ValueTask.CompletedTask;
            },
        })
        .Build();
}