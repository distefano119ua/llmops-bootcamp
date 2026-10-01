using System.Net.Sockets;
using Npgsql;

namespace LlmOps.Middleware;

// Shared by probes and application operations: one event per availability transition.
public sealed class PostgresAvailability(ILogger<PostgresAvailability> logger)
{
    internal static readonly object RequestFailureKey = new();
    private readonly object stateLock = new();
    private bool? available;

    public void ReportUnavailable(Exception exception)
    {
        lock (stateLock)
        {
            if (available == false) return;
            available = false;
            InfrastructureLog.Write(logger, LogLevel.Error, new EventId(1111, "db.unavailable"),
                "Database unavailable", exception, ("dependency", "postgres"));
        }
    }

    // True means a new availability period began: registry state must be reported again.
    public bool ReportAvailable()
    {
        lock (stateLock)
        {
            if (available == true) return false;
            var recovered = available == false;
            available = true;
            InfrastructureLog.Write(logger, LogLevel.Information,
                new EventId(1110, recovered ? "db.recovered" : "db.available"),
                recovered ? "Database recovered" : "Database available", null, ("dependency", "postgres"));
            return true;
        }
    }

    public static bool IsUnavailable(Exception exception) => exception switch
    {
        PostgresException error => error.SqlState.StartsWith("08", StringComparison.Ordinal)
            || error.SqlState is "57P01" or "57P02" or "57P03" or "57P04" or "57P05",
        NpgsqlException => true,
        SocketException => true,
        IOException => true,
        TimeoutException => true,
        OperationCanceledException => true,
        _ => false
    };
}
