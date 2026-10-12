using System.Security.Cryptography;
using System.Text.Json;
using LlmOps.Middleware;
using StackExchange.Redis;

namespace LlmOps.Infrastructure;

public sealed record CachedResponse(string Content, string? FinishReason);

public sealed class RedisResponseCache(string configuration, ILogger<RedisResponseCache> logger,
    RedisAvailability availability, TimeSpan ttl, MemoryResponseCache memoryCache) : IDisposable
{
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private ConnectionMultiplexer? connection;
    private long hits;
    private long misses;

    public long Hits => Interlocked.Read(ref hits);
    public long Misses => Interlocked.Read(ref misses);

    public static string CreateKey(string model, ActivePrompt prompt, string message)
    {
        // JSON зберігає межі полів; хеш прибирає текст повідомлення з ключа Redis.
        var data = JsonSerializer.SerializeToUtf8Bytes(new[] { model, prompt.Version, prompt.Body, message });
        // Нова версія префікса виключає старі записи, створені без TTL.
        return "llmops:response:v2:" + Convert.ToHexString(SHA256.HashData(data));
    }

    public async Task<CachedResponse?> GetAsync(string key)
    {
        CachedResponse? response = null;
        var source = "redis";
        // Поки Redis недоступний, тільки фоновий монітор перевіряє відновлення.
        if (!availability.IsUnavailable)
        {
            try
            {
                var db = await GetDatabaseAsync();
                if (db is not null)
                {
                    var value = await db.StringGetAsync(key);
                    availability.ReportAvailable();
                    if (!value.IsNullOrEmpty)
                        response = JsonSerializer.Deserialize<CachedResponse>(value.ToString());
                }
            }
            catch (JsonException ex)
            {
                // Пошкоджений запис кешу не означає втрату з'єднання з Redis.
                LogFailure("deserialize", ex);
            }
            catch (Exception ex)
            {
                availability.ReportUnavailable(ex);
            }
        }

        // Memory залишається резервним джерелом і після відновлення Redis.
        if (response is null || string.IsNullOrWhiteSpace(response.Content))
        {
            response = memoryCache.Get(key);
            source = "memory";
        }

        if (response is not null && !string.IsNullOrWhiteSpace(response.Content))
        {
            Interlocked.Increment(ref hits);
            LogLookup(true, source);
            return response;
        }

        Interlocked.Increment(ref misses);
        LogLookup(false, source);
        return null;
    }

    public async Task SetAsync(string key, CachedResponse response)
    {
        if (availability.IsUnavailable)
        {
            memoryCache.Set(key, response);
            return;
        }
        try
        {
            var db = await GetDatabaseAsync();
            if (db is null)
            {
                memoryCache.Set(key, response);
                return;
            }
            await db.StringSetAsync(key, JsonSerializer.Serialize(response), expiry: ttl);
            availability.ReportAvailable();
        }
        catch (Exception ex)
        {
            availability.ReportUnavailable(ex);
            memoryCache.Set(key, response);
        }
    }

    public async Task CheckConnectionAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Монітор обходить IsUnavailable, щоб відновити кеш після збою.
            var db = await GetDatabaseAsync();
            if (db is null) return;
            await db.PingAsync().WaitAsync(stoppingToken);
            availability.ReportAvailable();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            availability.ReportUnavailable(ex);
        }
    }

    private async Task<IDatabase?> GetDatabaseAsync()
    {
        await connectionLock.WaitAsync();
        try
        {
            if (connection is null)
            {
                var options = ConfigurationOptions.Parse(configuration);
                options.AbortOnConnectFail = false;
                options.ConnectRetry = 0;
                options.ConnectTimeout = 1000;
                options.AsyncTimeout = 1000;
                options.BacklogPolicy = BacklogPolicy.FailFast;
                connection = await ConnectionMultiplexer.ConnectAsync(options);
            }

            if (!connection.IsConnected)
            {
                availability.ReportUnavailable();
                return null;
            }

            return connection.GetDatabase();
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private void LogLookup(bool hit, string source) => InfrastructureLog.Write(logger, LogLevel.Information,
        new EventId(hit ? 1300 : 1301, hit ? "cache.hit" : "cache.miss"),
        hit ? "Response cache hit" : "Response cache miss", null,
        ("dependency", source), ("cache_hits", Hits), ("cache_misses", Misses));

    private void LogFailure(string operation, Exception ex) => InfrastructureLog.Write(logger,
        LogLevel.Warning, new EventId(1302, "cache.operation.failed"), "Response cache operation failed",
        null, ("dependency", "redis"), ("operation", operation), ("error_type", ex.GetType().Name));

    public void Dispose()
    {
        connection?.Dispose();
        connectionLock.Dispose();
    }
}
