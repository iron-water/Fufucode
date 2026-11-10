using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Fufucode.App.Integration;

/// <summary>进程拥有一个托盘；窗口各自拥有视图，关闭任一窗口时恢复入口交给其余窗口。</summary>
internal static class ApplicationWindows
{
    private sealed record Registration(MainWindow Window, Action Show, Action<NativeMenu> Fill);
    private static readonly List<Registration> Windows = new();
    private static Registration? _active;
    private static TrayIconService? _tray;
    internal static bool UseNativeTrayForTesting { get; set; }
    internal static bool TrayAvailable => _tray is not null;
    internal static MainWindow[] OpenWindows => Windows.Select(r => r.Window).ToArray();
    internal static MainWindow? Current => (_active ?? Windows.LastOrDefault())?.Window;

    internal static IDisposable Register(MainWindow window, Action show, Action<NativeMenu> fill)
    {
        var registration = new Registration(window, show, fill);
        Windows.Add(registration);
        _active = registration;
        void Activated(object? sender, EventArgs args) => _active = registration;
        window.Activated += Activated;
        if (_tray is null && (!Program.SelfTestMode || UseNativeTrayForTesting))
        {
            try { _tray = new TrayIconService(ShowCurrent, menu => (_active ?? Windows.LastOrDefault())?.Fill(menu)); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("托盘图标创建失败：{0}", error.Message); }
        }
        return new Lease(() =>
        {
            window.Activated -= Activated;
            Windows.Remove(registration);
            if (ReferenceEquals(_active, registration)) _active = Windows.LastOrDefault();
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                ReferenceEquals(desktop.MainWindow, window)) desktop.MainWindow = Current;
            if (Windows.Count != 0) return;
            _tray?.Dispose();
            _tray = null;
        });
    }

    internal static void ShowCurrent() => (_active ?? Windows.LastOrDefault())?.Show();
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() { var action = _release; _release = null; action?.Invoke(); }
    }
}
