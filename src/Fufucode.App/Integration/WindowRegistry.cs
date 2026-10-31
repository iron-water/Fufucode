using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.App.Integration;

/// <summary>
/// 同进程多窗口的会话所有权登记。
/// 一个会话只能有一个宿主（单写者）；同一会话的其它窗口作为“同步视图”借用该宿主，
/// 因此所有窗口看到同一份时间线与事件，不需要跨进程锁或轮询。
/// </summary>
internal static class SessionWindowRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (WeakReference<RealSessionHost> Host, WeakReference<MainWindow> Owner)> Owners =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<WeakReference<MainWindow>>> Followers =
        new(StringComparer.Ordinal);

    public static void RegisterOwner(string sessionId, RealSessionHost host, MainWindow owner)
    {
        lock (Gate)
        {
            Owners[sessionId] = (new WeakReference<RealSessionHost>(host), new WeakReference<MainWindow>(owner));
        }
    }

    public static bool TryGetOwner(string sessionId, out RealSessionHost? host, out MainWindow? owner)
    {
        lock (Gate)
        {
            host = null;
            owner = null;
            if (!Owners.TryGetValue(sessionId, out var entry)) return false;
            if (!entry.Host.TryGetTarget(out var target) || target is null) { Owners.Remove(sessionId); return false; }
            entry.Owner.TryGetTarget(out owner);
            host = target;
            return true;
        }
    }

    /// <summary>把窗口登记为某个会话的同步视图；宿主释放时会先关闭它们。</summary>
    public static void AddFollower(string sessionId, MainWindow window)
    {
        lock (Gate)
        {
            if (!Followers.TryGetValue(sessionId, out var list))
                Followers[sessionId] = list = new List<WeakReference<MainWindow>>();
            list.RemoveAll(reference => !reference.TryGetTarget(out var target) || target is null);
            if (!list.Any(reference => reference.TryGetTarget(out var target) && ReferenceEquals(target, window)))
                list.Add(new WeakReference<MainWindow>(window));
        }
    }

    /// <summary>仅当登记的所有者仍是该窗口时才移除，避免新窗口覆盖后误删。</summary>
    public static void TryUnregisterOwner(string sessionId, MainWindow window)
    {
        lock (Gate)
        {
            if (!Owners.TryGetValue(sessionId, out var entry)) return;
            entry.Owner.TryGetTarget(out var owner);
            if (owner is null || ReferenceEquals(owner, window)) Owners.Remove(sessionId);
        }
    }

    public static void RemoveFollower(string sessionId, MainWindow window)
    {
        lock (Gate)
        {
            if (Followers.TryGetValue(sessionId, out var list))
                list.RemoveAll(reference => !reference.TryGetTarget(out var target) || target is null || ReferenceEquals(target, window));
        }
    }

    /// <summary>宿主即将切换/释放会话：先关闭所有同步视图，避免它们引用已释放的会话对象。</summary>
    public static void CloseFollowers(string sessionId)
    {
        List<MainWindow> closing;
        lock (Gate)
        {
            if (!Followers.TryGetValue(sessionId, out var list)) return;
            closing = list.Select(reference => reference.TryGetTarget(out var target) ? target : null)
                .OfType<MainWindow>().Where(window => !window.IsClosedForSharing).ToList();
            Followers.Remove(sessionId);
            Owners.Remove(sessionId);
        }
        foreach (var window in closing)
            Avalonia.Threading.Dispatcher.UIThread.Post(window.CloseAsSharedView);
    }

    /// <summary>查找已打开该会话的窗口（用于把重复打开聚焦到原窗口）。</summary>
    public static MainWindow? FindWindow(string sessionId)
    {
        lock (Gate)
        {
            if (Owners.TryGetValue(sessionId, out var entry) && entry.Owner.TryGetTarget(out var owner) && owner is not null)
                return owner;
            if (Followers.TryGetValue(sessionId, out var list))
            {
                foreach (var reference in list)
                    if (reference.TryGetTarget(out var follower) && follower is not null) return follower;
            }
            return null;
        }
    }
}

/// <summary>
/// 跨窗口会话元数据变更广播：任一同进程窗口修改会话（新建/重命名/归档/删除/置顶）后，
/// 其它窗口刷新列表与概览。事件只携带来源窗口，来源自己忽略，避免刷新循环。
/// </summary>
internal static class SessionChangeBus
{
    public static event Action<object?>? Changed;

    public static void Publish(object? source) => Changed?.Invoke(source);
}
