// Сервіс — це наш control plane. LiteLLM тільки ходить у модель, а рішення
// (яку модель брати, коли ретраїти, скільки коштує) робимо тут.
// MODEL=mock — дефолт, грошей не треба. MODEL=gpt-5-mini або claude-haiku + ключ у gateway/.env — реальна модель.

using LlmOps.Infrastructure;
using LlmOps.Middleware;
using LlmOps.Routing;
using LlmOps.Endpoints;
using LlmOps.BudgetPolicy;
using LlmOps.Tools;

var builder = WebApplication.CreateBuilder(args);
builder.AddInfrastructureLogging(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ModelRouter>();
builder.Services.AddSingleton<ToolRegistry>();

// налаштування беремо з оточення (задаються в docker-compose.yml)
var gateway = Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://gateway:4000";
var dbConn = Environment.GetEnvironmentVariable("DB_CONN")
    ?? "Host=postgres;Database=llmops;Username=llmops;Password=llmops";
var defaultModel = Environment.GetEnvironmentVariable("MODEL") ?? "mock";
var halfOpenSetting = Environment.GetEnvironmentVariable("circuit-breaker-half-open") ?? "true";
if (!bool.TryParse(halfOpenSetting, out var halfOpenEnabled))
    throw new InvalidOperationException("circuit-breaker-half-open must be true or false.");
builder.Services.AddSingleton<ModelCircuitBreaker>(services =>
    new ModelCircuitBreaker(halfOpenEnabled, services.GetRequiredService<ILogger<ModelCircuitBreaker>>()));
var redisConnection = Environment.GetEnvironmentVariable("REDIS_CONN") ?? "redis:6379";
var redisCacheTtlSetting = Environment.GetEnvironmentVariable("ttl-redis-cache") ?? "60";
if (!int.TryParse(redisCacheTtlSetting, out var redisCacheTtlSeconds) || redisCacheTtlSeconds <= 0)
    throw new InvalidOperationException("ttl-redis-cache must be a positive number of seconds.");
var redisCacheTtl = TimeSpan.FromSeconds(redisCacheTtlSeconds);
builder.Services.AddSingleton<RedisAvailability>();
builder.Services.AddSingleton<MemoryResponseCache>(_ => new MemoryResponseCache(redisCacheTtl));
builder.Services.AddSingleton<RedisResponseCache>(services =>
    new RedisResponseCache(redisConnection, services.GetRequiredService<ILogger<RedisResponseCache>>(),
        services.GetRequiredService<RedisAvailability>(), redisCacheTtl,
        services.GetRequiredService<MemoryResponseCache>()));
builder.Services.AddHostedService<RedisHealthMonitor>();
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
    ModelPriceRepository prices,
    RequestRepository requests, 
    RedisResponseCache cache,
    ToolRegistry toolRegistry,
    BudgetPolicyEvaluator budgetPolicy,
    HttpContext context, 
    ModelCircuitBreaker circuitBreaker,
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
    var cacheKey = RedisResponseCache.CreateKey(model, prompt, body.Message);
    var cached = await cache.GetAsync(cacheKey);
    if (cached is not null)
    {
        var cachedLatencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        await requests.LogRequest(requestId, model, prompt.Version, cachedLatencyMs,
            0, 0, 0m, 200, cached.FinishReason);
        return Results.Json(new
        {
            request_id = requestId, content = cached.Content, tool = (string?)null,
            latency_ms = cachedLatencyMs
        });
    }

    var chain = router.GetFallbackChain(model);
    var http = httpFactory.CreateClient();
    var gatewayResult = GatewayResult.Failed(0, "not_called");
    for (var i = 0; i < chain.Length; i++)
    {
        var previousResult = gatewayResult;
        model = chain[i];
        gatewayResult = await circuitBreaker.ExecuteAsync(model, async () =>
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            if (i > 0)
            {
                var transitions = router.RecordFallback();
                InfrastructureLog.Write(app.Logger, LogLevel.Warning,
                    new EventId(1500, "gateway.fallback"), "Switching to fallback model", null,
                    ("requested_model", chain[i - 1]), ("selected_model", chain[i]),
                    ("status_code", previousResult.Status), ("error_type", previousResult.Error),
                    ("fallback_events", transitions));
            }

            return await GatewayClient.CallGateway(http, gateway, model, systemPrompt,
                body.Message, context.RequestAborted);
        });
        if (gatewayResult.Ok || !gatewayResult.CanFallback) break;
    }

    var answer = gatewayResult.Ok ? gatewayResult.Answer
        : "Вибачте, тимчасово не можемо обробити ваш запит. Спробуйте, будь ласка, пізніше.";
    var pendingToolCalls = gatewayResult.ToolCalls;
    var toolCall = pendingToolCalls.Count > 0 ? pendingToolCalls[0].Name : null;
    var finishReason = gatewayResult.FinishReason;
    var promptTokens = gatewayResult.PromptTokens;
    var completionTokens = gatewayResult.CompletionTokens;
    var usageAvailable = gatewayResult.UsageAvailable;
    // HTTP 200 з непридатним JSON також має бути помилкою у метриках запитів.
    var status = !gatewayResult.Ok && (gatewayResult.Status is >= 200 and < 300)
        ? 502 : gatewayResult.Status;

    if (!gatewayResult.Ok)
        InfrastructureLog.Write(app.Logger, LogLevel.Warning,
            new EventId(1501, "gateway.failed"), "Gateway request failed", null,
            ("model", model), ("status_code", gatewayResult.Status),
            ("error_type", gatewayResult.Error));

    if (gatewayResult.Ok && model != decision.Model)
    {
        var actualPrice = await prices.GetByModel(model);
        pr = actualPrice is null ? null : new BudgetModelPrice(actualPrice.Model,
            actualPrice.PromptTokensPrice, actualPrice.CompletionTokensPrice);
        cacheKey = RedisResponseCache.CreateKey(model, prompt, body.Message);
    }

    if (gatewayResult.Ok)
    {
        foreach (var call in pendingToolCalls)
        {
            var result = toolRegistry.RunTool(call.Name, call.Arguments);
            if (!result.Success)
            {
                // Не підміняємо відмову інструмента повідомленням про збій gateway.
                answer = result.Content;
                break;
            }

            answer = $"{answer} ({result.Content})";
        }
    }

    // Tool-відповіді поки не кешуємо: статус замовлення може змінитися.
    if (gatewayResult.Ok && usageAvailable && toolCall is null
        && (finishReason is null or "stop") && !string.IsNullOrWhiteSpace(answer))
        await cache.SetAsync(cacheKey, new CachedResponse(answer, finishReason));

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
