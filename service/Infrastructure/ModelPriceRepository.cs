using System.Text.Json.Serialization;
using LlmOps.Middleware;
using Npgsql;

namespace LlmOps.Infrastructure;

public sealed record ModelPrice(string Model,
    [property: JsonPropertyName("completion_tokens_price")] decimal CompletionTokensPrice,
    [property: JsonPropertyName("prompt_tokens_price")] decimal PromptTokensPrice,
    DateTime Updated, string User);

public sealed class ModelPriceRepository(string connectionString, PostgresOperationLogger operations,
    ILogger<ModelPriceRepository> logger)
{
    public async Task<ModelPrice?> GetByModel(string model)
    {
        return await operations.Run("select", async () =>
        {
            await using var db = new NpgsqlConnection(connectionString);
            await db.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT model, completion_tokens_price, prompt_tokens_price, updated, "user"
                FROM model_prices WHERE model = @model
                """, db);
            command.Parameters.AddWithValue("model", model);
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync()
                ? new ModelPrice(reader.GetString(0), reader.GetDecimal(1), reader.GetDecimal(2),
                    reader.GetDateTime(3), reader.GetString(4))
                : null;
        });
    }

    public async Task<ModelPrice> Upsert(string model, decimal completionTokensPrice,
        decimal promptTokensPrice, string user)
    {
        var saved = await operations.Run("upsert", async () =>
        {
            await using var db = new NpgsqlConnection(connectionString);
            await db.OpenAsync();
            await using var transaction = await db.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO model_prices (model, completion_tokens_price, prompt_tokens_price, "user")
                VALUES (@model, @completion_price, @prompt_price, @user)
                ON CONFLICT (model) DO UPDATE
                SET completion_tokens_price = EXCLUDED.completion_tokens_price,
                    prompt_tokens_price = EXCLUDED.prompt_tokens_price,
                    "user" = EXCLUDED."user"
                RETURNING model, completion_tokens_price, prompt_tokens_price, updated, "user"
                """, db, transaction);
            command.Parameters.AddWithValue("model", model);
            command.Parameters.AddWithValue("completion_price", completionTokensPrice);
            command.Parameters.AddWithValue("prompt_price", promptTokensPrice);
            command.Parameters.AddWithValue("user", user);

            // Migration 002 supplies the timestamp and the history entry in the same transaction.
            ModelPrice result;
            await using (var reader = await command.ExecuteReaderAsync())
            {
                if (!await reader.ReadAsync())
                    throw new InvalidOperationException("Model price write returned no row.");
                result = new ModelPrice(reader.GetString(0), reader.GetDecimal(1), reader.GetDecimal(2),
                    reader.GetDateTime(3), reader.GetString(4));
            }

            // Keep the upsert row lock while reading the action recorded by its trigger.
            await using var audit = new NpgsqlCommand("""
                SELECT action FROM model_price_history
                WHERE model = @model ORDER BY id DESC LIMIT 1
                """, db, transaction);
            audit.Parameters.AddWithValue("model", model);
            var action = await audit.ExecuteScalarAsync() as string;
            if (action is not ("insert" or "update"))
                throw new InvalidOperationException("Model price audit entry is missing.");
            await transaction.CommitAsync();
            return (Value: result, Action: action);
        });

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["model"] = saved.Value.Model, ["user"] = saved.Value.User,
            ["completion_tokens_price"] = saved.Value.CompletionTokensPrice,
            ["prompt_tokens_price"] = saved.Value.PromptTokensPrice, ["operation"] = saved.Action
        }))
        {
            if (saved.Action == "insert")
                logger.LogInformation(new EventId(1310, "model_price.created"), "Model prices created");
            else
                logger.LogInformation(new EventId(1311, "model_price.updated"), "Model prices updated");
        }
        return saved.Value;
    }
}
