using System;
using System.IO;
using System.Linq;

namespace Fufucode.Core.Plugins;

internal static class PluginPathGuard
{
    public static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Length > 180 ||
            value.EndsWith('.') || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+')))
            throw new InvalidDataException("插件路径标识不是合法的单个名称。");
        var stem = value.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             stem[3] is >= '1' and <= '9'))
            throw new InvalidDataException("插件路径包含保留设备名称。");
        return value;
    }

    public static string Within(string root, params string[] segments)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(new[] { fullRoot }.Concat(segments).ToArray()));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("插件路径越出受管目录。");
        RejectLinks(path);
        return path;
    }

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("插件载荷路径不能经过符号链接或目录联接。");
        }
    }
}
