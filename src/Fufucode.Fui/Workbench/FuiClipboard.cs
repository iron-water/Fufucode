using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Fufucode.Fui;

internal sealed record FuiClipboardResult(bool Success, int Characters, int Bytes, string? Error)
{
    internal static FuiClipboardResult Failed(string error, string text) =>
        new(false, text.EnumerateRunes().Count(), Encoding.UTF8.GetByteCount(text), error);
}

internal interface IFuiClipboard
{
    FuiClipboardResult CopyText(string text);
}

/// <summary>
/// Writes CF_UNICODETEXT through the Windows clipboard API. OSC 52 is deliberately not emitted:
/// the terminal protocol has no reliable acknowledgement, so it cannot provide the success receipt
/// FUI promises here.
/// </summary>
internal sealed class WindowsTextClipboard : IFuiClipboard
{
    internal static WindowsTextClipboard Instance { get; } = new();
    private const uint CfUnicodeText = 13;
    private const uint GMemMoveable = 0x0002;

    public FuiClipboardResult CopyText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!OperatingSystem.IsWindows())
            return FuiClipboardResult.Failed("当前平台不支持 Windows 文本剪贴板。", text);
        if (text.IndexOf('\0') >= 0)
            return FuiClipboardResult.Failed("正文包含 NUL，Windows 文本剪贴板拒绝写入。", text);

        nint memory = 0;
        var opened = false;
        try
        {
            for (var attempt = 0; attempt < 6 && !opened; attempt++)
            {
                opened = OpenClipboard(0);
                if (!opened) Thread.Sleep(15);
            }
            if (!opened) throw new Win32Exception(Marshal.GetLastWin32Error(), "系统剪贴板正被其他程序占用");
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法清空系统剪贴板");

            var bytes = checked((text.Length + 1) * sizeof(char));
            memory = GlobalAlloc(GMemMoveable, (nuint)bytes);
            if (memory == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法分配剪贴板内存");
            var target = GlobalLock(memory);
            if (target == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法锁定剪贴板内存");
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
            }
            finally { GlobalUnlock(memory); }

            if (SetClipboardData(CfUnicodeText, memory) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入系统剪贴板");
            memory = 0; // The clipboard now owns the global memory.
            return new(true, text.EnumerateRunes().Count(), Encoding.UTF8.GetByteCount(text), null);
        }
        catch (Exception error) when (error is Win32Exception or OverflowException)
        {
            return FuiClipboardResult.Failed(error.Message, text);
        }
        finally
        {
            if (memory != 0) GlobalFree(memory);
            if (opened) CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);
}
