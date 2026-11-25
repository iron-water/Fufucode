using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Fufucode.Fui;

internal interface ITextClipboard
{
    void Copy(string text);
}

// Windows owns the clipboard memory after SetClipboardData succeeds. Keeping this tiny native path
// avoids pulling WinForms into the console executable just for one text operation.
internal sealed class WindowsTextClipboard : ITextClipboard
{
    internal static WindowsTextClipboard Instance { get; } = new();

    public void Copy(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("FUI 文本剪贴板目前仅支持 Windows。");

        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        var memory = GlobalAlloc(GMemMoveable, (nuint)bytes.Length);
        if (memory == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法为剪贴板分配内存。");

        var owned = false;
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            OpenWithRetry();
            try
            {
                if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法清空剪贴板。");
                if (SetClipboardData(CfUnicodeText, memory) == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入剪贴板。");
                owned = true;
            }
            finally { CloseClipboard(); }
        }
        finally
        {
            if (!owned) GlobalFree(memory);
        }
    }

    private static void OpenWithRetry()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (OpenClipboard(0)) return;
            Thread.Sleep(15);
        }
        throw new Win32Exception(Marshal.GetLastWin32Error(), "剪贴板正被其他程序占用。");
    }

    private const uint CfUnicodeText = 13;
    private const uint GMemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);
}
