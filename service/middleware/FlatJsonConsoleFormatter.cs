using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace LlmOps.Middleware;

public sealed class FlatJsonConsoleFormatter(
    IOptionsMonitor<InfrastructureLoggingOptions> options,
    IHttpContextAccessor contextAccessor,
    IHostEnvironment environment) : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "flat-json";

    public override void Write<TState>(in LogEntry<TState> entry,
        IExternalScopeProvider? scopeProvider, TextWriter writer)
    {
        var message = entry.Formatter(entry.State, entry.Exception);
        if (message is null && entry.Exception is null) return;

        var settings = options.CurrentValue;
        // Normalize known framework events by source and ID, never by rendered text.
        if (settings.FrameworkMessages.TryGetValue($"{entry.Category}:{entry.EventId.Id}", out var shortMessage))
            message = shortMessage;
        var context = contextAccessor.HttpContext;
        var fields = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString(settings.TimestampFormat, CultureInfo.InvariantCulture),
            ["level"] = entry.LogLevel == LogLevel.Information ? "info" : entry.LogLevel.ToString().ToLowerInvariant(),
            ["service"] = settings.ServiceName,
            ["environment"] = environment.EnvironmentName,
            ["event"] = entry.EventId.Name ?? "system.log",
            ["event_id"] = entry.EventId.Id,
            ["message"] = message
        };
        if (settings.IncludeCategory) fields["category"] = entry.Category;
        if (context is not null)
        {
            fields["request_id"] = context.TraceIdentifier;
            fields["method"] = context.Request.Method;
            fields["path"] = context.Request.Path.Value;
        }

        // Only explicitly permitted scalar properties are emitted; no bodies, SQL or parameters.
        if (entry.State is IEnumerable<KeyValuePair<string, object?>> state)
        {
            foreach (var (key, value) in state)
            {
                var field = key switch
                {
                    "ConnectionId" => "connection_id",
                    "TraceIdentifier" or "RequestId" => "request_id",
                    _ => key
                };
                if (field == "status_code" || settings.StateFields.Contains(field, StringComparer.Ordinal))
                {
                    // Kestrel logs after request middleware has unwound; use its structured IDs
                    // when HttpContext is no longer available. Never overwrite an existing ID.
                    if (!fields.ContainsKey(field) && IsScalar(value)) fields[field] = value;
                }
            }
        }
        if (entry.Exception is not null)
        {
            fields["exception_type"] = entry.Exception.GetType().Name;
            fields["exception_message"] = entry.Exception.Message;
            if (settings.IncludeExceptionStackTrace)
                fields["exception_stack_trace"] = entry.Exception.StackTrace;
        }

        // WriteLine emits one complete JSON record. Embedded newlines are escaped by the serializer.
        writer.WriteLine(JsonSerializer.Serialize(fields));
    }

    private static bool IsScalar(object? value) => value is null or string or bool or char
        or byte or sbyte or short or ushort or int or uint or long or ulong
        or float or double or decimal or Guid or DateTime or DateTimeOffset;
}
