using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using Polly;
using Polly.Retry;
using Serilog;

namespace MarketNewsApp.Services;

internal static class SmtpResilience
{
    public static readonly ResiliencePipeline ConnectionRetry = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder()
                .Handle<SocketException>()
                .Handle<IOException>()
                .Handle<TimeoutException>()
                .Handle<TaskCanceledException>()
                .Handle<SmtpProtocolException>(),
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            OnRetry = args =>
            {
                Log.Warning(
                    args.Outcome.Exception,
                    "Transient SMTP connection failure. Retrying attempt {Attempt} after {Delay}",
                    args.AttemptNumber + 1,
                    args.RetryDelay);
                return ValueTask.CompletedTask;
            },
        })
        .Build();
}