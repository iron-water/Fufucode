using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using Avalonia;

namespace Fufucode.App.Infrastructure.Browser;

/// <summary>A clipped, initially hidden Win32 child. The WebView is never a sibling of the Avalonia workbench.</summary>
internal sealed class BrowserNativeViewport : IDisposable
{
    private const int GwlExStyle = -20, NoRedirectionBitmap = 0x00200000, Layered = 0x00080000;
    private const uint Child = 0x40000000, ClipChildren = 0x02000000, ClipSiblings = 0x04000000;
    private Rectangle? _bounds;
    private bool _visible;

    public IntPtr Handle { get; private set; }
    public IntPtr Parent => GetParent(Handle);

    public BrowserNativeViewport(IntPtr parent)
    {
        // Match Avalonia Win32NativeControlHost: a no-redirection top-level needs a redirected
        // (fully opaque layered) child for HWND content to participate in desktop composition.
        var layered = (GetWindowLongW(parent, GwlExStyle) & NoRedirectionBitmap) != 0;
        Handle = CreateWindowExW(layered ? Layered : 0, "STATIC", "", Child | ClipChildren | ClipSiblings,
            0, 0, 0, 0, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (layered && !SetLayeredWindowAttributes(Handle, 0, 255, 2 /* LWA_ALPHA */))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            Dispose();
            throw error;
        }
    }

    public void Reparent(IntPtr parent)
    {
        SetVisible(false);
        if (SetParent(Handle, parent) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        _bounds = null;
    }

    public bool Place(Rectangle bounds)
    {
        if (_bounds == bounds) return false;
        if (!SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                0x0014 /* SWP_NOACTIVATE | SWP_NOZORDER */)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _bounds = bounds;
        return true;
    }

    public void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        ShowWindow(Handle, visible ? 8 /* SW_SHOWNA */ : 0 /* SW_HIDE */);
        _visible = visible;
    }

    // Clip the parent, not the document: clipping/scrolling a dock must not reflow its web page.
    public static (Rectangle Site, Rectangle Document)? PlaceIn(Rect page, Rect viewport, Avalonia.Size client, double scale)
    {
        var visible = page.Intersect(viewport.Translate(new Vector(page.X, page.Y))).Intersect(new Rect(client));
        if (visible.Width <= 0 || visible.Height <= 0) return null;
        int Px(double value) => (int)Math.Round(value * scale);
        var site = Rectangle.FromLTRB(Px(visible.X), Px(visible.Y), Px(visible.Right), Px(visible.Bottom));
        if (site.Width <= 0 || site.Height <= 0) return null;
        return (site, Rectangle.FromLTRB(Px(page.X) - site.X, Px(page.Y) - site.Y, Px(page.Right) - site.X, Px(page.Bottom) - site.Y));
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyWindow(Handle);
        Handle = IntPtr.Zero;
    }

    [DllImport("user32", ExactSpelling = true)] private static extern int GetWindowLongW(IntPtr hwnd, int index);
    [DllImport("user32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern IntPtr CreateWindowExW(int exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32", SetLastError = true)] private static extern IntPtr SetParent(IntPtr hwnd, IntPtr parent);
    [DllImport("user32", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr hwnd);
}
