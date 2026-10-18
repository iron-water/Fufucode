using System;
using System.IO;

namespace Fufucode.App.Features.Automation.Persistence;

/// <summary>
/// 本模块固定目录内的路径自证：目标必须落在目录内，且从目录到目标的每一级都不是重解析点，
/// 属性不可读时按「不安全」处理（不当作可读写）。链接不跟随，因此改名/换盘不会把写入带出目录。
/// </summary>
internal static class AutomationPersistencePathGuard
{
    /// <summary>返回 null 表示可安全读写；否则给出可诊断的失败码与原因。</summary>
    public static AutomationPersistenceFailure? CheckUse(string targetPath, string directoryPath)
    {
        string directory;
        string target;
        try
        {
            directory = Path.GetFullPath(directoryPath);
            target = Path.GetFullPath(targetPath);
        }
        catch (Exception pathError)
        {
            return new(AutomationPersistenceErrorCode.ScopeOutsideRoot, "路径无法解析：" + pathError.Message);
        }

        var boundary = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return new(AutomationPersistenceErrorCode.ScopeOutsideRoot, "目标路径不在本模块固定目录内：" + target);
        }

        foreach (var probe in Chain(target, directory))
        {
            try
            {
                var attributes = File.GetAttributes(probe);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return new(AutomationPersistenceErrorCode.ReparsePoint, "路径含重解析点（junction/符号链接）：" + probe);
                }
            }
            catch (Exception attributeError) when (attributeError is FileNotFoundException or DirectoryNotFoundException)
            {
                // 尚未创建的条目（临时文件等）本身无属性；其祖先已在更早的循环里核验过。
                continue;
            }
            catch (Exception attributeError)
            {
                return new(AutomationPersistenceErrorCode.RootUnavailable, "无法确认路径安全性：" + probe + " · " + attributeError.Message);
            }
        }
        return null;
    }

    /// <summary>路径存在且是重解析点时返回可诊断说明与错误码；不存在或普通目录返回 null。</summary>
    public static AutomationPersistenceFailure? ReparseIssue(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return new AutomationPersistenceFailure(AutomationPersistenceErrorCode.ReparsePoint,
                    "该路径本身是重解析点（junction/符号链接），本模块不跟随链接：" + path);
            return null;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception error)
        {
            // 无法判定即按不安全处理，但不谎称它是链接。
            return new AutomationPersistenceFailure(AutomationPersistenceErrorCode.RootUnavailable,
                "无法确认该路径是否为链接：" + path + " · " + error.Message);
        }
    }

    private static System.Collections.Generic.IEnumerable<string> Chain(string target, string directory)
    {
        var relative = Path.GetRelativePath(directory, target);
        var current = directory;
        yield return current;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0 || segment == ".") continue;
            current = Path.Combine(current, segment);
            yield return current;
        }
    }
}
