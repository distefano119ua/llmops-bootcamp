using LlmOps.Routing;

namespace LlmOps.Observability;

public sealed record ProviderStatus(string Name, string Status);

public sealed class ProviderStatusService(string defaultModel, ModelRouter router, ModelCircuitBreaker circuitBreaker)
{
    public ProviderStatus[] GetProviders()
    {
        var snapshots = circuitBreaker.GetSnapshots().ToDictionary(snapshot => snapshot.Model);
        // Обидва сценарії маршрутизації та їхні fallback, включно з ще не викликаними моделями.
        var configuredModels = new[] { router.Route("", defaultModel), router.Route("refund", defaultModel) }
            .SelectMany(router.GetFallbackChain);

        return configuredModels.Concat(snapshots.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(model => model, StringComparer.Ordinal)
            .Select(model => new ProviderStatus(model,
                snapshots.TryGetValue(model, out var snapshot) ? GetStatus(snapshot) : "unknown"))
            .ToArray();
    }

    private static string GetStatus(ModelCircuitSnapshot snapshot) => snapshot.State switch
    {
        "open" => "down",
        "half-open" => "degraded",
        _ => snapshot.LastCallSucceeded switch
        {
            true => "ok",
            false => "degraded",
            null => "unknown"
        }
    };
}
