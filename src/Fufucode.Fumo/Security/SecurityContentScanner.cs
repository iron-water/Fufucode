using System.Text.RegularExpressions;

namespace Fufucode.Core.Security;

public static class SecurityContentScanner
{
    public static string Redact(string text, SecuritySettings? settings = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Policy controls tool-output detection; callers omitting policy get the safe builtin presets.
        var rules = (settings?.SecretRules ?? new SecuritySettings().SecretRules).Where(r => r.Enabled)
            .DistinctBy(r => r.Pattern);
        try
        {
            foreach (var rule in rules)
                text = Regex.Replace(text, rule.Pattern, "[REDACTED]", RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            return text;
        }
        catch (RegexMatchTimeoutException) { return "[内容已隐藏：敏感信息扫描超时]"; }
        catch (ArgumentException) { return "[内容已隐藏：敏感信息规则无效]"; }
    }

    public static bool ContainsSecret(string text, SecuritySettings settings) =>
        !string.Equals(text, Redact(text, settings), StringComparison.Ordinal);

    public static bool ContainsInjection(string text)
    {
        try
        {
            return Regex.IsMatch(text,
                @"(?is)(ignore|disregard|override)\s+(all\s+)?(previous|prior|system|developer|above)\s+(instructions?|prompts?|rules?)|(?:<\|(?:im_start|system)\|>|\[INST\]|</?system>)|(?:忽略|无视|覆盖).{0,20}(?:之前|系统|开发者).{0,10}(?:指令|提示|规则)|(?:reveal|print|send|exfiltrate).{0,30}(?:system prompt|api.?key|secret token)",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (RegexMatchTimeoutException) { return true; }
    }
}
