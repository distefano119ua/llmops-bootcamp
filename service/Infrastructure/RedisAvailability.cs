using LlmOps.Middleware;

namespace LlmOps.Infrastructure;

public sealed class RedisAvailability(ILogger<RedisAvailability> logger)
{
    private readonly object stateLock = new();
    private bool? available;

    public bool IsUnavailable
    {
        get { lock (stateLock) return available == false; }
    }

    public void ReportAvailable()
    {
        lock (stateLock)
        {
            if (available == true) return;
            var recovered = available == false;
            available = true;
            InfrastructureLog.Write(logger, LogLevel.Information,
                new EventId(recovered ? 1312 : 1310, recovered ? "redis.recovered" : "redis.available"),
                recovered ? "Redis connection restored" : "Redis connected", null,
                ("dependency", "redis"));
        }
    }

    public void ReportUnavailable(Exception? exception = null)
    {
        lock (stateLock)
        {
            if (available == false) return;
            available = false;
            InfrastructureLog.Write(logger, LogLevel.Warning, new EventId(1311, "redis.unavailable"),
                "Redis unavailable; response cache bypassed", null,
                ("dependency", "redis"), ("error_type", exception?.GetType().Name));
        }
    }
}
