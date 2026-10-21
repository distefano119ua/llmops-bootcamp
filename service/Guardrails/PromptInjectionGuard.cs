using System.Text.RegularExpressions;

namespace LlmOps.Guardrails;

public static class PromptInjectionGuard
{
    // Навчальні евристики для прямих атак; це не повний захист від prompt injection.
    private static readonly (string Category, string Pattern)[] rules =
    [
        ("rule_override",
            @"\b(забудь|ігноруй|игнорируй|forget|ignore)\b[\s\S]{0,100}\b(інструкц\w*|инструкц\w*|правил\w*|instructions?|rules?)\b"
            + @"|\b(тепер|теперь|now)\s+(ти|ты|you(?:\s+are)?)\s+(інший|другой|another|a\s+different)\s+(бот|bot)\b"),
        ("system_prompt_disclosure",
            @"\b(покажи|розкрий|розкрийте|покажіть|раскрой|show|reveal|print|display)\b[\s\S]{0,100}"
            + @"\b(системн\w*\s+(промпт\w*|інструкц\w*|инструкц\w*)|system\s+(prompt|instructions?))\b"),
        ("social_pressure",
            @"\b(я\s+(розробник|разработчик)|i\s+am\s+(the\s+|a\s+)?developer|i['’]m\s+(the\s+|a\s+)?developer)\b"
            + @"[\s\S]{0,200}\b(дебаг\w*|налагоджен\w*|отлад\w*|debug\w*|потріб\w*|нуж\w*|need)\b")
    ];

    public static string? Detect(string message)
    {
        try
        {
            foreach (var (category, pattern) in rules)
                if (Regex.IsMatch(message, pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    return category;
        }
        catch (RegexMatchTimeoutException)
        {
            // Не пропускаємо неперевірений запит у модель, якщо перевірка перевищила час.
            return "inspection_timeout";
        }

        return null;
    }
}
