using LlmOps.Infrastructure;

namespace LlmOps.Endpoints;

public sealed record BudgetInput(decimal? Budget, string? User);

public static class BudgetEndpoints
{
    // PostgreSQL NUMERIC(18, 6): twelve integer digits, six fractional digits.
    private const decimal MaximumBudget = 999_999_999_999.999999m;

    public static void MapBudgetEndpoints(this WebApplication app)
    {
        app.MapPut("/budget", async (BudgetInput body,
            BudgetRepository budgets) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(body.User))
                errors["user"] = ["User is required."];
            if (body.Budget is null)
                errors["budget"] = ["Budget is required."];
            else if (body.Budget < 0 || body.Budget > MaximumBudget)
                errors["budget"] = ["Budget must be between 0 and 999999999999.999999 USD."];
            else if (decimal.Round(body.Budget.Value, 6) != body.Budget.Value)
                errors["budget"] = ["Budget must have at most six decimal places."];

            if (errors.Count > 0)
                return (IResult)Results.ValidationProblem(errors);

            var result = await budgets.Upsert(body.Budget!.Value, body.User!.Trim());
            return Results.Ok(result);
        });
    }
}
