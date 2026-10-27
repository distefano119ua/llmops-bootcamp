using System.Text.Json;
using LlmOps.Observability;
using LlmOps.Middleware;

namespace LlmOps.Tools;

public sealed record ToolResult(bool Success, string Content, Guid? ApprovalId = null);

public sealed class ToolRegistry(ApprovalStore approvals, ErrorCounters errorCounters, ILogger<ToolRegistry> logger)
{
    // Read-only виконуємо одразу, create_ticket лише ставимо в чергу підтверджень.
    private readonly IReadOnlyDictionary<string, Func<JsonElement, Guid, ToolResult>> tools =
        new Dictionary<string, Func<JsonElement, Guid, ToolResult>>(StringComparer.Ordinal)
        {
            ["lookup_order"] = (arguments, _) => LookupOrder(arguments),
            ["create_ticket"] = (arguments, requestId) =>
            {
                var approval = approvals.EnqueueTicket(requestId, arguments.GetRawText());
                return new ToolResult(true, "Створення тікета очікує підтвердження оператора.", approval.Id);
            }
        };

    public ToolResult RunTool(string name, string arguments, Guid requestId)
    {
        if (!tools.TryGetValue(name, out var handler))
            return new ToolResult(false, "Запитаний інструмент недоступний.");

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new ToolResult(false, "Не вдалося виконати інструмент: некоректні аргументи.");

            return handler(document.RootElement, requestId);
        }
        catch (JsonException)
        {
            return new ToolResult(false, "Не вдалося виконати інструмент: некоректні аргументи.");
        }
        catch (TimeoutException)
        {
            errorCounters.RecordToolTimeout();
            InfrastructureLog.Write(logger, LogLevel.Warning,
                new EventId(1800, "tool.timeout"), "Tool execution timed out", null,
                ("tool_name", name), ("error_type", "tool_timeout"));
            return new ToolResult(false, "Інструмент не відповів вчасно. Спробуйте пізніше.");
        }
    }

    // Навчальний read-only інструмент: mock передає {}, зовнішнього сервісу замовлень немає.
    private static ToolResult LookupOrder(JsonElement arguments) =>
        new(true, "статус: оплачено, доставку призначено");
}
