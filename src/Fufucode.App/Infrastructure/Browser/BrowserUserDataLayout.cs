using System;
using System.IO;

namespace Fufucode.App.Infrastructure.Browser;

/// <summary>
/// 浏览数据（缓存、cookie、历史）的唯一落点：应用数据根目录下的 <c>webview</c> 子目录。
/// 不写入项目目录、不写入安装目录；自测模式重定向到隔离临时根，避免触碰真实 %LocalAppData%。
/// </summary>
internal static class BrowserUserDataLayout
{
    internal const string AppFolderName = "Fufucode";
    internal const string FolderName = "webview";

    /// <summary>
    /// 解析 UserDataFolder。<paramref name="overrideRoot"/> 非空（自测隔离）时在其下建子目录，
    /// 否则使用 <c>%LocalAppData%\Fufucode\webview</c>。
    /// </summary>
    public static string Resolve(string? overrideRoot)
    {
        var root = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName)
            : Path.GetFullPath(overrideRoot);
        var folder = Path.GetFullPath(Path.Combine(root, FolderName));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!folder.StartsWith(prefix, comparison))
            throw new InvalidOperationException("浏览器数据目录必须位于应用数据根目录之内。");
        return folder;
    }

    /// <summary>创建目录；失败时返回可读原因而不是抛出，供界面显示。</summary>
    public static string? TryEnsure(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return error.Message;
        }
    }
}
