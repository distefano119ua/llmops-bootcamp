using System.Text.Json;

namespace LlmOps.Tools;

public sealed record ToolResult(bool Success, string Content);

public sealed class ToolRegistry
{
    // У core виконуємо лише read-only дії; create_ticket поки повертає відмову.
    private readonly IReadOnlyDictionary<string, Func<JsonElement, ToolResult>> tools =
        new Dictionary<string, Func<JsonElement, ToolResult>>(StringComparer.Ordinal)
        {
            ["lookup_order"] = LookupOrder,
            ["create_ticket"] = _ => new ToolResult(false,
                "Створення тікета потребує підтвердження оператора.")
        };

    public ToolResult RunTool(string name, string arguments)
    {
        if (!tools.TryGetValue(name, out var handler))
            return new ToolResult(false, "Запитаний інструмент недоступний.");

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new ToolResult(false, "Не вдалося виконати інструмент: некоректні аргументи.");

            return handler(document.RootElement);
        }
        catch (JsonException)
        {
            return new ToolResult(false, "Не вдалося виконати інструмент: некоректні аргументи.");
        }
    }

    // Навчальний read-only інструмент: mock передає {}, зовнішнього сервісу замовлень немає.
    private static ToolResult LookupOrder(JsonElement arguments) =>
        new(true, "статус: оплачено, доставку призначено");
}
