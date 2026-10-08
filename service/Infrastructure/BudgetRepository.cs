using LlmOps.Middleware;
using Npgsql;

namespace LlmOps.Infrastructure;

public sealed record ServiceBudget(decimal Budget, DateTime Updated, string User);

public sealed class BudgetRepository(string connectionString, PostgresOperationLogger operations,
    ILogger<BudgetRepository> logger)
{
    public Task<decimal?> GetAmount() => operations.Run<decimal?>("select", async () =>
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT budget FROM budget WHERE id = 1", db);
        return await command.ExecuteScalarAsync() is decimal amount ? amount : null;
    });

    public async Task<ServiceBudget> Upsert(decimal budget, string user)
    {
        var saved = await operations.Run("upsert", async () =>
        {
            await using var db = new NpgsqlConnection(connectionString);
            await db.OpenAsync();
            await using var transaction = await db.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO budget (id, budget, "user")
                VALUES (1, @budget, @user)
                ON CONFLICT (id) DO UPDATE
                SET budget = EXCLUDED.budget, "user" = EXCLUDED."user"
                RETURNING budget, updated, "user"
                """, db, transaction);
            command.Parameters.AddWithValue("budget", budget);
            command.Parameters.AddWithValue("user", user);

            // The migration's triggers set updated and append history atomically.
            ServiceBudget result;
            await using (var reader = await command.ExecuteReaderAsync())
            {
                if (!await reader.ReadAsync())
                    throw new InvalidOperationException("Budget write returned no row.");
                result = new ServiceBudget(reader.GetDecimal(0), reader.GetDateTime(1), reader.GetString(2));
            }

            // The upsert retains the row lock until commit, so this is its own audit entry,
            // even with concurrent writes. Avoid a racy existence check before the upsert.
            await using var audit = new NpgsqlCommand("""
                SELECT action FROM budget_history
                ORDER BY id DESC LIMIT 1
                """, db, transaction);
            var action = await audit.ExecuteScalarAsync() as string;
            if (action is not ("insert" or "update"))
                throw new InvalidOperationException("Budget audit entry is missing.");
            await transaction.CommitAsync();
            return (Value: result, Action: action);
        });

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["budget"] = saved.Value.Budget,
            ["user"] = saved.Value.User, ["operation"] = saved.Action
        }))
        {
            if (saved.Action == "insert")
                logger.LogInformation(new EventId(1300, "budget.created"), "Budget created");
            else
                logger.LogInformation(new EventId(1301, "budget.updated"), "Budget updated");
        }
        return saved.Value;
    }
}
