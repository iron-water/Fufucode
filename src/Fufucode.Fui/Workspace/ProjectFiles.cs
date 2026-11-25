using System.Security.Cryptography;
using System.Text;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Security;
using SkiaSharp;

namespace Fufucode.Fui;

internal enum FileAttachmentSource { Project, Clipboard }
internal sealed record ProjectImagePreview(int Width, int Height, SKColor[] Pixels, int SourceWidth, int SourceHeight);
internal sealed record FileAttachment(string Path, string Text, byte[] Digest, string? MimeType = null,
    byte[]? ImageBytes = null, int ImageWidth = 0, int ImageHeight = 0, string? FormatLabel = null,
    bool IsClipboard = false,
    ProjectImagePreview? Preview = null, byte[]? Payload = null,
    FileAttachmentSource Source = FileAttachmentSource.Project)
{
    public bool IsClipboardAttachment => IsClipboard || Source == FileAttachmentSource.Clipboard;
    public byte[]? EncodedBytes => ImageBytes ?? Payload;
}
internal sealed record FuiPrompt(string Text, IReadOnlyList<ChatImage> Images);
internal sealed record FilePage(string Folder, IReadOnlyList<FuiChoice> Items);
internal sealed record ResolvedImage(string Label, string MimeType, byte[] Bytes, DecodedImage? Decoded = null);

// Browse one directory on demand, not the whole project on each keystroke. File contents are not
// sent merely by selecting/opening a file. Attachments are explicit, bounded and checked again at send.
internal sealed class ProjectFiles
{
    internal const int MaxFileBytes = 65536, MaxAttachments = 4;
    internal const int MaxAttachmentBytes = 10 * 1024 * 1024;
    private const int MaxEntries = 1000;
    private static readonly HashSet<string> HiddenDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".svn", ".hg", "bin", "obj", "node_modules", ".venv", "__pycache__" };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _root;
    private readonly SecurityPolicyEvaluator _policy;

    public ProjectFiles(string project, string dataRoot)
    {
        _root = AppDataLayout.NormalizeProjectPath(project);
        _policy = new(new SecurityPolicyStore(dataRoot), _root);
    }

    public string Relative(string path) => Path.GetRelativePath(_root, path).Replace('\\', '/');

    public FilePage List(string folder, CancellationToken ct)
    {
        var dir = Resolve(folder, directory: true);
        var allowed = _policy.ReadablePathsForListing();
        var children = new List<(FuiChoice Item, bool Directory)>();
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || !allowed(entry.FullName)) continue;
            var child = entry.Attributes.HasFlag(FileAttributes.Directory);
            if (child && HiddenDirectories.Contains(entry.Name)) continue;
            if (children.Count == MaxEntries) throw new IOException($"目录超过 {MaxEntries} 项；用 /attach 或 /preview 指定相对路径。");
            var rel = Relative(entry.FullName);
            children.Add((new(child ? rel + "/" : rel, entry.Name + (child ? "/" : ""), child ? "目录" : rel), child));
        }
        var choices = children.OrderByDescending(c => c.Directory).ThenBy(c => c.Item.Label, StringComparer.OrdinalIgnoreCase).Select(c => c.Item).ToList();
        var here = Relative(dir);
        if (here != ".") choices.Insert(0, new(Relative(Directory.GetParent(dir)!.FullName) + "/", "../", "上一级"));
        return new(here, choices);
    }

    public async Task<FileAttachment> ReadAsync(string path, CancellationToken ct) =>
        (await ReadFileAsync(path, allowImages: false, MaxAttachmentBytes, ct)).File;

    public async Task<FileAttachment> ReadAttachmentAsync(string path, CancellationToken ct) =>
        (await ReadFileAsync(path, allowImages: true, MaxAttachmentBytes, ct)).File;

    public async Task<FileAttachment> ReadPreviewAsync(string path, CancellationToken ct) =>
        (await ReadFileAsync(path, allowImages: true, MaxAttachmentBytes, ct)).File;

    public ResolvedImage? TryReadAllowedImage(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || MarkdownImages.IsRemoteUrl(reference) || MarkdownImages.IsDataUri(reference))
            return null;
        try
        {
            var rel = NormalizeImageReference(reference);
            if (rel is null) return null;
            var full = Resolve(rel);
            var mime = ProjectImage.MimeType(full);
            if (mime is null) return null;
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 8192);
            var length = stream.Length;
            if (length is <= 0 or > MaxAttachmentBytes) return null;
            var bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) return null;
            Resolve(rel); // Recheck links and current security policy while the handle is open.
            if (mime == "image/svg+xml")
            {
                if (ImageDecoder.UnpackAndAuditSvg(bytes) is null) return null;
                return new(Relative(full), mime, bytes);
            }
            if (!ProjectImage.TryInspect(bytes, mime, out _, out _, out _, out var decoded)) return null;
            return new(Relative(full), mime, bytes, decoded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            return null;
        }
    }

    private string? NormalizeImageReference(string reference)
    {
        var value = reference.Trim();
        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
            var full = Path.GetFullPath(uri.LocalPath);
            var rel = Path.GetRelativePath(_root, full);
            if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return null;
            return rel;
        }
        // Strip optional query/fragment suffix on relative image links (e.g. chart.png?v=1#light).
        var trim = value.IndexOfAny(['?', '#']);
        if (trim >= 0) value = value[..trim];
        if (value.Length == 0) return null;
        if (!value.Contains('%')) return value;
        try
        {
            var rawFull = Path.GetFullPath(value, _root);
            if (File.Exists(rawFull)) return value;
            return Uri.UnescapeDataString(value);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return value;
        }
    }

    public Task<FileAttachment> ReadClipboardImageAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(NormalizeClipboard(ClipboardImage.ReadAttachment()));
    }

    private FileAttachment NormalizeClipboard(FileAttachment file)
    {
        var bytes = file.EncodedBytes;
        if (bytes is not { Length: > 0 } || bytes.Length > MaxAttachmentBytes ||
            file.MimeType != "image/png" ||
            !ProjectImage.TryInspect(bytes, "image/png", out var width, out var height, out var label, out _))
            throw new InvalidDataException("剪贴板图片不是有效的 PNG。");
        var digest = SHA256.HashData(bytes);
        return file with
        {
            ImageBytes = bytes,
            Payload = bytes,
            Digest = digest,
            ImageWidth = width,
            ImageHeight = height,
            FormatLabel = label,
            IsClipboard = true,
            Source = FileAttachmentSource.Clipboard,
            Preview = ProjectImage.CreatePreview(bytes, "image/png")
        };
    }

    internal FileAttachment ClipboardAttachment(byte[] bytes)
    {
        bytes = bytes.ToArray();
        if (bytes.Length > MaxAttachmentBytes)
            throw new IOException($"每条消息的附件总量最多 {MaxAttachmentBytes / 1024 / 1024} MiB。");
        if (!ProjectImage.TryInspect(bytes, "image/png", out var width, out var height, out var label, out _))
            throw new InvalidDataException("剪贴板图片不是有效的 PNG。");
        var digest = SHA256.HashData(bytes);
        var name = $"剪贴板-{DateTime.Now:yyyyMMdd-HHmmss}-{Convert.ToHexString(digest.AsSpan(0, 2)).ToLowerInvariant()}.png";
        return new(name, "", digest, "image/png", bytes, width, height, label,
            IsClipboard: true, Preview: ProjectImage.CreatePreview(bytes, "image/png"),
            Payload: bytes, Source: FileAttachmentSource.Clipboard);
    }

    private async Task<(FileAttachment File, byte[] Bytes, DecodedImage? Decoded)> ReadFileAsync(string path, bool allowImages, int remaining, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var full = Resolve(path);
        var mime = allowImages ? ProjectImage.MimeType(full) : null;
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        var length = stream.Length;
        if (mime is null && length > MaxFileBytes) throw new IOException($"文本文件超过 {MaxFileBytes / 1024} KiB。");
        if (length > remaining) throw new IOException($"每条消息的附件总量最多 {MaxAttachmentBytes / 1024 / 1024} MiB。");
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, ct);
        if (stream.ReadByte() != -1) throw new IOException("文件在读取时增长；请重新选择。");
        Resolve(path); // Recheck links and the current read policy after I/O, while this handle is still open.
        ct.ThrowIfCancellationRequested();
        if (mime is not null)
        {
            if (!ProjectImage.TryInspect(bytes, mime, out var width, out var height, out var label, out var decoded))
                throw new InvalidDataException($"{path} 不是有效或与扩展名相符的图片文件。");
            var preview = decoded is not null
                ? ProjectImage.CreatePreview(decoded)
                : ProjectImage.CreatePreview(bytes, mime);
            return (new(Relative(full), "", SHA256.HashData(bytes), mime, bytes, width, height, label,
                Preview: preview, Payload: bytes), bytes, decoded);
        }
        string text;
        try { text = Utf8.GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new InvalidDataException(allowImages ? "可附加或预览 UTF-8 文本与常见图片格式。" : "只能读取 UTF-8 文本文件。"); }
        if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n')))
            throw new InvalidDataException("含二进制或控制数据；只能读取文本文件。");
        var settings = _policy.Store.Current;
        if (settings.SensitiveDataProtectionEnabled && SecurityContentScanner.ContainsSecret(text, settings))
            throw new UnauthorizedAccessException("安全中心检测到敏感内容；文件未加入上下文。");
        return (new(Relative(full), text, SHA256.HashData(bytes)), bytes, null);
    }

    public async Task<FuiPrompt> PrepareAsync(string prompt, IReadOnlyList<FileAttachment> attached, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (attached.Count > MaxAttachments) throw new InvalidOperationException("上下文中的文件过多。");
        var body = new StringBuilder(prompt);
        var images = new List<ChatImage>();
        var remaining = MaxAttachmentBytes;
        foreach (var file in attached)
        {
            FileAttachment current;
            byte[] bytes;
            DecodedImage? decoded = null;
            if (file.IsClipboardAttachment)
            {
                if (file.MimeType is not "image/png" || file.EncodedBytes is not { Length: > 0 } payload)
                    throw new InvalidDataException("剪贴板附件数据无效；请重新粘贴。");
                if (payload.Length > remaining)
                    throw new IOException($"每条消息的附件总量最多 {MaxAttachmentBytes / 1024 / 1024} MiB。");
                if (!SHA256.HashData(payload).AsSpan().SequenceEqual(file.Digest))
                    throw new IOException($"{file.Path} 已更改；请先移除，再重新附加。");
                if (!ProjectImage.TryInspect(payload, "image/png", out var width, out var height, out var label,
                        out decoded))
                    throw new InvalidDataException("剪贴板附件不是有效的 PNG；请重新粘贴。");
                bytes = payload.ToArray();
                current = file with
                {
                    Digest = SHA256.HashData(bytes),
                    ImageBytes = bytes,
                    Payload = bytes,
                    ImageWidth = width,
                    ImageHeight = height,
                    FormatLabel = label,
                    Preview = ProjectImage.CreatePreview(bytes, "image/png"),
                    Source = FileAttachmentSource.Clipboard,
                    IsClipboard = true
                };
            }
            else
            {
                (current, bytes, decoded) = await ReadFileAsync(file.Path, allowImages: true, remaining, ct);
            }
            if (!current.Digest.AsSpan().SequenceEqual(file.Digest) || current.MimeType != file.MimeType)
                throw new IOException($"{file.Path} 已更改；请先移除，再重新附加。");
            if (current.MimeType is { } mime)
            {
                var sourceBytes = current.ImageBytes ?? bytes;
                var wireBytes = sourceBytes;
                var wireMime = mime;
                if (!ProjectImage.IsNativeModelMime(mime))
                {
                    decoded ??= ImageDecoder.TryDecode(wireBytes, mime)
                        ?? throw new InvalidDataException($"{file.Path} 无法解码为模型图片。");
                    wireBytes = ImageDecoder.EncodePng(decoded)
                        ?? throw new InvalidDataException($"{file.Path} 无法转换为 PNG 图片。");
                    if (wireBytes.Length > remaining)
                        throw new IOException($"每条消息的附件总量最多 {MaxAttachmentBytes / 1024 / 1024} MiB。");
                    wireMime = "image/png";
                }
                images.Add(new(Convert.ToBase64String(wireBytes), wireMime));
                remaining -= Math.Max(sourceBytes.Length, wireBytes.Length);
            }
            else
            {
                body.Append("\n\n[项目文件：").Append(current.Path).Append("]\n").Append(current.Text).Append("\n[文件结束]");
                remaining -= bytes.Length;
            }
        }
        ct.ThrowIfCancellationRequested();
        return new(body.ToString(), images);
    }

    public async Task<string> DiffAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path, allowDeleted: true);
        var result = await DiffQuery.PreviewFileAsync(_root, Relative(full), maximumLines: 1000, cancellationToken: ct);
        if (result.Error == "git-filtered-file") throw new IOException("此路径使用 Git 过滤器，无法后台预览差异。");
        if (result.Error is { } error) throw new IOException($"无法读取 Git 差异：{error}");
        var text = new StringBuilder(result.Path);
        if (result.IsBinary == true) text.Append(" · 二进制文件");
        if (result.OldMode != result.NewMode) text.Append($"\n模式 {result.OldMode} → {result.NewMode}");
        foreach (var h in result.Hunks)
        {
            text.Append('\n').Append(h.Header);
            foreach (var line in h.Lines) text.Append('\n').Append(line.Kind).Append(line.Text);
        }
        if (result.Truncated) text.Append("\n… 差异已截断");
        else if (result.Hunks.Count == 0 && result.IsBinary != true && result.OldMode == result.NewMode) text.Append(" · 没有文本差异");
        var settings = _policy.Store.Current;
        return settings.SensitiveDataProtectionEnabled ? SecurityContentScanner.Redact(text.ToString(), settings) : text.ToString();
    }

    private string Resolve(string path, bool directory = false, bool allowDeleted = false)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Any(char.IsControl) || path.Contains(':'))
            throw new ArgumentException("请输入项目内的相对路径。");
        var full = Path.GetFullPath(path, _root);
        var rel = Path.GetRelativePath(_root, full);
        if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("路径不在项目内。");
        var parts = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = _root;
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part.Equals(".git", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("不能读取 Git 内部目录。");
            current = Path.Combine(current, part);
            try { if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException("不能读取链接或联接路径。"); }
            catch (FileNotFoundException) when (allowDeleted) { }
            catch (DirectoryNotFoundException) when (allowDeleted) { }
        }
        if (_policy.FileAction(full) != SecurityAction.Allow) throw new UnauthorizedAccessException("安全中心未允许直接读取此路径。");
        if (!directory && Directory.Exists(full)) throw new ArgumentException("请选择一个文件，而不是目录。");
        if (directory ? !Directory.Exists(full) : !allowDeleted && !File.Exists(full)) throw new FileNotFoundException("项目内未找到该路径。", path);
        return full;
    }
}
