using LlmOps.Middleware;

namespace LlmOps.BudgetPolicy;

public sealed record BudgetDecision(string? Model, BudgetModelPrice? Price);

public sealed class BudgetPolicyEvaluator(BudgetPolicyStore store, ILogger<BudgetPolicyEvaluator> logger)
{
    public const decimal Threshold = 0.8m;
    private readonly object stateLock = new();
    private DateOnly? observedDay;
    private decimal? observedBudget;
    private bool thresholdReached;
    private bool pricesUnavailable;

    public async Task<BudgetDecision> EvaluateAsync(string requestedModel)
    {
        var snapshot = await store.ReadAsync(requestedModel);
        // Compare raw decimals, before rounding for the UI or log fields.
        var reached = snapshot.BudgetUsd is { } budget && snapshot.TodayUsd >= budget * Threshold;
        var chosen = snapshot.RequestedPrice;
        if (reached && snapshot.CheapestPrice is { } cheapest
            && (chosen is null || cheapest.CombinedTokenPrice < chosen.CombinedTokenPrice))
            chosen = cheapest;

        var unavailable = reached && chosen is null;
        var model = unavailable ? null : chosen?.Model ?? requestedModel;
        Observe(snapshot, reached, unavailable, requestedModel, model);
        if (reached && model is not null && model != requestedModel)
            InfrastructureLog.Write(logger, LogLevel.Debug, new EventId(1403, "budget.model.degraded"),
                "Cheaper model selected", null, ("requested_model", requestedModel), ("selected_model", model));
        return new BudgetDecision(model, chosen);
    }

    private void Observe(BudgetSnapshot snapshot, bool reached, bool unavailable,
        string requestedModel, string? selectedModel)
    {
        lock (stateLock)
        {
            // Rearm the alert for a new database day or a different budget amount.
            var changed = observedDay != snapshot.Day || observedBudget != snapshot.BudgetUsd;
            observedDay = snapshot.Day;
            observedBudget = snapshot.BudgetUsd;

            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["today_usd"] = snapshot.TodayUsd, ["budget"] = snapshot.BudgetUsd,
                ["threshold_percent"] = Threshold * 100m,
                ["usage_percent"] = snapshot.BudgetUsd is > 0
                    ? Math.Round(snapshot.TodayUsd / snapshot.BudgetUsd.Value * 100m, 2) : null,
                ["requested_model"] = requestedModel, ["selected_model"] = selectedModel
            });
            if (reached && (!thresholdReached || changed))
                logger.LogWarning(new EventId(1400, "budget.threshold_reached"), "Budget threshold reached");
            else if (!reached && thresholdReached)
                logger.LogInformation(new EventId(1401, "budget.threshold_cleared"), "Budget threshold cleared");
            if (unavailable && (!pricesUnavailable || changed))
                logger.LogWarning(new EventId(1402, "budget.prices_unavailable"), "Model prices unavailable");
            thresholdReached = reached;
            pricesUnavailable = unavailable;
        }
    }
}
