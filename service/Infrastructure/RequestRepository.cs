using Npgsql;

namespace LlmOps.Infrastructure;

public sealed class RequestRepository(string connectionString)
{
    // Помилка запису лога не перериває відповідь користувачу.
    public async Task LogRequest(Guid id, string model, string promptVersion, int latency,
        int promptTokens, int completionTokens, decimal? cost, int status, string? finishReason)
    {
        try
        {
            await using var db = new NpgsqlConnection(connectionString);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO requests (request_id, model, prompt_version, latency_ms, prompt_tokens, completion_tokens, cost_usd, status, finish_reason) "
                + "VALUES (@id, @model, @prompt_version, @lat, @pt, @ct, @cost, @status, @finish_reason)", db);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("model", model);
            cmd.Parameters.AddWithValue("prompt_version", promptVersion);
            cmd.Parameters.AddWithValue("lat", latency);
            cmd.Parameters.AddWithValue("pt", promptTokens);
            cmd.Parameters.AddWithValue("ct", completionTokens);
            cmd.Parameters.AddWithValue("cost", (object?)cost ?? DBNull.Value);
            cmd.Parameters.AddWithValue("status", status.ToString());
            cmd.Parameters.AddWithValue("finish_reason", (object?)finishReason ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cannot log request: {ex.Message}");
        }
    }
}
