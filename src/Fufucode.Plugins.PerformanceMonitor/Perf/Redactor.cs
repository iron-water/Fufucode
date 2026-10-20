using System.Text;
using System.Text.RegularExpressions;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 把命令、URL、错误消息中的凭据脱敏后再落盘。规则是保守的：宁可多遮一段，也不把密钥写进日志。
/// 导出时再跑一遍，确保历史文件也被覆盖。
/// </summary>
public static partial class Redactor
{
    public const string Mask = "***";

    private const string KeyNames =
        @"api[_-]?key|access[_-]?key|secret[_-]?key|private[_-]?key|password|passwd|pwd|token|secret|auth[_-]?token|client[_-]?secret";

    /// <summary>任意文本脱敏：API Key、Bearer、常见密钥参数、URL 中的用户名/密码、凭据目录。</summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var text = value.Length > 4096 ? value[..4096] + "…" : value;
        text = SkKeyRegex().Replace(text, "sk-" + Mask);
        text = BearerRegex().Replace(text, "$1" + Mask);
        // URL 用户名/密码先于 key=value 处理，避免把 URL 片段当成密钥参数。
        text = UrlUserInfoRegex().Replace(text, "${scheme}" + Mask + "@");
        // 等号/冒号形式：--token=abc、token: abc、PASSWORD="abc"。
        text = KeyValueRegex().Replace(text, "$1" + Mask);
        // 空格分隔形式：--token abc123、--password hunter2（只接受长选项，避免误伤普通命令参数）。
        text = SpaceValueRegex().Replace(text, "$1" + Mask);
        text = SensitivePathRegex().Replace(text, "$1" + Mask + "$3");
        return text;
    }

    /// <summary>多行文本（异常栈）逐行脱敏，并限制行数。</summary>
    public static string Multiline(string? value, int maxLines = 20)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var lines = value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Length && i < maxLines; i++)
        {
            if (i > 0) builder.Append('\n');
            builder.Append(Text(lines[i]));
        }
        if (lines.Length > maxLines) builder.Append("\n…");
        return builder.ToString();
    }

    // sk-... / sk-ant-... / sk-proj-... 等 API Key 形态。
    [GeneratedRegex(@"\bsk-[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex SkKeyRegex();

    // Authorization: Bearer <token> 或裸 Bearer <token>。
    [GeneratedRegex(@"(?i)\b(bearer\s+)[A-Za-z0-9._~+\/\-]{8,}=*", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    // 等号/冒号形式：--api-key=xxx / password: xxx / TOKEN="abc"。
    [GeneratedRegex(@"((?i:" + KeyNames + @")""*\s*[=:]\s*)(""[^""]*""|'[^']*'|[^\s&;""']{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueRegex();

    // 空格分隔的长选项形式：--token abc123。
    [GeneratedRegex(@"((?i:--?(?:" + KeyNames + @"))\s+)(\S{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceValueRegex();

    // https://user:password@host
    [GeneratedRegex(@"(?i)\b(?<scheme>https?://)[^/\s@:]+:[^/\s@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfoRegex();

    // 路径中的凭据目录：/tokens/...、/credentials/...。
    [GeneratedRegex(@"(?i)([/\\](?:tokens?|secrets?|credentials?)[/\\])([^/\\\s]+)([/\\]?)", RegexOptions.CultureInvariant)]
    private static partial Regex SensitivePathRegex();
}
