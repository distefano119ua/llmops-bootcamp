using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace LlmOps.Middleware;

public sealed class LoggingMiddleware(RequestDelegate next,
    ILogger<LoggingMiddleware> logger, IOptionsMonitor<InfrastructureLoggingOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.TraceIdentifier = Guid.NewGuid().ToString();
        context.Response.Headers["X-Request-ID"] = context.TraceIdentifier;
        // Quiet polling; known database outages use the shared availability event.
        // Other errors and mutations remain visible.
        var quietPath = HttpMethods.IsGet(context.Request.Method)
            && options.CurrentValue.ExcludedPaths.Contains(context.Request.Path.Value,
                StringComparer.OrdinalIgnoreCase);

        var started = Stopwatch.GetTimestamp();
        if (!quietPath)
            logger.LogDebug(new EventId(1000, "http.request.received"), "HTTP request received");
        try
        {
            await next(context);
            var level = context.Response.StatusCode >= 500 ? LogLevel.Error
                : context.Response.StatusCode >= 400 ? LogLevel.Warning : LogLevel.Information;
            if (!quietPath || level >= LogLevel.Warning)
                InfrastructureLog.Write(logger, level, new EventId(1001, "http.response.completed"),
                    "Response completed", null, ("status_code", context.Response.StatusCode),
                    ("duration_ms", ElapsedMs(started)));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            InfrastructureLog.Write(logger, LogLevel.Warning, new EventId(1002, "http.request.aborted"),
                "Request aborted", null, ("duration_ms", ElapsedMs(started)));
            throw;
        }
        catch (Exception ex) when (!context.Response.HasStarted
            && context.Items.TryGetValue(PostgresAvailability.RequestFailureKey, out var databaseError)
            && ReferenceEquals(databaseError, ex))
        {
            // The database layer already emitted the shared availability transition.
            // Handle only errors identified there, never unrelated HTTP or filesystem errors.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Database unavailable",
                request_id = context.TraceIdentifier
            }, cancellationToken: context.RequestAborted);
            if (!quietPath)
                InfrastructureLog.Write(logger, LogLevel.Warning, new EventId(1001, "http.response.completed"),
                    "Response completed", null, ("status_code", context.Response.StatusCode),
                    ("duration_ms", ElapsedMs(started)), ("dependency", "postgres"));
        }
        catch (Exception ex)
        {
            // An exception is not a completed response. Preserve ASP.NET Core error handling.
            InfrastructureLog.Write(logger, LogLevel.Error, new EventId(1003, "http.request.failed"),
                "Request failed", ex, ("duration_ms", ElapsedMs(started)));
            throw;
        }
    }

    private static double ElapsedMs(long started) => Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 3);
}
