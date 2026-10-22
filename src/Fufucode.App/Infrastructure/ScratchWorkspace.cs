using System;
using System.IO;
using Fufucode.Core;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// “不在项目中工作”的独立工作区：会话仍需要一个目录才能运行，
/// 这里使用应用数据根下的 workspace，不出现在项目列表与最近项目里。
/// </summary>
public static class ScratchWorkspace
{
    public const string DisplayName = "不在项目中工作";

    public static string PathFor(AppDataLayout layout) => Path.Combine(layout.Root, "workspace");

    public static string Ensure(AppDataLayout layout)
    {
        var path = PathFor(layout);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>路径是否属于独立工作区（含其下任一子目录）。</summary>
    public static bool Contains(AppDataLayout layout, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return false;
        try
        {
            var full = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetFullPath(PathFor(layout)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    public static bool Matches(AppDataLayout layout, string projectPath) =>
        RealPathEquals(PathFor(layout), projectPath);

    private static bool RealPathEquals(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }
}
