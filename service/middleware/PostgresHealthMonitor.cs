using Microsoft.Extensions.Options;
using Npgsql;
using LlmOps.Infrastructure;

namespace LlmOps.Middleware;

public sealed class PostgresHealthMonitor(string connectionString,
    ILogger<PostgresHealthMonitor> logger,
    IOptionsMonitor<InfrastructureLoggingOptions> options,
    PromptRepository prompts, PostgresAvailability availability) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var registryCheckFailed = false;
        var registrySnapshotPending = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = options.CurrentValue;
            var timeoutSeconds = Math.Max(1, settings.DatabaseHealthTimeoutSeconds);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                // A dedicated bounded probe, independent of user traffic and operation logs.
                var connection = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    Timeout = timeoutSeconds,
                    CommandTimeout = timeoutSeconds,
                    Pooling = false
                };
                await using (var db = new NpgsqlConnection(connection.ConnectionString))
                {
                    await db.OpenAsync(timeout.Token);
                    await using var command = new NpgsqlCommand("SELECT 1", db);
                    await command.ExecuteScalarAsync(timeout.Token);
                    registrySnapshotPending |= availability.ReportAvailable();

                    try
                    {
                        await prompts.CheckRegistryAsync(db, timeout.Token, registrySnapshotPending);
                        registrySnapshotPending = false;
                        if (registryCheckFailed)
                            InfrastructureLog.Write(logger, LogLevel.Information,
                                new EventId(1204, "registry.check.recovered"), "Registry check recovered");
                        registryCheckFailed = false;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex) when (PostgresAvailability.IsUnavailable(ex))
                    {
                        // The connection can fail after SELECT 1 succeeded. Report only
                        // database availability through the outer handler, not registry state.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Missing registry schema is distinct from unavailable PostgreSQL.
                        // Do not infer an empty registry from a failed read.
                        if (!registryCheckFailed)
                            InfrastructureLog.Write(logger, LogLevel.Error,
                                new EventId(1203, "registry.check.failed"), "Registry check failed", ex);
                        registryCheckFailed = true;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                availability.ReportUnavailable(ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1,
                    options.CurrentValue.DatabaseHealthIntervalSeconds)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
