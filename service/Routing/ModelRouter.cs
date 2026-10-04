namespace LlmOps.Routing;

public sealed class ModelRouter
{
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

    // [W4] цей рядок стане кодом.
    // fallback chain: mock-strong -> mock-mini -> контрольована помилка
}
