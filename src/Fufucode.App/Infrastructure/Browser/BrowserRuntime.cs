using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Fufucode.App.Infrastructure.Browser;

/// <summary>一次 WebView2 子进程采样，全部来自真实进程。</summary>
public sealed record BrowserProcessIdentity(int ProcessId, CoreWebView2ProcessKind Kind);

public sealed record BrowserProcessSnapshot(
    long TotalPrivateBytes,
    long BrowserBytes,
    long RendererBytes,
    long GpuBytes,
    int ProcessCount,
    IReadOnlyList<int> ProcessIds,
    bool IsExact = true)
{
    public static BrowserProcessSnapshot Empty { get; } =
        new(0, 0, 0, 0, 0, Array.Empty<int>(), false);

    public bool IsEmpty => ProcessCount == 0;
}

/// <summary>
/// WebView2 运行时门面：唯一 env 复用（委托给 BrowserEnvironmentProvider）+ 进程预算采样。
/// GetProcessInfos 是 COM 调用，必须在创建 environment 的 UI 线程调用；进程内存读取可以在后台线程。
/// </summary>
public static class BrowserRuntime
{
    /// <summary>null=尚未确认；true=运行时支持 per-environment 进程列表；false=只支持降级采样。</summary>
    private static bool? _environment8Supported;

    public static Task<CoreWebView2Environment> GetEnvironmentAsync(string userDataFolder) =>
        BrowserEnvironmentProvider.GetAsync(userDataFolder);

    /// <summary>在 UI 线程把 COM 进程信息复制成普通快照；运行时过旧不支持 Environment8 时返回空。</summary>
    public static IReadOnlyList<BrowserProcessIdentity> GetProcessInfos(string userDataFolder)
    {
        if (!BrowserEnvironmentProvider.TryGetCompleted(userDataFolder, out var environment))
            return Array.Empty<BrowserProcessIdentity>();
        try
        {
            var infos = environment.GetProcessInfos();
            _environment8Supported = true;
            var result = new List<BrowserProcessIdentity>(infos.Count);
            foreach (var info in infos)
                result.Add(new BrowserProcessIdentity(info.ProcessId, info.Kind));
            return result;
        }
        catch (Exception error) when (error is NotImplementedException or InvalidCastException
            or InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            if (error is NotImplementedException or InvalidCastException) _environment8Supported = false;
            return Array.Empty<BrowserProcessIdentity>();
        }
    }

    /// <summary>读取每个 WebView2 进程的 PrivateMemorySize64；只使用普通快照，可放到后台线程。</summary>
    public static BrowserProcessSnapshot SampleProcesses(IReadOnlyList<BrowserProcessIdentity> infos)
    {
        if (infos.Count == 0) return BrowserProcessSnapshot.Empty;
        long browser = 0, renderer = 0, gpu = 0, total = 0;
        var ids = new List<int>();
        foreach (var info in infos)
        {
            try
            {
                using var process = Process.GetProcessById(info.ProcessId);
                var bytes = Math.Max(0, process.PrivateMemorySize64);
                total += bytes;
                switch (info.Kind)
                {
                    case CoreWebView2ProcessKind.Browser: browser += bytes; break;
                    case CoreWebView2ProcessKind.Renderer: renderer += bytes; break;
                    case CoreWebView2ProcessKind.Gpu: gpu += bytes; break;
                }
                ids.Add(info.ProcessId);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 进程在采样期间退出：跳过，不伪造数据。
            }
        }
        return new BrowserProcessSnapshot(total, browser, renderer, gpu, ids.Count, ids, true);
    }

    /// <summary>
    /// 运行时过旧、拿不到 per-environment 进程列表时的降级采样：按进程名统计。
    /// 可能包含同机其它 WebView2 应用，因此 IsExact=false，只用于预算兜底。
    /// </summary>
    public static BrowserProcessSnapshot SampleFallback()
    {
        try
        {
            long total = 0;
            var ids = new List<int>();
            foreach (var process in Process.GetProcessesByName("msedgewebview2"))
            {
                try
                {
                    total += Math.Max(0, process.PrivateMemorySize64);
                    ids.Add(process.Id);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { process.Dispose(); }
            }
            return new BrowserProcessSnapshot(total, 0, 0, 0, ids.Count, ids, false);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return BrowserProcessSnapshot.Empty;
        }
    }

    /// <summary>环境已拆除/列表不可用时的空快照；不把同机其它 WebView2 应用算入预算。</summary>
    public static BrowserProcessSnapshot SnapshotAfterEnvironmentTeardown() => BrowserProcessSnapshot.Empty;

    /// <summary>便捷采样（UI 线程调用）。运行时支持时返回精确分类，否则返回降级采样。</summary>
    public static BrowserProcessSnapshot Sample(string userDataFolder)
    {
        var infos = GetProcessInfos(userDataFolder);
        if (infos.Count > 0) return SampleProcesses(infos);
        // 已确认支持 Environment8 却拿不到列表，通常意味着 controller 全关、环境正在拆除；
        // 此时不能退回“按进程名统计”，否则会把同机其它 WebView2 应用算进预算。
        if (_environment8Supported == true) return BrowserProcessSnapshot.Empty;
        return SampleFallback();
    }
}
