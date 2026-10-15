namespace LlmOps.Infrastructure;

public sealed class RedisHealthMonitor(RedisResponseCache cache) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Перша перевірка не затримує запуск HTTP-сервісу.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await cache.CheckConnectionAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
