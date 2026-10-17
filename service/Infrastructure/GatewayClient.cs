using System.Text;
using System.Text.Json;

namespace LlmOps.Infrastructure;

public sealed record GatewayToolCall(string Name, string Arguments);

public sealed record GatewayResult(bool Ok, int Status, string Answer,
    IReadOnlyList<GatewayToolCall> ToolCalls, string? FinishReason,
    int PromptTokens, int CompletionTokens, bool UsageAvailable, string? Error)
{
    public bool CanFallback => !Ok && (Status == 429 || Status >= 500
        || Error is "network_error" or "timeout" or "invalid_response");

    public static GatewayResult Failed(int status, string error) =>
        new(false, status, "", Array.Empty<GatewayToolCall>(), null, 0, 0, false, error);
}

public static class GatewayClient
{
    public static async Task<GatewayResult> CallGateway(HttpClient http, string gateway,
        string model, string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
    {
        var status = 0; // 0 означає, що HTTP-відповідь не отримано.
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userMessage }
                }
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"{gateway}/v1/chat/completions", content,
                cancellationToken);
            status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return GatewayResult.Failed(status, "http_error");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var choice = doc.RootElement.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            var answer = message.TryGetProperty("content", out var text)
                ? text.GetString() ?? "" : "";
            var finishReason = choice.TryGetProperty("finish_reason", out var reason)
                ? reason.GetString() : null;
            var calls = new List<GatewayToolCall>();
            if (message.TryGetProperty("tool_calls", out var tools)
                && tools.ValueKind != JsonValueKind.Null)
            {
                foreach (var call in tools.EnumerateArray())
                {
                    var function = call.GetProperty("function");
                    var name = function.GetProperty("name").GetString();
                    if (string.IsNullOrWhiteSpace(name))
                        return GatewayResult.Failed(status, "invalid_response");
                    var arguments = function.TryGetProperty("arguments", out var args)
                        ? args.GetString() ?? "{}" : "{}";
                    calls.Add(new GatewayToolCall(name, arguments));
                }
            }
            if (string.IsNullOrWhiteSpace(answer) && calls.Count == 0)
                return GatewayResult.Failed(status, "invalid_response");

            int promptTokens = 0, completionTokens = 0;
            var usageAvailable = doc.RootElement.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number
                && pt.TryGetInt32(out promptTokens) && promptTokens >= 0
                && usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number
                && ct.TryGetInt32(out completionTokens) && completionTokens >= 0;
            return new GatewayResult(true, status, answer, calls, finishReason,
                usageAvailable ? promptTokens : 0, usageAvailable ? completionTokens : 0,
                usageAvailable, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GatewayResult.Failed(status, "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return GatewayResult.Failed(status, "network_error");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or KeyNotFoundException or IndexOutOfRangeException or FormatException)
        {
            return GatewayResult.Failed(status, "invalid_response");
        }
    }
}
