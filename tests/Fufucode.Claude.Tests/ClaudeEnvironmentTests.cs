using Fufucode.Plugins.Claude;

namespace Fufucode.Claude.Tests;

/// <summary>
/// Claude Code 2.1.31x 的 CLI 不再应用 settings.json 的 env；网关凭据必须由宿主转发。
/// 这里验证：只读允许的键、进程环境优先、不转发无关键。
/// </summary>
internal static class ClaudeEnvironmentTests
{
    private static readonly string[] Forwarded =
        ["ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS"];

    public static void Run(Checks checks)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-claude-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var saved = Forwarded.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var savedConfigDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            foreach (var key in Forwarded) Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", root);
            File.WriteAllText(Path.Combine(root, "settings.json"),
                """
                {
                  "env": {
                    "ANTHROPIC_AUTH_TOKEN": "gateway-token",
                    "ANTHROPIC_BASE_URL": "http://127.0.0.1:3425",
                    "UNRELATED_SETTING": "should-not-be-forwarded"
                  }
                }
                """);

            var loaded = ClaudeSettingsEnvironment.Load();
            checks.Check(loaded is not null, "settings.json 的网关凭据被转发");
            checks.Equal("gateway-token", loaded!["ANTHROPIC_AUTH_TOKEN"], "转发 AUTH_TOKEN");
            checks.Equal("http://127.0.0.1:3425", loaded["ANTHROPIC_BASE_URL"], "转发 BASE_URL");
            checks.Check(!loaded.ContainsKey("UNRELATED_SETTING"), "无关的 env 键不转发");

            // 进程环境已有值时，文件不得覆盖它。
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "process-token");
            var withProcess = ClaudeSettingsEnvironment.Load();
            checks.Check(withProcess is null || !withProcess.ContainsKey("ANTHROPIC_AUTH_TOKEN"),
                "进程环境优先，settings.json 不覆盖");

            // 缺少 settings.json 时返回 null，而不是伪造值。
            File.Delete(Path.Combine(root, "settings.json"));
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", null);
            checks.Check(ClaudeSettingsEnvironment.Load() is null, "缺少配置文件时不伪造凭据");
        }
        finally
        {
            foreach (var key in Forwarded) Environment.SetEnvironmentVariable(key, saved[key]);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", savedConfigDir);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
