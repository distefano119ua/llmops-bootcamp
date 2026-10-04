using LlmOps.Middleware;
using Npgsql;

namespace LlmOps.BudgetPolicy;

public sealed record BudgetModelPrice(string Model, decimal PromptTokensPrice, decimal CompletionTokensPrice)
{
    // Equal weight for input/output: both prices are USD per 1000 tokens.
    public decimal CombinedTokenPrice => (PromptTokensPrice + CompletionTokensPrice) / 1000m;
}

public sealed record BudgetSnapshot(DateOnly Day, decimal TodayUsd, decimal? BudgetUsd,
    BudgetModelPrice? RequestedPrice, BudgetModelPrice? CheapestPrice);

public sealed class BudgetPolicyStore(string connectionString, PostgresOperationLogger operations)
{
    public Task<BudgetSnapshot> ReadAsync(string model) => operations.Run("select", async () =>
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        // One statement gives budget, spend and prices from the same database snapshot.
        await using var command = new NpgsqlCommand("""
            SELECT CURRENT_DATE,
                (SELECT coalesce(sum(cost_usd), 0) FROM requests WHERE created_at::date = CURRENT_DATE),
                (SELECT budget FROM budget WHERE id = 1),
                requested.model, requested.prompt_tokens_price, requested.completion_tokens_price,
                cheapest.model, cheapest.prompt_tokens_price, cheapest.completion_tokens_price
            FROM (SELECT 1) AS state
            LEFT JOIN model_prices AS requested ON requested.model = @model
            LEFT JOIN LATERAL (
                SELECT model, prompt_tokens_price, completion_tokens_price FROM model_prices
                ORDER BY prompt_tokens_price + completion_tokens_price, model
                LIMIT 1
            ) AS cheapest ON true
            """, db);
        command.Parameters.AddWithValue("model", model);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Budget snapshot returned no row.");

        return new BudgetSnapshot(reader.GetFieldValue<DateOnly>(0), reader.GetDecimal(1),
            reader.IsDBNull(2) ? null : reader.GetDecimal(2), ReadPrice(reader, 3), ReadPrice(reader, 6));
    });

    private static BudgetModelPrice? ReadPrice(NpgsqlDataReader reader, int offset) => reader.IsDBNull(offset)
        ? null
        : new BudgetModelPrice(reader.GetString(offset), reader.GetDecimal(offset + 1), reader.GetDecimal(offset + 2));
}
