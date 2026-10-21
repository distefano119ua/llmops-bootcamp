using System.Text.RegularExpressions;

namespace LlmOps.Guardrails;

public static class PiiMasker
{
    public static string MaskPii(string message) =>
        Regex.Replace(message, @"[\w.\-]+@[\w.\-]+", "[email]");
}
