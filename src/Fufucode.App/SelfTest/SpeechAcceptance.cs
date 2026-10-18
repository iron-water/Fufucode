using System;
using System.Linq;
using System.Threading.Tasks;
using Fufucode.App.Infrastructure;
using Fufucode.Core.Plugins;

namespace Fufucode.App.SelfTest;

internal static class SpeechAcceptance
{
    internal static async Task<int> RunAsync()
    {
        using ISpeechService service = NullSpeechService.Instance;
        try
        {
            async Task Wait(string status)
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
                while (service.Status != status && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
                if (service.Status != status) throw new InvalidOperationException("Expected " + status + ", actual " + service.Status);
            }
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
            service.Rate = 10;
            await service.SpeakAsync("Completed.").WaitAsync(TimeSpan.FromSeconds(8));
            if (service.IsSpeaking || service.Status != "朗读完成") throw new InvalidOperationException("Completion was not reported.");
            Console.WriteLine("PASS SAPI natural completion and speed setting");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL SAPI " + error.Message); return 1; }
    }
}
