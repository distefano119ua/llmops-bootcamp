using System.Diagnostics;

namespace LlmOps.Middleware;

public sealed class PostgresOperationLogger(ILogger<PostgresOperationLogger> logger,
    PostgresAvailability availability, IHttpContextAccessor contextAccessor)
{
    public async Task<T> Run<T>(string operation, Func<Task<T>> execute)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await execute();
            InfrastructureLog.Write(logger, LogLevel.Debug, new EventId(1100, "db.operation.completed"),
                "Database operation completed", null, ("dependency", "postgres"),
                ("operation", operation), ("duration_ms", ElapsedMs(started)));
            return result;
        }
        catch (Exception ex)
        {
            if (PostgresAvailability.IsUnavailable(ex))
            {
                if (contextAccessor.HttpContext is { } context)
                    context.Items[PostgresAvailability.RequestFailureKey] = ex;
                availability.ReportUnavailable(ex);
            }
            else
                InfrastructureLog.Write(logger, LogLevel.Error, new EventId(1101, "db.operation.failed"),
                    "Database operation failed", ex, ("dependency", "postgres"),
                    ("operation", operation), ("duration_ms", ElapsedMs(started)));
            throw;
        }
    }

    private static double ElapsedMs(long started) => Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 3);
}
