using System.Security.Cryptography;

namespace Fufucode.Fui;

// Reads an in-memory image from the Windows clipboard (PNG, CF_DIBV5 or CF_DIB) and normalizes it
// to a bounded PNG FileAttachment so users can paste screenshots directly into FUI input.
internal static class ClipboardImage
{
    private static int _sequence;

    internal static Func<FileAttachment?>? OverrideForTests { get; set; }

    internal static FileAttachment ReadAttachment()
    {
        if (OverrideForTests is { } hook)
            return hook() ?? throw new InvalidOperationException("剪贴板中没有可用图片。");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("当前平台不支持直接粘贴剪贴板图片，请用 /attach 指定项目内图片。");
        var bytes = ProjectImage.ReadWindowsClipboardPng(ProjectFiles.MaxAttachmentBytes)
            ?? throw new InvalidOperationException("剪贴板中没有可用图片。");
        return BuildFromPng(bytes) ?? throw new InvalidDataException("剪贴板图片不是有效的 PNG。");
    }

    internal static FileAttachment? TryBuildFromPng(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > ProjectFiles.MaxAttachmentBytes) return null;
        if (!ProjectImage.TryInspect(bytes, "image/png", out var width, out var height, out var label, out _))
            return null;
        return new(NextName(), "", SHA256.HashData(bytes), "image/png", bytes, width, height, label,
            IsClipboard: true, Preview: ProjectImage.CreatePreview(bytes, "image/png"),
            Payload: bytes, Source: FileAttachmentSource.Clipboard);
    }

    internal static FileAttachment? TryBuildFromDib(ReadOnlySpan<byte> dib)
    {
        if (dib.IsEmpty || dib.Length > ProjectFiles.MaxAttachmentBytes) return null;
        try
        {
            var png = ProjectImage.DibToPng(dib, ProjectFiles.MaxAttachmentBytes);
            return TryBuildFromPng(png);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private static FileAttachment? BuildFromPng(byte[] bytes) =>
        TryBuildFromPng(bytes) is { } file ? file with { Path = NextName() } : null;

    private static string NextName() =>
        $"clipboard-{DateTime.Now:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _sequence)}.png";
}
