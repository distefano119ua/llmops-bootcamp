namespace LlmOps.Observability;

public static class SloEndpoints
{
    public static void MapSloEndpoints(this WebApplication app)
    {
        app.MapGet("/observability/slo", async (SloService slo) =>
        {
            var report = await slo.GetReport();
            return Results.Ok(new
            {
                window_days = 7,
                target_pct = SloService.TargetPct,
                requests = report.Requests,
                errors = report.Errors,
                success_rate_pct = report.SuccessRatePct,
                allowed_errors = report.AllowedErrors,
                remaining_errors = report.RemainingErrors,
                slo_met = report.Met
            });
        });
    }
}
