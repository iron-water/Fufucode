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
