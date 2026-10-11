using System;
using System.Collections.Generic;
using System.IO;

namespace Fufucode.App.Features.Storage;

/// <summary>路径安全判定结果。除 <see cref="Safe"/> 外都表示「不得据此路径执行删除」。</summary>
internal enum StoragePathSafety
{
    /// <summary>从可信边界到该对象的逐级属性均可读且都不是重解析点。</summary>
    Safe,
    /// <summary>该对象或它的祖先之一是重解析点（junction / 符号链接 / 文件链接）。</summary>
    Link,
    /// <summary>该对象已不存在（祖先或自身缺失）。</summary>
    Missing,
    /// <summary>属性无法读取（权限、路径长度等）。按「不安全」处理，绝不当作可删除。</summary>
    Unreadable,
}

internal readonly record struct StoragePathCheck(StoragePathSafety Safety, string Path, string? Reason)
{
    public bool IsSafe => Safety == StoragePathSafety.Safe;
}

/// <summary>
/// 重解析点（junction / 目录符号链接 / 文件符号链接）的统一判定。发现、预览与执行都用这一处规则：
/// 不跟随链接、不删除链接目标。字符串前缀只说明拼写，不能证明实际落点，因此按路径逐级读取属性。
/// 属性读取失败**不能**当作安全：一律返回 <see cref="StoragePathSafety.Unreadable"/>。
/// </summary>
internal static class StoragePathGuard
{
    /// <summary>读取单个条目的属性并判定；读取失败按不安全处理。</summary>
    public static StoragePathCheck CheckElement(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0
                ? new StoragePathCheck(StoragePathSafety.Link, path, null)
                : new StoragePathCheck(StoragePathSafety.Safe, path, null);
        }
        catch (FileNotFoundException)
        {
            return new StoragePathCheck(StoragePathSafety.Missing, path, "条目已不存在");
        }
        catch (DirectoryNotFoundException)
        {
            return new StoragePathCheck(StoragePathSafety.Missing, path, "条目已不存在");
        }
        catch (Exception error)
        {
            // 权限、路径长度等：无法确认安全性，按不可安全删除处理。
            return new StoragePathCheck(StoragePathSafety.Unreadable, path, "无法读取属性：" + error.Message);
        }
    }

    /// <summary>
    /// 从 <paramref name="rootBoundary"/> 逐级向下检查到 <paramref name="path"/>（含边界目录与目标自身）：
    /// 任一级是重解析点返回 <see cref="StoragePathSafety.Link"/>，属性不可读返回
    /// <see cref="StoragePathSafety.Unreadable"/>，缺失返回 <see cref="StoragePathSafety.Missing"/>。
    /// 只有全部可读且都不是链接才返回 <see cref="StoragePathSafety.Safe"/>。
    /// </summary>
    public static StoragePathCheck CheckChain(string path, string rootBoundary)
    {
        var root = Path.GetFullPath(rootBoundary);
        var full = Path.GetFullPath(path);
        if (!IsUnder(full, root)) return new StoragePathCheck(StoragePathSafety.Unreadable, full, "路径不在允许清理的区域内");

        var rootCheck = CheckElement(root);
        if (!rootCheck.IsSafe) return rootCheck with { Path = root };

        var relative = Path.GetRelativePath(root, full);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0 || segment == ".") continue;
            current = Path.Combine(current, segment);
            var check = CheckElement(current);
            if (!check.IsSafe) return check;
        }
        return new StoragePathCheck(StoragePathSafety.Safe, full, null);
    }

    public static bool IsReparsePoint(string path) => CheckElement(path).Safety == StoragePathSafety.Link;

    public static bool Exists(string path) => CheckElement(path).Safety != StoragePathSafety.Missing;

    public static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    public static bool IsUnder(string path, string directory)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 带记忆的祖先链检查：同一次清理内父目录结果可复用，因此每个条目只多读一次属性。
    /// 用于执行阶段逐个快照条目复核「从可信边界到该对象」的完整链。
    /// </summary>
    internal sealed class ChainGuard
    {
        private readonly string _root;
        private readonly Dictionary<string, StoragePathCheck> _cache = new(StringComparer.OrdinalIgnoreCase);

        public ChainGuard(string rootBoundary)
        {
            _root = Path.GetFullPath(rootBoundary);
            _cache[_root] = CheckElement(_root);
        }

        public StoragePathCheck Check(string path)
        {
            var full = Path.GetFullPath(path);
            if (_cache.TryGetValue(full, out var cached)) return cached;
            if (!IsUnder(full, _root))
            {
                return new StoragePathCheck(StoragePathSafety.Unreadable, full, "路径不在允许清理的区域内");
            }

            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full));
            StoragePathCheck result;
            if (string.IsNullOrEmpty(parent) || SamePath(parent, _root))
            {
                var rootCheck = _cache[_root];
                result = rootCheck.IsSafe ? CheckElement(full) : rootCheck;
            }
            else
            {
                var parentCheck = Check(parent);
                result = parentCheck.IsSafe ? CheckElement(full) : parentCheck;
            }
            _cache[full] = result;
            return result;
        }
}
}
