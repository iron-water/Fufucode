using System.Text.Json;

namespace Fufucode.Plugins.Claude;

/// <summary>
/// Claude Code 2.1.31x 的 `-p` 不再把 <c>~/.claude/settings.json</c> 的 <c>env</c> 应用到 CLI 自身，
/// 网关（如本机 magpie）依赖的凭据因此读不到。这里只转发网关认证所需的少数键：
/// 进程环境已有值时绝不覆盖，值不写日志。
/// </summary>
public static class ClaudeSettingsEnvironment
{
    private static readonly string[] ForwardedKeys =
    [
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_CUSTOM_HEADERS",
    ];

    public static IReadOnlyDictionary<string, string>? Load()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in ForwardedKeys)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) continue;
            if (TryReadSetting(key) is { Length: > 0 } value) result[key] = value;
        }
        return result.Count == 0 ? null : result;
    }

    private static string? TryReadSetting(string key)
    {
        try
        {
            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            var path = string.IsNullOrWhiteSpace(configDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json")
                : Path.Combine(configDir, "settings.json");
            if (!File.Exists(path)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("env", out var env) &&
                   env.ValueKind == JsonValueKind.Object &&
                   env.TryGetProperty(key, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex)
        {
            // 配置损坏不应阻断会话启动；只记录异常类型，不带值。
            System.Diagnostics.Trace.TraceWarning(
                "Failed to read Claude settings environment ({0}): {1}", key, ex.GetType().Name);
            return null;
        }
    }
}
