using Npgsql;
using LlmOps.Middleware;

namespace LlmOps.Infrastructure;

public sealed record ActivePrompt(string Body, string Version);
public sealed record PromptInfo(string Name, string Version, bool Active);
public sealed record PromptActivation(long Id, string PromptName, string Version,
    string Actor, DateTime ActivatedAt);

public sealed class PromptRepository(string connectionString, PostgresOperationLogger operations,
    ILogger<PromptRepository> logger)
{
    private readonly object registryStateLock = new();
    private bool? hasActivePrompt;

    private void ObserveRegistry(bool active, bool forceLog = false)
    {
        lock (registryStateLock)
        {
            if (hasActivePrompt == active && !forceLog) return;
            if (!active)
                logger.LogWarning(new EventId(1200, "registry.no_active_prompt"), "No active prompt");
            else if (hasActivePrompt == false)
                logger.LogInformation(new EventId(1201, "registry.active_prompt.restored"), "Active prompt restored");
            else
                logger.LogInformation(new EventId(1202, "registry.active_prompt.available"), "Active prompt available");
            hasActivePrompt = active;
        }
    }

    // Reuse the monitor's bounded connection. Read only registry state, not prompt content.
    public async Task CheckRegistryAsync(NpgsqlConnection db, CancellationToken cancellationToken,
        bool forceLog = false)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM prompts WHERE name = @name AND active = true)", db);
        command.Parameters.AddWithValue("name", "support");
        var active = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        ObserveRegistry(active, forceLog);
    }

    public Task<bool> ActivatePrompt(string version, string actor) => operations.Run("update", async () =>
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
    });

    public Task<IReadOnlyList<PromptActivation>> ListActivations() => operations.Run<IReadOnlyList<PromptActivation>>("select", async () =>
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
    });

    public Task<IReadOnlyList<PromptInfo>> ListPrompts() => operations.Run<IReadOnlyList<PromptInfo>>("select", async () =>
    {
        var prompts = new List<PromptInfo>();
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT name, version, active FROM prompts ORDER BY name, created_at, version", db);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            prompts.Add(new PromptInfo(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));

        ObserveRegistry(prompts.Any(prompt => prompt.Name == "support" && prompt.Active));
        return prompts;
    });

    // "none" означає успішне читання реєстру без активного промпта.
    // Помилка БД проходить через operations.Run до HTTP middleware як 503.
    public Task<ActivePrompt> GetActivePrompt() => operations.Run("select", async () =>
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT body, version FROM prompts WHERE name = @name AND active = true LIMIT 1", db);
        cmd.Parameters.AddWithValue("name", "support");
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            ObserveRegistry(true);
            return new ActivePrompt(reader.GetString(0), reader.GetString(1));
        }
        ObserveRegistry(false);
        return new ActivePrompt("You are an assistant.", "none");
    });
}
