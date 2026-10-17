namespace LlmOps.Routing;

public sealed class ModelRouter
{
    private long fallbackEvents;

    public long FallbackEvents => Interlocked.Read(ref fallbackEvents);
    public long RecordFallback() => Interlocked.Increment(ref fallbackEvents);

    // [W2] проста маршрутизація: ескалацію — на сильну модель.
    public string Route(string message, string defaultModel)
    {
        if (defaultModel != "mock") return defaultModel;
        var u = message.ToLowerInvariant();
        bool isEscalation = u.Contains("поверн")
            || u.Contains("терміново")
            || u.Contains("скарг")
            || u.Contains("refund");
        return isEscalation ? "mock-strong" : "mock-mini";
    }

    // Ланцюг будується після бюджетної політики за фактично вибраною моделлю.
    public string[] GetFallbackChain(string model) => model switch
    {
        "mock-strong" => new[] { model, "mock-mini" },
        "mock-mini" => new[] { model, "mock-azure-gpt-5" },
        "mock-azure-gpt-5" => new[] { model, "mock-mini" },
        "mock" => new[] { model, "mock-azure-gpt-5" },
        _ => new[] { model }
    };
}
