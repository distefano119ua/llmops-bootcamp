using Prometheus;

namespace LlmOps.Observability;

public static class MetricsEndpoints
{
    public static void MapServiceMetrics(this WebApplication app)
    {
        // Створюємо singleton і реєструємо callback до першого scrape.
        app.Services.GetRequiredService<ServiceMetrics>();
        app.MapMetrics("/metrics");
    }
}
