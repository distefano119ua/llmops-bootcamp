namespace LlmOps.Observability;

public sealed record SloReport(long Requests, long Errors, decimal? SuccessRatePct,
    long AllowedErrors, long RemainingErrors, bool? Met);

public sealed class SloService(ObservabilityRepository repository)
{
    public const decimal TargetPct = 99m;

    public async Task<SloReport> GetReport()
    {
        var counts = await repository.GetWeeklyRequestCounts();
        var errors = counts.Requests - counts.Successful;
        var allowedErrors = (long)decimal.Floor(counts.Requests * (100m - TargetPct) / 100m);
        return new SloReport(counts.Requests, errors,
            counts.Requests == 0 ? null : Math.Round(100m * counts.Successful / counts.Requests, 2),
            allowedErrors, allowedErrors - errors,
            // Оцінюємо без округлення відсотка, щоб не приховати перевищення бюджету.
            counts.Requests == 0 ? null : errors <= allowedErrors);
    }
}
