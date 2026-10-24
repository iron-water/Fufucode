using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Win32;
using Live2D.Core;
using Microsoft.Web.WebView2.Core;
using System.Runtime.InteropServices;

namespace ProbeWebView2;

/// <summary>
/// WebView2 通过子 HWND 嵌入 Avalonia：NativeControlHost 创建静态子窗口，
/// CoreWebView2Controller 绑到该 HWND；DefaultBackgroundColor=透明验证与 Avalonia 透明窗合成。
/// </summary>
public sealed class WebViewHost : NativeControlHost
{
    private IntPtr _hwnd;
    private CoreWebView2Controller? _controller;
    private CoreWebView2Environment? _env;
    public CoreWebView2? Core => _controller?.CoreWebView2;
    public event Action<CoreWebView2>? Ready;
    public bool WebViewReady => _controller != null;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern ushort RegisterClassW(ref WNDCLASSW wc);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(IntPtr name);
    private delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);
    private static WndProcDelegate? _wndProc;   // 保活防 GC
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WNDCLASSW { public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
    public IntPtr ChildHwnd => _hwnd;

    // 宿主子窗口：自注册类、不响应 WM_ERASEBKGND —— 避免在 WV2 透明区刷出灰底
    private static IntPtr HostWndProc(IntPtr h, uint m, IntPtr w, IntPtr l) => m == 0x14 /*WM_ERASEBKGND*/ ? new IntPtr(1) : DefWindowProc(h, m, w, l);
    private static void EnsureClass()
    {
        if (_wndProc != null) return;
        _wndProc = HostWndProc;
        var wc = new WNDCLASSW { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc), hInstance = GetModuleHandleW(IntPtr.Zero), lpszClassName = "FufuWv2Host" };
        if (RegisterClassW(ref wc) == 0) throw new InvalidOperationException("RegisterClassW failed " + Marshal.GetLastWin32Error());
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        EnsureClass();
        _hwnd = CreateWindowEx(0, "FufuWv2Host", "", WS_CHILD | WS_VISIBLE, 0, 0, 10, 10,
            parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _ = InitAsync();
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try { _controller?.Close(); } catch { }
        _controller = null;
        DestroyWindow(control.Handle);
        _hwnd = IntPtr.Zero;
    }

    private async Task InitAsync()
    {
        try
        {
            var webroot = EnsureWebroot();
            // 优先 Evergreen 运行时；未装时回退到 FUFU_WV2_BROWSER 或已装 Edge 浏览器目录
            var browser = Environment.GetEnvironmentVariable("FUFU_WV2_BROWSER");
            if (string.IsNullOrEmpty(browser))
            {
                try { _env = await CoreWebView2Environment.CreateAsync(); }
                catch { browser = FindEdgeBrowser(); }
            }
            if (browser != null)
                _env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: browser);
            // 普通子窗口控制器：WebView2 区域按不透明 surface 合成；
            // 逐像素透明需要 CompositionController+DirectComposition（见 REPORT.md 风险记录）
            _controller = await _env.CreateCoreWebView2ControllerAsync(_hwnd);
            _controller.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            FitBounds();
            var core = _controller.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("app.probe", webroot, CoreWebView2HostResourceAccessKind.Allow);
            core.Navigate("https://app.probe/webroot/index.html");
            Ready?.Invoke(core);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.Message);
            Ready?.Invoke(null!);
        }
    }

    public event Action<string>? Failed;

    private static string? FindEdgeBrowser()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application");
        if (Directory.Exists(dir))
            foreach (var v in Directory.GetDirectories(dir))
                if (File.Exists(Path.Combine(v, "msedge.exe"))) return v;
        return null;
    }

    /// <summary>把 webroot 自写文件复制到 vendor 根目录下的 webroot/，再虚拟映射整个根目录。</summary>
    private static string EnsureWebroot()
    {
        var root = VendorPaths.Root;
        var src = Path.Combine(AppContext.BaseDirectory, "webroot");
        var dst = Path.Combine(root, "webroot");
        Directory.CreateDirectory(dst);
        if (Directory.Exists(src))
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        return root;
    }

    public void FitBounds()
    {
        if (_controller == null || _hwnd == IntPtr.Zero) return;
        GetClientRect(_hwnd, out var r);
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, r.Right - r.Left, r.Bottom - r.Top);
    }
}
