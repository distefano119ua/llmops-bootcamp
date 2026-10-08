// Сервіс — це наш control plane. LiteLLM тільки ходить у модель, а рішення
// (яку модель брати, коли ретраїти, скільки коштує) робимо тут.
// MODEL=mock — дефолт, грошей не треба. MODEL=gpt-5-mini або claude-haiku + ключ у gateway/.env — реальна модель.

using System.Text;
using System.Text.Json;
using LlmOps.Infrastructure;
using LlmOps.Middleware;
using LlmOps.Routing;
using LlmOps.Endpoints;
using LlmOps.BudgetPolicy;

var builder = WebApplication.CreateBuilder(args);
builder.AddInfrastructureLogging(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ModelRouter>();

// налаштування беремо з оточення (задаються в docker-compose.yml)
var gateway = Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://gateway:4000";
var dbConn = Environment.GetEnvironmentVariable("DB_CONN")
    ?? "Host=postgres;Database=llmops;Username=llmops;Password=llmops";
var defaultModel = Environment.GetEnvironmentVariable("MODEL") ?? "mock";
builder.Services.AddSingleton<PromptRepository>(services =>
    new PromptRepository(dbConn, services.GetRequiredService<PostgresOperationLogger>(),
        services.GetRequiredService<ILogger<PromptRepository>>()));
builder.Services.AddSingleton<RequestRepository>(services =>
    new RequestRepository(dbConn, services.GetRequiredService<PostgresOperationLogger>()));
builder.Services.AddSingleton<BudgetRepository>(services =>
    new BudgetRepository(dbConn, services.GetRequiredService<PostgresOperationLogger>(),
        services.GetRequiredService<ILogger<BudgetRepository>>()));
builder.Services.AddSingleton<ModelPriceRepository>(services =>
    new ModelPriceRepository(dbConn, services.GetRequiredService<PostgresOperationLogger>(),
        services.GetRequiredService<ILogger<ModelPriceRepository>>()));
builder.Services.AddSingleton<BudgetPolicyStore>(services =>
    new BudgetPolicyStore(dbConn, services.GetRequiredService<PostgresOperationLogger>()));
builder.Services.AddSingleton<BudgetPolicyEvaluator>();
builder.Services.AddHostedService<PostgresHealthMonitor>(services =>
    new PostgresHealthMonitor(dbConn, services.GetRequiredService<ILogger<PostgresHealthMonitor>>(),
        services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<InfrastructureLoggingOptions>>(),
        services.GetRequiredService<PromptRepository>(), services.GetRequiredService<PostgresAvailability>()));
var app = builder.Build();
app.UseMiddleware<LoggingMiddleware>();
app.MapBudgetEndpoints();
app.MapModelPriceEndpoints();

app.MapPost("/chat", async (
    ChatIn body, 
    IHttpClientFactory httpFactory,
    PromptRepository prompts, 
    RequestRepository requests, 
    BudgetPolicyEvaluator budgetPolicy,
    HttpContext context, 
    ModelRouter router) =>
{
    var requestId = Guid.Parse(context.TraceIdentifier);
    var startedAt = DateTimeOffset.UtcNow;

    // guardrails (W4): тут перевірити вхід на PII / інʼєкції. поки нічого.
    // TODO(student, W4)

    // routing (W2): обираємо модель за задачею.
    var model = router.Route(body.Message, defaultModel);
    var decision = await budgetPolicy.EvaluateAsync(model);
    if (decision.Model is null)
        return Results.Json(new { error = "Model prices unavailable", request_id = requestId }, statusCode: 503);
    model = decision.Model;
    var pr = decision.Price;

    // Беремо активний промпт і його версію з реєстру.
    var prompt = await prompts.GetActivePrompt();
    var systemPrompt = prompt.Body;

    // cache (W3): перед викликом глянути в Redis — раптом вже відповідали
    // TODO(student, W3)

    // fallback (W4): якщо тут 429/5xx — піти на іншого провайдера. поки один виклик.
    // TODO(student, W4)
    var payload = JsonSerializer.Serialize(new
    {
        model,
        messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = body.Message }
        }
    });

    var http = httpFactory.CreateClient();
    var answer = "";
    string? toolCall = null;
    string? finishReason = null;
    int promptTokens = 0, completionTokens = 0, status = 0; // 0 = відповіді не було
    var usageAvailable = false;
    try
    {
        var response = await http.PostAsync(
            $"{gateway}/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        status = (int)response.StatusCode;
        var rawJson = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(rawJson);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        if (choice.TryGetProperty("finish_reason", out var reason)
            && reason.ValueKind == JsonValueKind.String)
            finishReason = reason.GetString();
        answer = message.GetProperty("content").GetString() ?? "";

        // tools + HITL (W3/W4): якщо модель попросила інструмент — виконати;
        // перед незворотною дією (створити тікет) спитати людину. поки лише читаємо назву.
        // TODO(student, W3/W4)
        if (message.TryGetProperty("tool_calls", out var tools)
            && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
        {
            toolCall = tools[0].GetProperty("function").GetProperty("name").GetString();
        }

        var usage = doc.RootElement.GetProperty("usage");
        promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
        completionTokens = usage.GetProperty("completion_tokens").GetInt32();
        usageAvailable = true;
    }
    catch
    {
        // мережа/gateway недоступні або відповідь не розпарсилась (status лишиться 0/5xx).
        // TODO(student, W4): тут краще graceful degradation
        answer = "Сервіс тимчасово недоступний.";
    }

    var latencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

    // Prices are USD per 1000 tokens. Missing prices/usage mean unknown cost.
    decimal? costUsd = pr is not null && usageAvailable
        ? Math.Round(promptTokens / 1000m * pr.PromptTokensPrice
            + completionTokens / 1000m * pr.CompletionTokensPrice, 6)
        : null;

    // лог кожного запиту — з цього живе observability (W1) і cost (W2)
    await requests.LogRequest(requestId, model, prompt.Version, latencyMs, promptTokens, completionTokens, costUsd, status, finishReason);

    return Results.Json(new { request_id = requestId, content = answer, tool = toolCall, latency_ms = latencyMs });
});

// ці ендпоінти читає готова консоль. поверни потрібну форму — картки оживуть.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));                                    // ліфнес, не для консолі

app.MapGet("/observability", () => Results.Json(new { todo = "aggregate from requests table" }));  // W5: { p95_ms, requests, cache_hit_pct, error_rate_pct, fallback_events }
app.MapGet("/cost", async (RequestRepository requests, BudgetRepository budgets) =>
{
    var today = await requests.GetTodayCost();
    var budget = await budgets.GetAmount();
    return Results.Json(new
    {
        today_usd = Math.Round(today, 4),
        budget_usd = budget
    });
});

// W1: [ { name, version, active } ]
app.MapGet("/prompts", async (PromptRepository prompts) =>
    Results.Json(await prompts.ListPrompts()));
app.MapGet("/prompts/activations", async (PromptRepository prompts) =>
    Results.Json(await prompts.ListActivations()));
app.MapPost("/prompts/{version}/activate", async (string version, HttpContext context,
    PromptRepository prompts) =>
{
    var actor = context.Request.Headers["X-Actor"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(actor))
        actor = "unknown";

    if (!await prompts.ActivatePrompt(version, actor))
        return (IResult)Results.NotFound();

    return Results.Ok(new { version, active = true });
});
app.MapGet("/providers", () => Results.Json(new { todo = "provider health" }));                    // W5: { providers: [ { name, status } ] }
app.MapGet("/approvals", () => Results.Json(new { todo = "pending HITL approvals" }));              // W4: { pending: [ { id, action } ] }

app.Run("http://0.0.0.0:8080");

record ChatIn(string Message);
