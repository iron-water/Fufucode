using System;
using System.Collections.Generic;
using System.IO;

namespace Fufucode.App.Integration;

/// <summary>
/// 第二个进程 → 已运行实例的激活负载投递箱。
///
/// 系统通知按钮以协议激活方式启动本程序；单实例守卫只会唤醒已有窗口，不会携带参数。
/// 因此第二个进程先把链接落到用户级目录，再发激活信号；首个实例收到信号后取走并处理。
/// 目录固定在 %LOCALAPPDATA%\Fufucode\activation：协议激活没有 --data-root，
/// 只有固定位置才能让任意构建的实例都取到同一份负载。
/// </summary>
internal static class ActivationInbox
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fufucode", "activation");

    internal static void Enqueue(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return;
        try
        {
            Directory.CreateDirectory(Root);
            PruneExpired();
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, payload);
        }
        catch (Exception)
        {
            // 投递失败不阻止第二个进程退出；首个实例仍会被唤醒，只是拿不到具体动作。
        }
    }

    /// <summary>取走并删除全部待处理负载；读取失败的条目保持原样，下次再试。</summary>
    internal static IReadOnlyList<string> Drain()
    {
        var drained = new List<string>();
        try
        {
            if (!Directory.Exists(Root)) return drained;
            foreach (var path in Directory.EnumerateFiles(Root, "*.txt"))
            {
                try
                {
                    var payload = File.ReadAllText(path);
                    File.Delete(path);
                    if (!string.IsNullOrWhiteSpace(payload)) drained.Add(payload.Trim());
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception) { }
        return drained;
    }

    private static void PruneExpired()
    {
        var cutoff = DateTime.UtcNow - Lifetime;
        foreach (var path in Directory.EnumerateFiles(Root, "*.txt"))
        {
            try { if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
