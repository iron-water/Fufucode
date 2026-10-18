using System;

namespace Fufucode.Core.Runtimes;

/// <summary>工具链类型。运行时与后端使用同一张表、同一套安装接口。</summary>
public enum ToolchainKind
{
    Runtime,
    Backend,
}

/// <summary>
/// 一个工具链的统一描述。解析顺序对所有人相同：
/// 显式参数 → 系统安装（标准路径 + PATH）→ 用户安装到 Fufucode 的 runtime/&lt;id&gt;。
/// </summary>
public sealed record ToolchainDescriptor(
    string Id,
    string DisplayName,
    ToolchainKind Kind,
    string Description,
    // PATH 搜索名（Windows 会自动追加 .exe/.cmd）。
    IReadOnlyList<string> ExecutableNames,
    // 操作系统标准安装路径。
    IReadOnlyList<string> StandardPaths,
    // 用户安装到 runtime/<id> 后，可执行文件相对该目录的路径。
    string? ManagedExecutable,
    // 用户安装到 runtime/<id> 后，需要加入子进程 PATH 的相对目录。
    IReadOnlyList<string> ManagedPathEntries,
    // 依赖的其它工具链 id（例如 Pi 依赖 node）。
    IReadOnlyList<string> DependsOn,
    // Node + JS 脚本型工具：脚本相对路径与标准安装中的脚本路径。
    string? ManagedScript = null,
    IReadOnlyList<string>? StandardScripts = null,
    // 该工具链没有已验证的配置隔离变量时为 null。
    string? ConfigDirectoryEnvironmentVariable = null);

/// <summary>统一工具链探测结果。</summary>
public sealed record ToolchainStatus(
    string Id,
    ToolchainKind Kind,
    bool Available,
    bool SystemInstalled,
    bool ManagedInstalled,
    string? SystemPath,
    string? ManagedPath,
    string? ScriptPath,
    string? Diagnostic);

/// <summary>
/// 全部可选工具链：Node、Bun、Python、Git Bash 等运行时，以及 Pi/OMP/Claude/Codex/DSH 后端。
/// 所有条目使用同一探测、安装、移除与 PATH 注入逻辑。
/// </summary>
public static class ToolchainCatalog
{
    private const string PiCliRelative = "node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js";

    public static IReadOnlyList<ToolchainDescriptor> All { get; } = Build();

    public static ToolchainDescriptor? Find(string id) =>
        All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string ManagedRoot(AppDataLayout layout, string id) =>
        Path.Combine(layout.RuntimeDirectory, id);

    public static ToolchainStatus Detect(AppDataLayout layout, string id)
    {
        var descriptor = Find(id);
        if (descriptor is null)
            return new ToolchainStatus(id, ToolchainKind.Runtime, false, false, false, null, null, null,
                $"未知工具链 '{id}'。");

        // PATH 搜索必须尊重当前进程 PATH：系统根目录的硬编码候选只在对应文件真实存在时才算命中。
        var systemExe = descriptor.StandardPaths.FirstOrDefault(File.Exists)
            ?? FindOnPath(descriptor.ExecutableNames);
        var managedRoot = ManagedRoot(layout, id);
        var managedExe = descriptor.ManagedExecutable is { } relative
            ? Path.Combine(managedRoot, relative)
            : null;
        var managedInstalled = managedExe is not null && File.Exists(managedExe);

        // Node + JS 脚本型（Pi）：两部分都齐才算可用；系统与托管可交叉组合。
        if (descriptor.ManagedScript is { } scriptRelative)
        {
            var standardScript = descriptor.StandardScripts?.FirstOrDefault(File.Exists);
            var managedScript = Path.Combine(managedRoot, scriptRelative);
            var script = standardScript ?? (File.Exists(managedScript) ? managedScript : null);
            // 依赖 node：使用 node 工具链的统一探测结果。
            var node = Detect(layout, "node");
            var nodePath = node.SystemPath ?? node.ManagedPath;
            var nodeInstalled = nodePath is not null;
            var available = nodeInstalled && script is not null;
            return new ToolchainStatus(id, descriptor.Kind, available,
                SystemInstalled: node.SystemInstalled && standardScript is not null,
                ManagedInstalled: managedInstalled,
                nodePath, managedInstalled ? managedExe : null, script,
                available ? null : $"缺少 Node 或脚本（Node: {nodePath ?? "missing"}; 脚本: {script ?? "missing"}）。");
        }

        var availableExe = systemExe is not null || managedInstalled;
        return new ToolchainStatus(id, descriptor.Kind, availableExe,
            SystemInstalled: systemExe is not null,
            ManagedInstalled: managedInstalled,
            systemExe,
            managedInstalled ? managedExe : null,
            null,
            availableExe ? null : $"未找到 {descriptor.ExecutableNames.FirstOrDefault() ?? descriptor.Id}。");
    }

    /// <summary>
    /// 把用户安装到 Fufucode 的工具链目录加入子进程 PATH。
    /// 系统已有工具链时不需要安装；这里只补充缺失项，不覆盖系统顺序。
    /// </summary>
    public static IReadOnlyList<string> ManagedPathDirectories(AppDataLayout layout)
    {
        var directories = new List<string>();
        foreach (var descriptor in All)
        {
            var root = ManagedRoot(layout, descriptor.Id);
            if (!Directory.Exists(root)) continue;
            foreach (var entry in descriptor.ManagedPathEntries)
            {
                var directory = Path.GetFullPath(Path.Combine(root, entry));
                if (Directory.Exists(directory) && !directories.Contains(directory, StringComparer.OrdinalIgnoreCase))
                    directories.Add(directory);
            }
        }
        return directories;
    }

    public static string? FindOnPath(IReadOnlyList<string> executableNames)
    {
        if (executableNames.Count == 0) return null;
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in executableNames)
            {
                foreach (var candidate in ExpandNames(name))
                {
                    var full = Path.Combine(directory.Trim(), candidate);
                    if (File.Exists(full)) return Path.GetFullPath(full);
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> ExpandNames(string name)
    {
        yield return name;
        if (!OperatingSystem.IsWindows()) yield break;
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            yield return name + ".exe";
            yield return name + ".cmd";
        }
    }

    private static IReadOnlyList<ToolchainDescriptor> Build()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var nodePaths = new[]
        {
            Path.Combine(programFiles, "nodejs", OperatingSystem.IsWindows() ? "node.exe" : "node"),
            Path.Combine(programFilesX86, "nodejs", OperatingSystem.IsWindows() ? "node.exe" : "node"),
            Path.Combine(localAppData, "Programs", "node", OperatingSystem.IsWindows() ? "node.exe" : "node"),
        };

        return
        [
            // ── 通用运行时 ──
            new("node", "Node.js", ToolchainKind.Runtime,
                "Pi 后端与 Node 工具链依赖。优先使用系统安装。",
                ["node"], nodePaths, "node.exe", ["."], []),
            new("bun", "Bun", ToolchainKind.Runtime,
                "JavaScript 运行时与包管理器；OMP 等工具可使用。优先使用系统安装。",
                ["bun"], [Path.Combine(userProfile, ".bun", "bin", "bun.exe")], "bun.exe", ["."], []),
            new("python", "Python", ToolchainKind.Runtime,
                "Seep 逆向工作台等插件依赖的 Python 运行时。优先使用系统安装。",
                ["python", "python3"],
                [
                    Path.Combine(programFiles, "Python313", "python.exe"),
                    Path.Combine(programFiles, "Python312", "python.exe"),
                    Path.Combine(localAppData, "Programs", "Python", "Python313", "python.exe"),
                    Path.Combine(localAppData, "Programs", "Python", "Python312", "python.exe"),
                ],
                "python.exe", ["."], []),
            new("git-bash", "Git Bash", ToolchainKind.Runtime,
                "内置终端与 Git 操作使用的 POSIX shell。优先使用系统安装。",
                ["bash"],
                [
                    Path.Combine(programFiles, "Git", "bin", "bash.exe"),
                    Path.Combine(programFilesX86, "Git", "bin", "bash.exe"),
                    Path.Combine(localAppData, "Programs", "Git", "bin", "bash.exe"),
                ],
                Path.Combine("bin", "bash.exe"),
                [".", "bin", Path.Combine("usr", "bin"), Path.Combine("mingw64", "bin")], []),

            // ── 后端 CLI ──
            new("pi", "Pi", ToolchainKind.Backend,
                "Pi RPC 后端。脚本型工具：依赖 node 工具链。",
                ["node"], nodePaths, "node.exe", ["."], ["node"],
                ManagedScript: PiCliRelative,
                StandardScripts:
                [
                    Path.Combine(appData, "npm", "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js"),
                    Path.Combine(userProfile, ".npm-global", "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js"),
                    Path.Combine(userProfile, ".local", "share", "npm", "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js"),
                ],
                ConfigDirectoryEnvironmentVariable: "PI_CODING_AGENT_DIR"),
            new("omp", "OMP", ToolchainKind.Backend,
                "Oh My Pi 独立 CLI 引擎。",
                ["omp"],
                [
                    Path.Combine(localAppData, "Tern", "omp", "bin", "omp.exe"),
                    Path.Combine(userProfile, ".bun", "bin", "omp.exe"),
                    Path.Combine(userProfile, "go", "bin", "omp.exe"),
                ], "omp.exe", ["."], []),
            new("claude", "Claude Code", ToolchainKind.Backend,
                "Claude Code 官方 CLI 的 stream-json 适配后端。",
                ["claude"],
                [
                    Path.Combine(appData, "npm", "claude.cmd"),
                    Path.Combine(localAppData, "Programs", "claude", "claude.exe"),
                    Path.Combine(userProfile, ".local", "bin", "claude"),
                    Path.Combine(userProfile, ".claude", "local", "claude"),
                ], "claude.exe", ["."], [], ConfigDirectoryEnvironmentVariable: "CLAUDE_CONFIG_DIR"),
            new("codex", "Codex", ToolchainKind.Backend,
                "Codex 官方 CLI 的 exec --json 适配后端。",
                ["codex"],
                [
                    Path.Combine(appData, "npm", "codex.cmd"),
                    Path.Combine(localAppData, "Programs", "codex", "codex.exe"),
                    Path.Combine(userProfile, ".npm-global", "bin", "codex"),
                    "/usr/local/bin/codex", "/usr/bin/codex",
                ], "codex.exe", ["."], [], ConfigDirectoryEnvironmentVariable: "CODEX_HOME"),
            new("dsh", "DeepSeek Harness", ToolchainKind.Backend,
                "DeepSeek Harness 官方 CLI 的 headless JSON 适配后端。",
                ["dsh"],
                [
                    Path.Combine(appData, "npm", "dsh.cmd"),
                    @"C:\customprograms\DeepSeek Harness\resources\runtime\cli\bin\dsh.cmd",
                    Path.Combine(userProfile, ".npm-global", "bin", "dsh"),
                    "/usr/local/bin/dsh", "/usr/bin/dsh",
                ], "dsh.exe", ["."], []),
        ];
    }
}
