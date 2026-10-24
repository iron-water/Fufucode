using System;
using System.Collections.Generic;
using System.IO;

namespace Fufucode.App.Integration;

/// <summary>
/// 组合根的真实侧运行时定位：找到应用管理的 Node 与 Pi CLI，缺失时报带可操作信息的诊断。
/// 进程参数（--session-dir 指向应用数据目录、工作目录绑定项目、PI_CODING_AGENT_DIR）
/// 全部由 PersistentSessionManager 按计划 §7.2 组装，这里不重复拼参数。
/// 会话生命周期（新建/切换/重命名/草稿）见 <see cref="RealSessionHost"/>。
/// </summary>
public static class RealSessionBootstrap
{
    /// <summary>
    /// 定位 Node 与 Pi CLI。仅当调用方明确要使用 Pi/OMP 后端时才需要；
    /// FUMO 原生内核（默认后端）不调用本方法，也不会因为缺这些文件而失败。
    /// 缺文件时抛出带可操作信息的诊断，不静默降级。
    /// </summary>
    public static (string Node, string Cli) ResolveRuntime(AppOptions options, bool required = true)
    {
        var root = FindRuntimeRoot() ?? AppContext.BaseDirectory;
        // 运行时可放在三处：显式参数、仓库/应用自带 runtime/、按需安装的用户运行时。
        var managedRoot = Path.Combine(
            Fufucode.Core.AppDataLayout.DefaultRoot(),
            "runtime", "pi");
        var cliRelative = Path.Combine("node_modules", "@earendil-works", "pi-coding-agent",
            "dist", "bundle", "cli.js");

        var node = options.PiNodePath ??
                   (root is null ? null : Path.Combine(root, "runtime", "node", "node.exe")) ??
                   (File.Exists(Path.Combine(managedRoot, "node.exe"))
                       ? Path.Combine(managedRoot, "node.exe") : null);
        if (node is null || !File.Exists(node))
        {
            node = FindNodeInSystem() ?? node;
        }
        var cli = options.PiCliPath ??
                  (root is null ? null : Path.Combine(root, "runtime", "pi", cliRelative)) ??
                  (File.Exists(Path.Combine(managedRoot, cliRelative))
                      ? Path.Combine(managedRoot, cliRelative) : null);
        if (cli is null || !File.Exists(cli))
        {
            cli = FindPiCliInSystem() ?? cli;
        }

        var missing = new List<string>();
        if (node is null || !File.Exists(node))
        {
            missing.Add($"Node 运行时（期望 {node ?? "<仓库>/runtime/node/node.exe 或 %LOCALAPPDATA%/Fufucode/runtime/pi/node.exe"}）");
        }

        if (cli is null || !File.Exists(cli))
        {
            missing.Add($"Pi CLI（期望 {cli ?? "<仓库>/runtime/pi 或 %LOCALAPPDATA%/Fufucode/runtime/pi 下的 node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js"}）");
        }

        if (required && missing.Count > 0)
        {
            throw new FileNotFoundException(
                "Pi 运行环境缺失：" + string.Join("；", missing) +
                "。可用 --pi-node <路径> 与 --pi-cli <路径> 显式指定，或先按 docs/runtime-baseline.md 准备运行目录。");
        }

        return (node is not null ? Path.GetFullPath(node) : string.Empty,
                cli is not null ? Path.GetFullPath(cli) : string.Empty);
    }

    // 五个后端同一解析顺序：显式参数 > 仓库 runtime/<id> > 系统安装/用户运行时（统一表）。
    private static string? ResolveFromCatalog(AppOptions options, Fufucode.Core.BackendId backend,
        string? explicitPath)
    {
        if (explicitPath is { Length: > 0 } && File.Exists(explicitPath)) return explicitPath;
        var local = Path.Combine(FindRuntimeRoot() ?? AppContext.BaseDirectory, "runtime",
            Fufucode.Core.BackendCapabilities.StorageId(backend),
            OperatingSystem.IsWindows() ? Fufucode.Core.BackendCapabilities.StorageId(backend) + ".exe"
                : Fufucode.Core.BackendCapabilities.StorageId(backend));
        if (File.Exists(local)) return local;
        var detection = Fufucode.Core.Runtimes.ToolchainCatalog.Detect(
            options.CreateAppDataLayout(), Fufucode.Core.BackendCapabilities.StorageId(backend));
        // 显式路径与仓库 runtime 之后：用户自带运行时优先于系统安装，
        // 没有系统 CLI 的机器也能只用 Fufucode 自带运行时工作。
        return detection.ManagedPath ?? detection.SystemPath;
    }

    public static string? ResolveOmpRuntime(AppOptions options) =>
        ResolveFromCatalog(options, Fufucode.Core.BackendId.Omp, options.OmpExecutablePath);

    public static string? ResolveClaudeRuntime(AppOptions options) =>
        ResolveFromCatalog(options, Fufucode.Core.BackendId.Claude, options.ClaudeExecutablePath);

    public static string? ResolveCodexRuntime(AppOptions options) =>
        ResolveFromCatalog(options, Fufucode.Core.BackendId.Codex, options.CodexExecutablePath);

    public static string? ResolveDshRuntime(AppOptions options) =>
        ResolveFromCatalog(options, Fufucode.Core.BackendId.Dsh, options.DshExecutablePath);

    private static string? FindNodeInSystem()
    {
        var standardPaths = new[]
        {
            @"C:\Program Files\nodejs\node.exe",
            @"C:\Program Files (x86)\nodejs\node.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "node", "node.exe"),
        };
        foreach (var p in standardPaths)
        {
            if (File.Exists(p)) return p;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir.Trim(), "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string? FindPiCliInSystem()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var standardPaths = new[]
        {
            Path.Combine(appData, "npm", "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npm-global", "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js"),
        };
        foreach (var p in standardPaths)
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string? FindOmpInSystem()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var standardPaths = new[]
        {
            Path.Combine(localAppData, "Tern", "omp", "bin", "omp.exe"),
            Path.Combine(userProfile, ".bun", "bin", "omp.exe"),
            Path.Combine(userProfile, "go", "bin", "omp.exe"),
        };
        foreach (var p in standardPaths)
        {
            if (File.Exists(p)) return p;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir.Trim(), "omp.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    // 开发布局：从 bin/<配置>/net10.0 向上找到含 runtime/node/node.exe 的仓库根。
    // 打包布局（M7）由轨道 F 决定入口约定后再接入，不提前猜测。
    private static string? FindRuntimeRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "runtime", "node", "node.exe")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
