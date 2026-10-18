using System;

namespace Fufucode.Core.Runtimes;

/// <summary>
/// 把用户安装到 Fufucode 的工具链目录注入当前进程 PATH。
/// 子进程默认继承父进程环境，因此 git、node、python、bash 等所有
/// Process.Start 调用无需逐个修改即可看到这些目录。
/// 系统已有工具链时仍排在最前，托管目录只补充缺失项。
/// </summary>
public static class ToolchainEnvironment
{
    private static readonly object Gate = new();
    private static string? _appliedRoot;

    public static void ApplyToProcess(AppDataLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        lock (Gate)
        {
            if (string.Equals(_appliedRoot, layout.Root, StringComparison.OrdinalIgnoreCase)) return;
            var directories = ToolchainCatalog.ManagedPathDirectories(layout);
            if (directories.Count > 0)
            {
                var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                var parts = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
                var additions = directories
                    .Where(directory => !parts.Contains(directory, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                if (additions.Length > 0)
                {
                    Environment.SetEnvironmentVariable("PATH",
                        string.Join(Path.PathSeparator, additions.Concat(parts)));
                }
            }
            _appliedRoot = layout.Root;
        }
    }

    /// <summary>测试与进程重启后重新注入。</summary>
    public static void ResetForTesting()
    {
        lock (Gate) _appliedRoot = null;
    }
}
