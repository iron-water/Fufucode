using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Shell;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// run_command 多解释器：默认 PowerShell 7、显式选择严格生效、退出码与 UTF-8 输出在各解释器中一致。
/// 本机没有的解释器显式 SKIP，不以“没装”冒充通过。
/// </summary>
internal static class CommandShellTests
{
    public static async Task RunAsync(Checks checks)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "fufucode-shell-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            CatalogAndDefaults(checks);
            SettingsRoundTrip(checks, workspace);
            await PerShellBehaviour(checks, workspace);
            await DispatcherSettings(checks, workspace);
            PromptGuidance(checks);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    private static void CatalogAndDefaults(Checks checks)
    {
        if (OperatingSystem.IsWindows())
        {
            checks.Equal(CommandShells.Pwsh, CommandShells.DefaultId, "Windows 默认解释器是 PowerShell 7");
            checks.Check(CommandShells.All.Select(kind => kind.Id).SequenceEqual(
                    [CommandShells.Pwsh, CommandShells.WindowsPowerShell, CommandShells.Cmd, CommandShells.GitBash]),
                "Windows 支持 pwsh / Windows PowerShell / cmd / Git Bash");
            var resolved = CommandShells.Resolve(null, out _);
            var expected = CommandShells.FindExecutable(CommandShells.Pwsh) is not null ? CommandShells.Pwsh
                : CommandShells.FindExecutable(CommandShells.WindowsPowerShell) is not null ? CommandShells.WindowsPowerShell
                : CommandShells.Cmd;
            checks.Equal(expected, resolved?.Kind.Id, "未设置时按 pwsh → Windows PowerShell → cmd 取第一个存在的");
            checks.Equal(expected != CommandShells.Pwsh, resolved?.IsFallback, "只有顺延到后备解释器时才标记 IsFallback");
        }

        checks.Check(CommandShells.Resolve("no-such-shell", out var unknown) is null && unknown!.Contains("不支持"),
            "未知 shell 如实报错，不回退");
        var missing = CommandShells.All.FirstOrDefault(kind => CommandShells.FindExecutable(kind.Id) is null);
        if (missing is null) Console.WriteLine("SKIP 本机所有解释器都已安装，无法验证“显式选择缺失解释器”");
        else checks.Check(CommandShells.Resolve(missing.Id, out var missingError) is null && missingError!.Contains("未找到"),
            $"显式选择未安装的 {missing.Id} 时报告未找到，不悄悄换成别的");
    }

    private static void SettingsRoundTrip(Checks checks, string workspace)
    {
        var layout = new AppDataLayout(Path.Combine(workspace, "data"));
        var store = new AppSettingsStore(layout);
        checks.Check(store.Current.Fumo is null, "旧设置没有 fumo 字段时按默认处理");
        store.Update(current => current with { Fumo = new FumoSettings(CommandShells.Cmd, true, 42) });
        var reloaded = new AppSettingsStore(layout).Current.Fumo;
        checks.Equal(new FumoSettings(CommandShells.Cmd, true, 42), reloaded, "FUMO 高级设置写盘后可原样读回");
        checks.Throws<ArgumentException>(() => store.Update(current => current with { Fumo = new FumoSettings(CommandTimeoutSeconds: 0) }),
            "命令超时必须在 1–3600 秒内");
        checks.Throws<ArgumentException>(() => store.Update(current => current with { Fumo = new FumoSettings(Shell: "cmd & calc") }),
            "shell id 拒绝脏值");
    }

    private static async Task PerShellBehaviour(Checks checks, string workspace)
    {
        foreach (var kind in CommandShells.All)
        {
            if (CommandShells.FindExecutable(kind.Id) is null)
            {
                Console.WriteLine($"SKIP {kind.Id}：本机未安装");
                continue;
            }
            var dispatcher = new ProductionToolDispatcher(workspace) { FumoSettingsProvider = () => new FumoSettings(Shell: kind.Id) };
            Task<PureToolResult> Run(string command, int timeout = 60) => dispatcher.DispatchAsync(new PureToolCall(
                "s-" + kind.Id, "run_command", JsonSerializer.Serialize(new { command, timeout_seconds = timeout })));

            var (echo, failNative, failExplicit, quoted) = kind.Family switch
            {
                CommandShellFamily.PowerShell => ("Write-Output '中文输出✓'",
                    OperatingSystem.IsWindows() ? "cmd.exe /c exit 7" : "sh -c 'exit 7'",
                    "exit 5", "Write-Output \"a  b\"; Write-Output 'c\"d'"),
                CommandShellFamily.Cmd => ("echo 中文输出✓", "cmd.exe /c exit 7", "exit /b 5", "echo \"a  b\" c\"d"),
                _ => ("echo '中文输出✓'", "sh -c 'exit 7'", "exit 5", "echo \"a  b\" 'c\"d'"),
            };

            var ok = await Run(echo);
            checks.Check(ok.Success && ok.Content.StartsWith("ExitCode: 0", StringComparison.Ordinal) && ok.Content.Contains("中文输出✓"),
                $"{kind.Id}：UTF-8 中文输出不乱码且退出码 0");
            var native = await Run(failNative);
            checks.Check(!native.Success && native.Content.StartsWith("ExitCode: 7", StringComparison.Ordinal),
                $"{kind.Id}：外部程序失败时透传其退出码");
            var explicitExit = await Run(failExplicit);
            checks.Check(!explicitExit.Success && explicitExit.Content.StartsWith("ExitCode: 5", StringComparison.Ordinal),
                $"{kind.Id}：显式 exit 生效");
            var quotes = await Run(quoted);
            checks.Check(quotes.Success && quotes.Content.Contains("a  b") && quotes.Content.Contains("c\"d"),
                $"{kind.Id}：命令中的引号与连续空格原样传递");

            if (kind.Family == CommandShellFamily.PowerShell)
            {
                var cmdlet = await Run("Get-Item -LiteralPath './definitely-missing-file'");
                checks.Check(!cmdlet.Success && cmdlet.Content.StartsWith("ExitCode: 1", StringComparison.Ordinal) &&
                             cmdlet.Content.Contains("definitely-missing-file"),
                    $"{kind.Id}：cmdlet 出错时退出码 1 且错误进入输出");
                var recovered = await Run("cmd.exe /c exit 3; Write-Output after");
                checks.Check(recovered.Success && recovered.Content.Contains("after"),
                    $"{kind.Id}：退出码取最后一条语句");
                var profile = await Run("Write-Output ([Environment]::CommandLine)");
                checks.Check(profile.Content.Contains("-NoProfile", StringComparison.OrdinalIgnoreCase),
                    $"{kind.Id}：默认不加载用户配置文件");
                var longScript = "$x = 0\n" + string.Concat(Enumerable.Repeat("$x++\n", 3000)) + "Write-Output \"count=$x 长脚本\"";
                checks.Check(longScript.Length > 10_000, "长脚本超过 EncodedCommand 阈值");
                var tempDir = Path.Combine(Path.GetTempPath(), "fufucode-shell");
                var before = Directory.Exists(tempDir) ? Directory.GetFiles(tempDir).Length : 0;
                var longRun = await Run(longScript);
                checks.Check(longRun.Success && longRun.Content.Contains("count=3000 长脚本"),
                    $"{kind.Id}：超长脚本经临时 .ps1 执行，中文不乱码");
                var after = Directory.Exists(tempDir) ? Directory.GetFiles(tempDir).Length : 0;
                checks.Check(after <= before, $"{kind.Id}：临时脚本执行后被删除");
                if (kind.Id == CommandShells.Pwsh)
                {
                    var chain = await Run("Write-Output one && Write-Output two");
                    checks.Check(chain.Success && chain.Content.Contains("one") && chain.Content.Contains("two"),
                        "pwsh：支持 && 串联");
                }
            }
        }
    }

    private static async Task DispatcherSettings(Checks checks, string workspace)
    {
        var dispatcher = new ProductionToolDispatcher(workspace) { FumoSettingsProvider = () => new FumoSettings(CommandTimeoutSeconds: 1) };
        var sleep = OperatingSystem.IsWindows() && CommandShells.FindExecutable(CommandShells.Cmd) is not null
            ? JsonSerializer.Serialize(new { command = "ping -n 6 127.0.0.1 >nul", shell = CommandShells.Cmd })
            : JsonSerializer.Serialize(new { command = "sleep 5", shell = CommandShells.Sh });
        var timedOut = await dispatcher.DispatchAsync(new PureToolCall("t1", "run_command", sleep));
        checks.Check(!timedOut.Success && timedOut.Content.Contains("超过 1 秒"), "未指定 timeout_seconds 时使用设置中的默认超时");

        var bad = await dispatcher.DispatchAsync(new PureToolCall("t2", "run_command",
            JsonSerializer.Serialize(new { command = "echo x", shell = "no-such-shell" })));
        checks.Check(!bad.Success && bad.Content.Contains("不支持的 shell"), "run_command 的 shell 参数不认识时如实报错");

        var schema = ProductionToolDispatcher.GetBuiltinToolDefinitions().Single(d => d.Name == "run_command").ParametersJsonSchema;
        checks.Check(schema.Contains("\"shell\"", StringComparison.Ordinal), "run_command 声明可选 shell 参数");
    }

    private static void PromptGuidance(Checks checks)
    {
        if (!OperatingSystem.IsWindows()) return;
        var pwsh = CommandShells.Resolve(CommandShells.Pwsh, out _);
        if (pwsh is null) { Console.WriteLine("SKIP pwsh 提示：本机未安装"); }
        else
        {
            var text = CommandShellPrompt.BuildGuidance(pwsh);
            checks.Check(text.Contains("PowerShell 7") && text.Contains("$env:NAME") && !text.Contains("不要使用 ';'"),
                "pwsh 提示给出 PowerShell 语法，而非 cmd 规则");
        }
        var cmd = CommandShells.Resolve(CommandShells.Cmd, out _)!;
        checks.Check(CommandShellPrompt.BuildGuidance(cmd).Contains("'&&' 或 '&'"), "cmd 提示保留 cmd 串联规则");
        checks.Check(CommandShellPrompt.BuildGuidance(null).Contains("没有可用的命令解释器"), "无解释器时提示如实说明");
    }
}
