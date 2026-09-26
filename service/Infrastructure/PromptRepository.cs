using Npgsql;

namespace LlmOps.Infrastructure;

public sealed record ActivePrompt(string Body, string Version);
public sealed record PromptInfo(string Name, string Version, bool Active);
public sealed record PromptActivation(long Id, string PromptName, string Version,
    string Actor, DateTime ActivatedAt);

public sealed class PromptRepository(string connectionString)
{
    public async Task<bool> ActivatePrompt(string version, string actor)
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "WITH changed AS ("
            + "UPDATE prompts SET active = (version = @version) "
            + "WHERE name = @name AND EXISTS ("
            + "SELECT 1 FROM prompts WHERE name = @name AND version = @version) "
            + "RETURNING name) "
            + "INSERT INTO prompt_activations (prompt_name, version, actor) "
            + "SELECT @name, @version, @actor WHERE EXISTS (SELECT 1 FROM changed) "
            + "RETURNING id", db);
        cmd.Parameters.AddWithValue("name", "support");
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("actor", actor);

        // Зміна активної версії й запис історії — одна атомарна SQL-команда.
        // Невідома версія не змінить реєстр і не створить запис історії.
        return await cmd.ExecuteScalarAsync() is not null;
    }

    public async Task<IReadOnlyList<PromptActivation>> ListActivations()
    {
        var activations = new List<PromptActivation>();
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, prompt_name, version, actor, activated_at "
            + "FROM prompt_activations ORDER BY activated_at DESC, id DESC", db);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            activations.Add(new PromptActivation(reader.GetInt64(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetDateTime(4)));

        return activations;
    }

    public async Task<IReadOnlyList<PromptInfo>> ListPrompts()
    {
        var prompts = new List<PromptInfo>();
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT name, version, active FROM prompts ORDER BY name, created_at, version", db);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            prompts.Add(new PromptInfo(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));

        return prompts;
    }

    // Без активного промпта використовуємо видимий дефолт без "support".
    public async Task<ActivePrompt> GetActivePrompt()
    {
        var fallback = new ActivePrompt("You are an assistant.", "none");
        try
        {
            await using var db = new NpgsqlConnection(connectionString);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT body, version FROM prompts WHERE name = @name AND active = true LIMIT 1", db);
            cmd.Parameters.AddWithValue("name", "support");
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return new ActivePrompt(reader.GetString(0), reader.GetString(1));
        }
        catch (NpgsqlException ex)
        {
            Console.Error.WriteLine($"Cannot load active prompt: {ex.Message}");
        }

        return fallback;
    }
}
