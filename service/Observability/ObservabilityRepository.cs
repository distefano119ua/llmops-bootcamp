using LlmOps.Middleware;
using Npgsql;

namespace LlmOps.Observability;

public sealed record TodayMetrics(long Requests, decimal P95Ms, decimal ErrorRatePct);
public sealed record WeeklyRequestCounts(long Requests, long Successful);

public sealed class ObservabilityRepository(string connectionString, PostgresOperationLogger operations)
{
    public Task<WeeklyRequestCounts> GetWeeklyRequestCounts() => operations.Run("select", async () =>
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*), count(*) FILTER (WHERE status = '200')
            FROM requests
            WHERE created_at >= CURRENT_TIMESTAMP - INTERVAL '168 hours'
              AND created_at < CURRENT_TIMESTAMP
            """, db);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new WeeklyRequestCounts(reader.GetInt64(0), reader.GetInt64(1));
    });

    public Task<TodayMetrics> GetTodayMetrics() => operations.Run("select", async () =>
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                count(*) AS requests,
                coalesce(round(
                    (percentile_cont(0.95) WITHIN GROUP (ORDER BY latency_ms))::numeric,
                    1), 0) AS p95_ms,
                coalesce(round(
                    avg(CASE WHEN status <> '200' THEN 1.0 ELSE 0 END) * 100,
                    1), 0) AS error_rate_pct
            FROM requests
            WHERE created_at::date = CURRENT_DATE
            """, db);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new TodayMetrics(reader.GetInt64(0), reader.GetDecimal(1), reader.GetDecimal(2));
    });
}
