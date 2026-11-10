using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.App.Integration;

/// <summary>
/// 窗口发现索引，不拥有会话生命周期。每个窗口独立持有宿主引用；
/// 同一会话允许多个可编辑窗口，关闭/切换一个窗口不关闭其他窗口。
/// </summary>
internal static class SessionWindowRegistry
{
    private sealed record View(WeakReference<RealSessionHost> Host, WeakReference<MainWindow> Window);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<View>> Views = new(StringComparer.Ordinal);

    public static void RegisterOwner(string sessionId, RealSessionHost host, MainWindow owner)
    {
        lock (Gate)
        {
            if (!Views.TryGetValue(sessionId, out var list)) Views[sessionId] = list = [];
            list.RemoveAll(view => !view.Window.TryGetTarget(out var window) || window.IsClosedForSharing || ReferenceEquals(window, owner));
            list.Add(new(new(host), new(owner)));
        }
    }

    public static bool TryGetOwner(string sessionId, out RealSessionHost? host, out MainWindow? owner)
    {
        lock (Gate)
        {
            host = null;
            owner = null;
            if (!Views.TryGetValue(sessionId, out var list)) return false;
            foreach (var view in list.ToArray())
            {
                if (view.Window.TryGetTarget(out var window) && !window.IsClosedForSharing &&
                    view.Host.TryGetTarget(out var target) && target.HasLiveSession && target.Current.Session.Id == sessionId)
                {
                    host = target;
                    owner = window;
                    return true;
                }
                list.Remove(view);
            }
            if (list.Count == 0) Views.Remove(sessionId);
            return false;
        }
    }

    public static void TryUnregisterOwner(string sessionId, MainWindow window)
    {
        lock (Gate)
        {
            if (!Views.TryGetValue(sessionId, out var list)) return;
            list.RemoveAll(view => !view.Window.TryGetTarget(out var target) || target.IsClosedForSharing || ReferenceEquals(target, window));
            if (list.Count == 0) Views.Remove(sessionId);
        }
    }

    public static MainWindow? FindWindow(string sessionId) => TryGetOwner(sessionId, out _, out var window) ? window : null;
}

/// <summary>跨窗口元数据/状态变更广播；只携带来源窗口，不传递另一份会话事实。</summary>
internal static class SessionChangeBus
{
    public static event Action<object?>? Changed;
    public static void Publish(object? source) => Changed?.Invoke(source);
}
