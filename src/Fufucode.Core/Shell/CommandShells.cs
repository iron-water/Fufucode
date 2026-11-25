using System.Diagnostics;
using System.Text;

namespace Fufucode.Core.Shell;

/// <summary>命令解释器的语法家族：决定参数构造、退出码收集与给模型的语法提示。</summary>
public enum CommandShellFamily
{
    PowerShell,
    Cmd,
    Posix,
}

/// <summary>一种受支持的命令解释器。</summary>
public sealed record CommandShellKind(string Id, string DisplayName, CommandShellFamily Family);

/// <summary>解析到真实可执行文件的解释器。<see cref="IsFallback"/> 表示默认解释器不可用而顺延到了后备项。</summary>
public sealed record ResolvedCommandShell(CommandShellKind Kind, string Executable, bool IsFallback);

/// <summary>启动一次非交互命令时的选项。</summary>
public sealed record CommandShellLaunchOptions(bool LoadProfile = false);

/// <summary>
/// 非交互命令解释器（FUMO run_command 与终端插件共用）的唯一目录：
/// 支持哪些解释器、默认是谁、到哪里找可执行文件、如何把一段命令交给它执行。
/// </summary>
public static class CommandShells
{
    public const string Pwsh = "pwsh";
    public const string WindowsPowerShell = "powershell";
    public const string Cmd = "cmd";
    public const string GitBash = "git-bash";
    public const string Sh = "sh";
    public const string Bash = "bash";

    private static readonly CommandShellKind[] WindowsKinds =
    [
        new(Pwsh, "PowerShell 7", CommandShellFamily.PowerShell),
        new(WindowsPowerShell, "Windows PowerShell 5.1", CommandShellFamily.PowerShell),
        new(Cmd, "命令提示符 (cmd.exe)", CommandShellFamily.Cmd),
        new(GitBash, "Git Bash", CommandShellFamily.Posix),
    ];

    private static readonly CommandShellKind[] UnixKinds =
    [
        new(Sh, "sh", CommandShellFamily.Posix),
        new(Bash, "bash", CommandShellFamily.Posix),
        new(Pwsh, "PowerShell 7", CommandShellFamily.PowerShell),
    ];

    /// <summary>当前平台支持的解释器，按展示顺序排列。</summary>
    public static IReadOnlyList<CommandShellKind> All => OperatingSystem.IsWindows() ? WindowsKinds : UnixKinds;

    /// <summary>未显式设置时使用的解释器：Windows 为 PowerShell 7，其余平台为 sh。</summary>
    public static string DefaultId => OperatingSystem.IsWindows() ? Pwsh : Sh;

    /// <summary>默认解释器不存在时依次尝试的顺序（只用于“未显式设置”）。</summary>
    private static IEnumerable<string> DefaultChain =>
        OperatingSystem.IsWindows() ? [Pwsh, WindowsPowerShell, Cmd] : [Sh, Bash];

    public static CommandShellKind? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null :
        All.FirstOrDefault(kind => string.Equals(kind.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 解析要使用的解释器。<paramref name="preferredId"/> 为空时走默认链（pwsh → Windows PowerShell → cmd）；
    /// 显式指定时只用该解释器，不存在就返回 null 与原因，不悄悄换成别的。
    /// </summary>
    public static ResolvedCommandShell? Resolve(string? preferredId, out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            if (Find(preferredId) is not { } explicitKind)
            {
                error = $"不支持的 shell '{preferredId}'。可用：{string.Join("、", All.Select(kind => kind.Id))}。";
                return null;
            }
            if (FindExecutable(explicitKind.Id) is { } explicitPath)
                return new ResolvedCommandShell(explicitKind, explicitPath, IsFallback: false);
            error = $"未找到 {explicitKind.DisplayName}（{explicitKind.Id}）。";
            return null;
        }

        foreach (var id in DefaultChain)
        {
            if (Find(id) is { } kind && FindExecutable(id) is { } path)
                return new ResolvedCommandShell(kind, path, IsFallback: id != DefaultId);
        }
        error = "未找到任何可用的命令解释器。";
        return null;
    }

    /// <summary>当前机器上真实存在的解释器（设置页与 run_command 参数说明使用）。</summary>
    public static IReadOnlyList<ResolvedCommandShell> Available() =>
        All.Select(kind => FindExecutable(kind.Id) is { } path ? new ResolvedCommandShell(kind, path, false) : null)
            .OfType<ResolvedCommandShell>()
            .ToArray();

    /// <summary>该解释器可执行文件的绝对路径；不存在时返回 null。</summary>
    public static string? FindExecutable(string id)
    {
        if (Find(id) is not { } kind) return null;
        return OperatingSystem.IsWindows() ? FindWindowsExecutable(kind.Id) : FindUnixExecutable(kind.Id);
    }

    private static string? FindWindowsExecutable(string id)
    {
        var system = Environment.SystemDirectory;
        switch (id)
        {
            case Cmd:
                return FirstExisting(Path.Combine(system, "cmd.exe"));
            case WindowsPowerShell:
                return FirstExisting(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"))
                       ?? SearchPath("powershell.exe");
            case Pwsh:
                return FirstExisting(
                           Expand(@"%ProgramFiles%\PowerShell\7\pwsh.exe"),
                           Expand(@"%LOCALAPPDATA%\Microsoft\PowerShell\7\pwsh.exe"),
                           Expand(@"%LOCALAPPDATA%\Programs\PowerShell\7\pwsh.exe"),
                           Expand(@"%ProgramFiles%\PowerShell\7-preview\pwsh.exe"))
                       ?? SearchPath("pwsh.exe");
            case GitBash:
                return FirstExisting(
                    Expand(@"%ProgramFiles%\Git\bin\bash.exe"),
                    Expand(@"%ProgramFiles(x86)%\Git\bin\bash.exe"),
                    Expand(@"%LOCALAPPDATA%\Programs\Git\bin\bash.exe"),
                    Expand(@"%USERPROFILE%\scoop\apps\git\current\bin\bash.exe"));
            default:
                return null;
        }
    }

    private static string? FindUnixExecutable(string id) => id switch
    {
        Sh => FirstExisting("/bin/sh") ?? SearchPath("sh"),
        Bash => FirstExisting("/bin/bash", "/usr/bin/bash") ?? SearchPath("bash"),
        Pwsh => SearchPath("pwsh") ?? FirstExisting("/usr/local/bin/pwsh", "/opt/microsoft/powershell/7/pwsh"),
        _ => null,
    };

    private static string Expand(string value) => Environment.ExpandEnvironmentVariables(value);

    private static string? FirstExisting(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            // 未展开的环境变量（变量不存在）不是有效路径。
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains('%')) continue;
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? SearchPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(directory.Trim('"')), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>超过此长度的 PowerShell 脚本改走临时 .ps1 文件，避免 -EncodedCommand 撞上 32K 命令行上限。</summary>
    internal const int MaxEncodedScriptChars = 10_000;

    /// <summary>
    /// 构造一次非交互执行的进程参数。返回值中的 <c>TempScript</c> 不为 null 时，调用方在进程结束后负责删除。
    /// 标准输出/错误都按 UTF-8 解码。
    /// </summary>
    public static (ProcessStartInfo StartInfo, string? TempScript) CreateStartInfo(
        ResolvedCommandShell shell, string command, string workingDirectory, CommandShellLaunchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(command);
        options ??= new CommandShellLaunchOptions();
        var psi = new ProcessStartInfo
        {
            FileName = shell.Executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        string? tempScript = null;
        switch (shell.Kind.Family)
        {
            case CommandShellFamily.PowerShell:
                tempScript = AddPowerShellArguments(psi, command, options);
                break;
            case CommandShellFamily.Cmd:
                // /d 跳过 AutoRun（等价于不加载配置）；chcp 65001 让 cmd 与多数控制台程序输出 UTF-8。
                psi.Arguments = $"{(options.LoadProfile ? "" : "/d ")}/s /c \"chcp 65001>nul & {command}\"";
                break;
            default:
                // Git Bash 的 MSYS 程序默认按 UTF-8 输出；sh 不认识 --noprofile，只有 bash 系才加。
                if (shell.Kind.Id != Sh)
                {
                    if (options.LoadProfile) psi.ArgumentList.Add("--login");
                    else
                    {
                        psi.ArgumentList.Add("--noprofile");
                        psi.ArgumentList.Add("--norc");
                    }
                }
                // ArgumentList 避免 .NET 按 Windows 规则重新解析并剥离命令中的内层引号。
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
                if (shell.Kind.Id == GitBash) psi.Environment["CHERE_INVOKING"] = "1";
                break;
        }
        return (psi, tempScript);
    }

    private static string? AddPowerShellArguments(ProcessStartInfo psi, string command,
        CommandShellLaunchOptions options)
    {
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NonInteractive");
        if (!options.LoadProfile) psi.ArgumentList.Add("-NoProfile");
        if (OperatingSystem.IsWindows())
        {
            // 进程级策略：只影响这一次执行，不改用户或机器的执行策略；否则 5.1 默认策略会拒绝运行仓库里的 .ps1。
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
        }
        psi.ArgumentList.Add("-OutputFormat");
        psi.ArgumentList.Add("Text");

        var script = BuildPowerShellScript(command);
        if (script.Length <= MaxEncodedScriptChars)
        {
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            return null;
        }

        var directory = Path.Combine(Path.GetTempPath(), "fufucode-shell");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".ps1");
        // 带 BOM：Windows PowerShell 5.1 只有看到 BOM 才按 UTF-8 读取脚本。
        File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(path);
        return path;
    }

    /// <summary>
    /// 包装用户命令：前导行统一 UTF-8 输出并关闭进度条；结尾按最后一条语句的成败给出退出码——
    /// 成功为 0，外部程序失败时取其退出码，cmdlet 失败为 1。显式 <c>exit N</c> 直接生效。
    /// 只用 Windows PowerShell 5.1 也支持的语法。
    /// </summary>
    internal static string BuildPowerShellScript(string command)
    {
        const string prelude =
            "$ProgressPreference='SilentlyContinue';" +
            "try{$__fu=[System.Text.UTF8Encoding]::new($false);[Console]::OutputEncoding=$__fu;[Console]::InputEncoding=$__fu;$OutputEncoding=$__fu}catch{};" +
            "Remove-Variable __fu -ErrorAction SilentlyContinue;$global:LASTEXITCODE=0";
        const string epilogue =
            "if($?){exit 0}elseif(($LASTEXITCODE -is [int]) -and ($LASTEXITCODE -ne 0)){exit $LASTEXITCODE}else{exit 1}";
        return prelude + "\n" + command + "\n" + epilogue;
    }
}
