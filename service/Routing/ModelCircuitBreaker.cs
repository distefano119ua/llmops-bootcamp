using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using LlmOps.Infrastructure;
using LlmOps.Middleware;

namespace LlmOps.Routing;

public sealed record ModelCircuitSnapshot(string Model, string State, bool? LastCallSucceeded);

public sealed class ModelCircuitBreaker(bool halfOpenEnabled, ILogger<ModelCircuitBreaker> logger,
    TimeProvider? timeProvider = null)
{
    public const int FailureThreshold = 3;
    public static readonly TimeSpan OpenDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ProbeDelay = TimeSpan.FromSeconds(5);

    private enum Phase { Closed, Open, HalfOpen }
    private sealed class Breaker
    {
        public Phase Phase;
        public int Failures;
        public long OpenedAt;
        public long Generation;
        public bool? LastCallSucceeded;
    }

    private readonly ConcurrentDictionary<string, Breaker> breakers = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    // Знімок не запускає проб і не змінює стан breaker.
    public ModelCircuitSnapshot[] GetSnapshots() => breakers.Select(entry =>
    {
        lock (entry.Value)
            return new ModelCircuitSnapshot(entry.Key,
                entry.Value.Phase switch
                {
                    Phase.Open => "open",
                    Phase.HalfOpen => "half-open",
                    _ => "closed"
                }, entry.Value.LastCallSucceeded);
    }).ToArray();

    public async Task<GatewayResult> ExecuteAsync(string model, Func<Task<GatewayResult>> call)
    {
        var breaker = breakers.GetOrAdd(model, _ => new Breaker());
        long generation;
        lock (breaker)
        {
            if (breaker.Phase != Phase.Closed)
            {
                var elapsed = clock.GetElapsedTime(breaker.OpenedAt);
                if (breaker.Phase == Phase.HalfOpen
                    || elapsed < (halfOpenEnabled ? ProbeDelay : OpenDuration))
                {
                    InfrastructureLog.Write(logger, LogLevel.Information,
                        new EventId(1505, "circuit.skipped"), "Model skipped by circuit breaker", null,
                        ("model", model), ("error_type", breaker.Phase == Phase.HalfOpen
                            ? "probe_in_flight" : "circuit_open"));
                    return GatewayResult.Failed(503, "circuit_open");
                }

                breaker.Generation++;
                breaker.Phase = halfOpenEnabled ? Phase.HalfOpen : Phase.Closed;
                if (!halfOpenEnabled) breaker.Failures = 0;
                LogTransition(model, breaker.Phase, "cooldown_elapsed");
            }
            generation = breaker.Generation;
        }

        try
        {
            var result = await call();
            lock (breaker)
            {
                // Запізніла відповідь старого запиту не змінює новий цикл breaker.
                if (generation == breaker.Generation)
                {
                    breaker.LastCallSucceeded = result.Ok;
                    if (result.Ok)
                    {
                        breaker.Failures = 0;
                        if (breaker.Phase == Phase.HalfOpen)
                        {
                            breaker.Phase = Phase.Closed;
                            breaker.Generation++;
                            LogTransition(model, Phase.Closed, "probe_succeeded");
                        }
                    }
                    else if (breaker.Phase == Phase.HalfOpen
                        || (result.CanFallback && ++breaker.Failures >= FailureThreshold))
                    {
                        breaker.Phase = Phase.Open;
                        breaker.OpenedAt = clock.GetTimestamp();
                        breaker.Generation++;
                        LogTransition(model, Phase.Open, result.Error ?? "request_failed");
                    }
                    else if (!result.CanFallback)
                    {
                        breaker.Failures = 0;
                    }
                }
            }
            return result;
        }
        finally
        {
            lock (breaker)
            {
                // Скасований або аварійно завершений probe звільняє єдиний слот.
                if (generation == breaker.Generation && breaker.Phase == Phase.HalfOpen)
                {
                    breaker.Phase = Phase.Open;
                    breaker.Generation++;
                    LogTransition(model, Phase.Open, "probe_interrupted");
                }
            }
        }
    }

    private void LogTransition(string model, Phase phase, string reason)
    {
        var (id, name, message) = phase switch
        {
            Phase.Open => (1502, "circuit.open", "Circuit opened"),
            Phase.HalfOpen => (1503, "circuit.half_open", "Circuit probe started"),
            _ => (1504, "circuit.closed", "Circuit closed")
        };
        InfrastructureLog.Write(logger, phase == Phase.Open ? LogLevel.Warning : LogLevel.Information,
            new EventId(id, name), message, null, ("model", model), ("error_type", reason));
    }
}
