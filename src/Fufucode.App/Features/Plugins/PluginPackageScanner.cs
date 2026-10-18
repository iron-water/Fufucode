using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Fufucode.App.Features.Plugins;

/// <summary>导入的保守上限：超过即拒绝，不自动清理历史。</summary>
public sealed record LocalPluginLimits(
    long MaxTotalBytes = 20L * 1024 * 1024,
    int MaxFileCount = 2000,
    int MaxDirectoryDepth = 12)
{
    public static LocalPluginLimits Default { get; } = new();
}

/// <summary>
/// 本地插件包的只读扫描：校验入口、拒绝越界与链接遍历、计算版本摘要。
/// 只做检查，不复制、不写任何目标目录。
/// </summary>
public static class PluginPackageScanner
{
    /// <summary>入口校验与越界拒绝；返回选定入口的绝对路径。</summary>
    public static (string? EntryAbsolutePath, LocalPluginImportRejection? Rejection) ResolveEntry(
        string root, string entryRelativePath)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.IsNullOrWhiteSpace(entryRelativePath))
        {
            return (null, new LocalPluginImportRejection(LocalPluginRejectionKind.MissingEntry,
                "必须显式选择入口文件（相对路径）。"));
        }

        // 绝对路径或盘符根直接拒绝：入口必须是包内相对路径。
        if (Path.IsPathRooted(entryRelativePath) || entryRelativePath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return (null, new LocalPluginImportRejection(LocalPluginRejectionKind.EntryOutsideRoot,
                "入口必须是包内相对路径，不接受绝对路径或 UNC。"));
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, entryRelativePath));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, new LocalPluginImportRejection(LocalPluginRejectionKind.EntryOutsideRoot,
                $"入口路径不合法：{error.GetBaseException().Message}"));
        }

        if (!IsInsideRoot(root, full))
        {
            return (null, new LocalPluginImportRejection(LocalPluginRejectionKind.EntryOutsideRoot,
                "入口超出插件目录范围（拒绝 .. 越界）。"));
        }

        if (!File.Exists(full))
        {
            return (null, new LocalPluginImportRejection(LocalPluginRejectionKind.MissingEntry,
                $"入口文件不存在：{entryRelativePath}"));
        }

        var extension = Path.GetExtension(full);
        if (!extension.Equals(".js", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase))
        {
            return (null, new LocalPluginImportRejection(LocalPluginRejectionKind.MissingEntry,
                $"入口必须是 .js/.ts/.mjs/.cjs 文件，实际为 “{extension}”。"));
        }

        return (full, null);
    }

    /// <summary>
    /// 遍历候选包：统计大小与文件数、拒绝符号链接/reparse 点遍历。返回 null 表示可导入。
    /// </summary>
    public static LocalPluginImportRejection? Inspect(string root, LocalPluginLimits limits)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        long total = 0;
        var count = 0;
        var stack = new Stack<(string Directory, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (directory, depth) = stack.Pop();
            if (depth > limits.MaxDirectoryDepth)
            {
                return new LocalPluginImportRejection(LocalPluginRejectionKind.FileCountLimitExceeded,
                    $"目录层级超过上限 {limits.MaxDirectoryDepth}。");
            }

            if (IsReparse(directory))
            {
                return new LocalPluginImportRejection(LocalPluginRejectionKind.SymlinkTraversal,
                    "目录中包含符号链接或重解析点：拒绝遍历。");
            }

            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory);
                children = Directory.GetDirectories(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return new LocalPluginImportRejection(LocalPluginRejectionKind.SourceUnreadable,
                    $"无法读取目录：{error.GetBaseException().Message}");
            }

            foreach (var file in files)
            {
                if (IsReparse(file))
                {
                    return new LocalPluginImportRejection(LocalPluginRejectionKind.SymlinkTraversal,
                        "包内存在符号链接或重解析点文件：拒绝导入。");
                }

                FileInfo info;
                try { info = new FileInfo(file); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return new LocalPluginImportRejection(LocalPluginRejectionKind.SourceUnreadable,
                        $"无法读取文件：{error.GetBaseException().Message}");
                }

                total += info.Length;
                count++;
                if (total > limits.MaxTotalBytes)
                {
                    return new LocalPluginImportRejection(LocalPluginRejectionKind.SizeLimitExceeded,
                        $"包体积超过上限 {limits.MaxTotalBytes / (1024 * 1024)} MB。");
                }

                if (count > limits.MaxFileCount)
                {
                    return new LocalPluginImportRejection(LocalPluginRejectionKind.FileCountLimitExceeded,
                        $"文件数量超过上限 {limits.MaxFileCount}。");
                }
            }

            foreach (var child in children) stack.Push((child, depth + 1));
        }

        return null;
    }

    /// <summary>
    /// 版本摘要：逐文件相对路径 + 字节内容进入 SHA256，因此"同版本名不同内容"必然产生不同摘要。
    /// </summary>
    public static string ComputeContentHash(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var entries = new List<(string Relative, byte[] Bytes)>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            foreach (var file in Directory.GetFiles(directory))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                entries.Add((relative, File.ReadAllBytes(file)));
            }

            foreach (var child in Directory.GetDirectories(directory)) stack.Push(child);
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));
        using var sha = SHA256.Create();
        foreach (var (relative, bytes) in entries)
        {
            var header = Encoding.UTF8.GetBytes(relative + "\0" + bytes.Length + "\0");
            sha.TransformBlock(header, 0, header.Length, null, 0);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    /// <summary>把源目录复制到本轮新暂存目录；只走普通文件与目录，不跟随链接。</summary>
    public static void CopyToStaging(string root, string staging)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Directory.CreateDirectory(staging);
        var stack = new Stack<(string Source, string Target)>();
        stack.Push((root, staging));
        while (stack.Count > 0)
        {
            var (source, target) = stack.Pop();
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                if (IsReparse(file)) continue;
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var child in Directory.GetDirectories(source))
            {
                if (IsReparse(child)) continue;
                stack.Push((child, Path.Combine(target, Path.GetFileName(child))));
            }
        }
    }

    public static bool IsInsideRoot(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsReparse(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 读不到属性时按可疑处理：不静默放行。
            return true;
        }
    }
}
