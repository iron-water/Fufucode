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
    /// <summary>定位 Node 与 Pi CLI；缺文件时抛出带可操作信息的诊断，不静默降级。</summary>
    public static (string Node, string Cli) ResolveRuntime(AppOptions options, bool required = true)
    {
        var root = FindRuntimeRoot() ?? AppContext.BaseDirectory;
        var node = options.PiNodePath ??
                   (root is null ? null : Path.Combine(root, "runtime", "node", "node.exe"));
        var cli = options.PiCliPath ??
                  (root is null ? null : Path.Combine(root, "runtime", "pi", "node_modules",
                      "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js"));

        var missing = new List<string>();
        if (node is null || !File.Exists(node))
        {
            missing.Add($"Node 运行时（期望 {node ?? "<仓库>/runtime/node/node.exe"}）");
        }

        if (cli is null || !File.Exists(cli))
        {
            missing.Add($"Pi CLI（期望 {cli ?? "<仓库>/runtime/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js"}）");
        }

        if (required && missing.Count > 0)
        {
            throw new FileNotFoundException(
                "Pi 运行环境缺失：" + string.Join("；", missing) +
                "。可用 --pi-node <路径> 与 --pi-cli <路径> 显式指定，或先按 README『Runtime』一节准备运行目录。");
        }

        return (Path.GetFullPath(node!), Path.GetFullPath(cli!));
    }

    public static string ResolveOmpRuntime(AppOptions options) => options.OmpExecutablePath ??
        Path.Combine(FindRuntimeRoot() ?? AppContext.BaseDirectory, "runtime", "omp", "omp.exe");

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
