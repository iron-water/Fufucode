using System;
using System.IO;
using System.Threading.Tasks;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// 可选的真实中继联调探针（不进默认套件，需要本机部署 Seep 工作台与 Python）：
///   dotnet run --project tests/Fufucode.App.Backend.Tests -- --seep-relay-live [labRoot]
///
/// 把工作台自带的 compliance-relay 目录<b>原样复制</b>到临时目录（不改动工作台本体、
/// 也不在只读的工作台目录里生成 relay-config.json），然后用
/// <see cref="ReverseLabRelayHost"/> 真实启动 relay.py 子进程，验证：
/// 真的进入监听 → 枚举/策略可用 → 停止后本插件启动的进程被回收。
/// </summary>
internal static class SeepRealRelayProbe
{
    public static async Task<int> RunAsync(string? labRoot)
    {
        var probe = SeepReverseLabAssets.Probe(labRoot);
        Console.WriteLine($"[relay-live] labRoot={probe.LabRoot} relayScript={probe.RelayScriptExists}");
        if (!probe.RelayAvailable)
        {
            Console.WriteLine("[relay-live] SKIP 未部署或缺少 " + probe.RelayScriptPath);
            return 2;
        }

        var root = Path.Combine(Path.GetTempPath(), "seep-relay-probe-" + Guid.NewGuid().ToString("N"));
        var failures = 0;
        void Check(bool condition, string name)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
            if (!condition) failures++;
        }

        try
        {
            var sandboxLab = CopyRelayToSandbox(probe.LabRoot, root);
            var port = SeepFixtures.FreePort();
            var host = new ReverseLabRelayHost(port, sandboxLab);
            try
            {
                var result = await host.StartAsync();
                Console.WriteLine($"[relay-live] state={result.State} reason={result.Reason}");
                Check(result.State == RelayHostState.Running, "真实启动 relay.py 并进入监听");
                Check(host.IsListening(), $"127.0.0.1:{port} 真实可连接");
                Check(host.OwnsProcess, "本插件持有自己启动的中继进程");

                await host.StopAsync();
                await Task.Delay(300);
                Check(!host.OwnsProcess, "停止后本插件不再持有中继进程");
                Check(!SeepFixtures.IsPortOpen(port), $"停止后 127.0.0.1:{port} 已释放");
            }
            finally
            {
                await host.DisposeAsync();
            }

            Check(!SeepFixtures.IsPortOpen(port), "收尾后端口保持释放（不留常驻中继）");
            Console.WriteLine(failures == 0 ? "[relay-live] ALL PASS" : $"[relay-live] FAILURES={failures}");
            return failures == 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("[relay-live] 异常：" + error);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>只读复制工作台中继目录到临时沙箱，保留相对路径布局（不修改源文件）。</summary>
    private static string CopyRelayToSandbox(string sourceLabRoot, string destinationRoot)
    {
        var sourceRelay = Path.GetDirectoryName(Path.Combine(sourceLabRoot, SeepReverseLabAssets.RelayScriptRelativePath))!;
        var sandboxLab = Path.Combine(destinationRoot, "Seep-Reverse-Lab");
        var destinationRelay = Path.Combine(sandboxLab, Path.GetDirectoryName(SeepReverseLabAssets.RelayScriptRelativePath)!);
        Directory.CreateDirectory(destinationRelay);
        foreach (var file in Directory.GetFiles(sourceRelay))
        {
            File.Copy(file, Path.Combine(destinationRelay, Path.GetFileName(file)), overwrite: false);
        }
        return sandboxLab;
    }
}
