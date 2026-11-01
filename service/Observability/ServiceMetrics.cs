using LlmOps.Infrastructure;
using LlmOps.Routing;
using Prometheus;

namespace LlmOps.Observability;

public sealed class ServiceMetrics
{
    private readonly Gauge requests = Metrics.CreateGauge("llmops_requests_today", "Request rows for the current database day.");
    private readonly Gauge p95 = Metrics.CreateGauge("llmops_latency_p95_today_seconds", "Today's database p95 latency in seconds.");
    private readonly Gauge errorRatio = Metrics.CreateGauge("llmops_error_ratio_today", "Today's database fraction of non-200 statuses.");
    private readonly Gauge cost = Metrics.CreateGauge("llmops_cost_today_usd", "Today's sum of known request costs in USD.");
    private readonly Gauge databaseAvailable = Metrics.CreateGauge("llmops_database_metrics_available", "1 if database metrics were collected successfully, else 0.");
    private readonly Counter cache = Metrics.CreateCounter("llmops_cache_lookups_total", "Cache lookups since service start.", new CounterConfiguration { LabelNames = ["result"] });
    private readonly Counter fallbacks = Metrics.CreateCounter("llmops_fallback_events_total", "Model fallback transitions since service start.");
    private readonly Counter errors = Metrics.CreateCounter("llmops_error_events_total", "Error events since service start.", new CounterConfiguration { LabelNames = ["reason"] });
    private readonly Gauge providers = Metrics.CreateGauge("llmops_provider_status", "One-hot observed model route status, not an active health probe.", new GaugeConfiguration { LabelNames = ["model", "status"] });

    public ServiceMetrics(ObservabilityRepository repository, RedisResponseCache responseCache,
        ModelRouter router, ErrorCounters errorCounters, ProviderStatusService providerStatus)
    {
        Metrics.DefaultRegistry.AddBeforeCollectCallback(async cancellationToken =>
        {
            cache.WithLabels("hit").IncTo(responseCache.Hits);
            cache.WithLabels("miss").IncTo(responseCache.Misses);
            fallbacks.IncTo(router.FallbackEvents);
            foreach (var (reason, count) in errorCounters.GetSnapshot())
                errors.WithLabels(reason).IncTo(count);
            foreach (var provider in providerStatus.GetProviders())
                foreach (var status in new[] { "unknown", "ok", "degraded", "down" })
                    providers.WithLabels(provider.Name, status).Set(provider.Status == status ? 1 : 0);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var today = await repository.GetTodayMetrics(timeout.Token);
                requests.Set(today.Requests);
                p95.Set((double)today.P95Ms / 1000);
                errorRatio.Set((double)today.ErrorRatePct / 100);
                cost.Set((double)today.CostUsd);
                databaseAvailable.Set(1);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // SQL-шар уже записав помилку; відсутні дані не підміняємо нулями.
                databaseAvailable.Set(0);
                requests.Set(double.NaN);
                p95.Set(double.NaN);
                errorRatio.Set(double.NaN);
                cost.Set(double.NaN);
            }
        });
    }
}
