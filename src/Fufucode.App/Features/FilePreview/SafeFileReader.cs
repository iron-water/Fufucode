using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.App.Features.FilePreview;

public sealed record SafeFileReadResult(
    bool Success,
    string FullPath,
    long Size,
    string? Text,
    byte[] Bytes,
    string? Encoding,
    bool IsTruncated,
    string? Error)
{
    public static SafeFileReadResult Failure(string path, string error) =>
        new(false, path, 0, null, [], null, false, error);
}

/// <summary>只读文件读取器：所有 provider 都通过此类读取，统一执行项目边界和资源限制。</summary>
public sealed class SafeFileReader
{
    public const long DefaultMaxBytes = 8L * 1024 * 1024;
    private const int MaxLinkDepth = 32;

    public async Task<SafeFileReadResult> ReadAsync(FilePreviewRequest request,
        long maxBytes = DefaultMaxBytes, CancellationToken cancellationToken = default, bool allowTruncate = false)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        string requested;
        try
        {
            requested = Path.GetFullPath(request.Path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return SafeFileReadResult.Failure(request.Path, $"路径无效：{error.Message}");
        }

        try
        {
            var root = Path.GetFullPath(request.ProjectRoot);
            var resolvedRoot = ResolveExistingPath(root, root, 0);
            var resolvedPath = ResolveExistingPath(requested, root, 0);
            if (!IsWithinRoot(resolvedPath, resolvedRoot))
                return SafeFileReadResult.Failure(requested, "文件路径超出当前项目范围。");
            if (!File.Exists(resolvedPath))
                return SafeFileReadResult.Failure(requested, "文件不存在或已被移除。");

            var info = new FileInfo(resolvedPath);
            if (info.Length > maxBytes && !allowTruncate)
                return SafeFileReadResult.Failure(requested,
                    $"文件过大：{FormatSize(info.Length)}，预览上限为 {FormatSize(maxBytes)}。") with { Size = info.Length, FullPath = resolvedPath };

            await using var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytes = new byte[checked((int)Math.Min(info.Length, maxBytes) + 1)];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count, bytes.Length - count), cancellationToken);
                if (read == 0) break;
                count += read;
            }

            var truncated = count > maxBytes;
            var payload = bytes[..(int)Math.Min(count, maxBytes)];
            var detection = DetectEncoding(payload);
            string? text = null;
            if (detection.Encoding is not null)
            {
                try { text = detection.Encoding.GetString(payload, detection.PreambleLength, payload.Length - detection.PreambleLength); }
                catch (DecoderFallbackException error) { return SafeFileReadResult.Failure(resolvedPath, $"文件编码无法识别：{error.Message}"); }
            }

            return new(true, resolvedPath, info.Length, text, payload, detection.Name, truncated,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return SafeFileReadResult.Failure(requested, $"读取文件失败：{error.Message}");
        }
    }

    public string? ResolvePathWithinRoot(string projectRoot, string path, out string? error)
    {
        error = null;
        try
        {
            var root = Path.GetFullPath(projectRoot);
            var requested = Path.GetFullPath(path);
            var resolvedRoot = ResolveExistingPath(root, root, 0);
            var resolved = ResolveExistingPath(requested, root, 0);
            if (!IsWithinRoot(resolved, resolvedRoot))
            {
                error = "路径超出当前项目范围。";
                return null;
            }
            return resolved;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"路径无法解析：{exception.Message}";
            return null;
        }
    }

    private static string ResolveExistingPath(string path, string root, int depth)
    {
        if (depth > MaxLinkDepth) throw new IOException("符号链接层级过深。");
        var full = Path.GetFullPath(path);
        var rootFull = Path.GetFullPath(root);
        if (!IsWithinRoot(full, rootFull)) throw new IOException("路径超出当前项目范围。");
        var relative = Path.GetRelativePath(rootFull, full);
        var current = rootFull;
        if (relative is "." or "") return current;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            if (!File.Exists(next) && !Directory.Exists(next))
            {
                current = next;
                continue;
            }
            var attributes = File.GetAttributes(next);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                current = next;
                continue;
            }

            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            var target = info.ResolveLinkTarget(true) ?? throw new IOException("链接目标不存在。");
            current = ResolveExistingPath(target.FullName, rootFull, depth + 1);
        }
        return current;
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedPath, normalizedRoot, PathComparison) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static (Encoding? Encoding, string? Name, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return (new UTF8Encoding(false, true), "UTF-8 BOM", 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            return (new UTF32Encoding(false, true, true), "UTF-32 LE", 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            return (new UTF32Encoding(true, true, true), "UTF-32 BE", 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return (new UnicodeEncoding(false, false, true), "UTF-16 LE", 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return (new UnicodeEncoding(true, false, true), "UTF-16 BE", 2);
        return (new UTF8Encoding(false, false), "UTF-8", 0);
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:F1} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):F1} MiB",
        _ => $"{bytes / (1024d * 1024 * 1024):F2} GiB",
    };
}
