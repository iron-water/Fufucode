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
        ArgumentNullException.ThrowIfNull(options);
        var root = FindRuntimeRoot() ?? AppContext.BaseDirectory;
        // 与 OMP/Claude/Codex/DSH 同一条解析路径：显式参数 → 仓库/应用自带 runtime/ →
        // 统一工具链表（用户自带运行时优先于系统安装）。路径候选不再在这里另写一份。
        var status = Fufucode.Core.Runtimes.ToolchainCatalog.Detect(
            options.CreateAppDataLayout(), Fufucode.Core.BackendCapabilities.StorageId(Fufucode.Core.BackendId.Pi));

        var node = FirstExisting(
            options.PiNodePath,
            Path.Combine(root, "runtime", "node", "node.exe"),
            status.ManagedPath,
            status.SystemPath);
        var cli = FirstExisting(
            options.PiCliPath,
            Path.Combine(root, "runtime", "pi", Fufucode.Core.Runtimes.ToolchainCatalog.PiScriptRelative),
            status.ScriptPath);

        var missing = new List<string>();
        if (node is null || !File.Exists(node))
        {
            missing.Add($"Node 运行时（期望 {node ?? "仓库/应用 runtime/node/node.exe，或 fufucode runtime install node"}）");
        }

        if (cli is null || !File.Exists(cli))
        {
            missing.Add($"Pi CLI（期望 {cli ?? $"仓库/应用 runtime/pi 或用户运行时下的 {Fufucode.Core.Runtimes.ToolchainCatalog.PiScriptRelative}"}）");
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

    // 候选按优先级排列：取第一个真实存在的；都不存在时返回第一个非空值，供诊断文案指出"期望在哪"。
    private static string? FirstExisting(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
        }
        return Array.Find(candidates, candidate => !string.IsNullOrEmpty(candidate));
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
