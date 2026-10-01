namespace LlmOps.Middleware;

internal static class InfrastructureLog
{
    // Keep the human message separate from structured attributes.
    public static void Write(ILogger logger, LogLevel level, EventId eventId,
        string message, Exception? exception = null, params (string Name, object? Value)[] attributes)
    {
        var state = attributes.ToDictionary(attribute => attribute.Name, attribute => attribute.Value);
        state["message"] = message;
        logger.Log(level, eventId, state, exception, (fields, _) => (string)fields["message"]!);
    }
}
