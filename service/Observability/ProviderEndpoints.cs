namespace LlmOps.Observability;

public static class ProviderEndpoints
{
    public static void MapProviderEndpoints(this WebApplication app)
    {
        app.MapGet("/providers", (ProviderStatusService providers) =>
            Results.Ok(new { providers = providers.GetProviders() }));
    }
}
