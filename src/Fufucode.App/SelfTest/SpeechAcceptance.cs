using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fufucode.Core;
using Fufucode.Core.Plugins;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 语音朗读验收探针（--speech-probe）：走插件真实载荷，而不是宿主内的替身。
/// 插件未部署时必须明确报告“未找到载荷”，不能用替身的空返回值伪造通过。
/// </summary>
internal static class SpeechAcceptance
{
    internal static async Task<int> RunAsync()
    {
        IPluginPackage? package = null;
        try
        {
            package = LoadPackage();
            if (package is null) throw new FileNotFoundException(
                "未找到 service.speech-synthesis 插件载荷；先执行 fucode/publish-local.ps1 或安装插件。");
            package.InitializeAsync(new PluginPackageContext("service.speech-synthesis", PayloadDirectory!, null))
                .GetAwaiter().GetResult();
            var service = PluginPackageManager.ActiveSpeechService
                ?? throw new InvalidOperationException("插件已初始化但没有登记朗读服务。");

            async Task Wait(string status)
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
                while (service.Status != status && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
                if (service.Status != status) throw new InvalidOperationException("Expected " + status + ", actual " + service.Status);
            }

            service.Volume = 0; // 静音验收：只验证进程与状态机，不声称音频听感
            var speech = service.SpeakAsync(string.Join(" ", Enumerable.Repeat("Windows speech acceptance pause resume test.", 30)));
            await Wait("正在朗读"); Console.WriteLine("PASS SAPI worker started with muted volume");
            await service.PauseAsync(); await Wait("已暂停");
            await Task.Delay(200);
            if (!service.IsPaused || !service.IsSpeaking) throw new InvalidOperationException("Paused speech was lost.");
            Console.WriteLine("PASS SAPI pause acknowledgement and paused state");
            await service.ResumeAsync(); await Wait("正在朗读"); Console.WriteLine("PASS SAPI resume acknowledgement");
            service.Stop(); await speech.WaitAsync(TimeSpan.FromSeconds(8));
            if (service.IsSpeaking || service.IsPaused || service.Status != "已停止") throw new InvalidOperationException("Stop did not release worker.");
            Console.WriteLine("PASS SAPI stop releases worker");
            service.Rate = 10; service.Volume = 0;
            await service.SpeakAsync("Completed.").WaitAsync(TimeSpan.FromSeconds(8));
            if (service.IsSpeaking || service.Status != "朗读完成") throw new InvalidOperationException("Completion was not reported.");
            Console.WriteLine("PASS SAPI natural completion and speed setting");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL SAPI " + error.GetBaseException().Message); return 1; }
        finally
        {
            if (package is not null) { try { package.ShutdownAsync().GetAwaiter().GetResult(); } catch { } }
        }
    }

    private static string? PayloadDirectory;

    /// <summary>按 manifest 的 packageType 从真实载荷目录创建插件包；目录缺失返回 null。</summary>
    private static IPluginPackage? LoadPackage()
    {
        foreach (var directory in CandidateDirectories())
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = manifest.RootElement;
                if (!root.TryGetProperty("id", out var id) || id.GetString() != "service.speech-synthesis") continue;
                var assemblyPath = Path.Combine(directory, root.GetProperty("entryAssembly").GetString()!);
                if (!File.Exists(assemblyPath)) continue;
                var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
                var type = root.TryGetProperty("packageType", out var typeName)
                    ? assembly.GetType(typeName.GetString()!, throwOnError: false)
                    : null;
                if (type is null || Activator.CreateInstance(type) is not IPluginPackage package) continue;
                PayloadDirectory = directory;
                return package;
            }
            catch { }
        }
        return null;
    }

    private static string[] CandidateDirectories()
    {
        var layout = new AppDataLayout();
        return
        [
            layout.PluginDirectory("service.speech-synthesis"),
            Path.Combine(AppContext.BaseDirectory, "plugins", "service.speech-synthesis"),
            Path.Combine(AppContext.BaseDirectory, "plugins-dist", "service.speech-synthesis"),
            Path.Combine(AppContext.BaseDirectory, "distribution", "service.speech-synthesis"),
        ];
    }
}
