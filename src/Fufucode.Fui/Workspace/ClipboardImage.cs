using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Fufucode.Fui;

// Reads an in-memory image from the Windows clipboard (PNG, CF_DIBV5 or CF_DIB) and normalizes it
// to a bounded PNG FileAttachment so users can paste screenshots directly into FUI input.
internal static class ClipboardImage
{
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;
    private static int _sequence;

    internal static Func<FileAttachment?>? OverrideForTests { get; set; }

    internal static FileAttachment ReadAttachment()
    {
        if (OverrideForTests is { } hook)
            return hook() ?? throw new InvalidOperationException("剪贴板中没有可用图片。");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("当前平台不支持直接粘贴剪贴板图片，请用 /attach 指定项目内图片。");

        if (!TryOpenClipboard())
            throw new IOException("无法打开系统剪贴板，请稍后重试。");
        try
        {
            var pngFormat = RegisterClipboardFormatW("PNG");
            if (pngFormat != 0 && TryReadClipboardBytes(pngFormat, out var pngBytes) &&
                TryBuildFromPng(pngBytes) is { } fromPng)
                return fromPng;

            if (TryReadClipboardBytes(CfDibV5, out var dibV5Bytes) &&
                TryBuildFromDib(dibV5Bytes) is { } fromDibV5)
                return fromDibV5;

            if (TryReadClipboardBytes(CfDib, out var dibBytes) &&
                TryBuildFromDib(dibBytes) is { } fromDib)
                return fromDib;
        }
        finally
        {
            CloseClipboard();
        }
        throw new InvalidOperationException("剪贴板中没有可用图片。");
    }

    internal static FileAttachment? TryBuildFromPng(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > ProjectFiles.MaxAttachmentBytes) return null;
        if (!ProjectImage.TryInspect(bytes, "image/png", out var w, out var h, out _, out _)) return null;
        var name = NextName();
        return new(name, "", SHA256.HashData(bytes), "image/png", bytes, w, h, "PNG", IsClipboard: true);
    }

    internal static FileAttachment? TryBuildFromDib(ReadOnlySpan<byte> dib)
    {
        if (dib.IsEmpty || dib.Length > ProjectFiles.MaxAttachmentBytes) return null;
        var bmp = ImageDecoder.WrapDibAsBmp(dib);
        if (bmp is null) return null;
        var decoded = ImageDecoder.TryDecode(bmp, "image/bmp");
        if (decoded is null) return null;
        var png = ImageDecoder.EncodePng(decoded);
        if (png is null || png.Length == 0 || png.Length > ProjectFiles.MaxAttachmentBytes) return null;
        var name = NextName();
        return new(name, "", SHA256.HashData(png), "image/png", png, decoded.Width, decoded.Height, "PNG", IsClipboard: true);
    }

    private static string NextName() => $"clipboard-{Interlocked.Increment(ref _sequence)}.png";

    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (OpenClipboard(nint.Zero)) return true;
            Thread.Sleep(15);
        }
        return false;
    }

    private static bool TryReadClipboardBytes(uint format, out byte[] bytes)
    {
        bytes = [];
        var handle = GetClipboardData(format);
        if (handle == nint.Zero) return false;
        var size = GlobalSize(handle);
        if (size == 0 || size > (nuint)ProjectFiles.MaxAttachmentBytes) return false;
        var ptr = GlobalLock(handle);
        if (ptr == nint.Zero) return false;
        try
        {
            bytes = new byte[(int)size];
            Marshal.Copy(ptr, bytes, 0, bytes.Length);
            return true;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(nint hMem);
}
