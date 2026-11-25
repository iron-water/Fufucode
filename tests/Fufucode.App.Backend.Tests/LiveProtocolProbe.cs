using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// 真实端点探针（花真实额度，默认不跑）：
///   dotnet run --project tests/Fufucode.App.Backend.Tests -- --live-protocol &lt;api&gt; &lt;baseUrl&gt; &lt;model&gt; [apiKey]
/// 让 FUMO 引擎经指定协议读一个文件并原样回答，检查流式增量、工具回合与 ProviderState 回传。
/// </summary>
internal static class LiveProtocolProbe
{
    public static async Task<int> RunAsync(string api, string baseUrl, string model, string? apiKey)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "fufucode-live-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            const string marker = "LIVE-PROTOCOL-5521";
            await File.WriteAllTextAsync(Path.Combine(workspace, "marker.txt"), marker);
            var client = ModelApi.CreateClient(api, baseUrl, apiKey, model);
            var engine = new PureCSharpAgentEngine(new ProductionToolDispatcher(workspace), client, maxIterations: 4);
            var text = new StringBuilder();
            var thinking = new StringBuilder();
            var callbacks = new NativeAgentCallbacks
            {
                Stream = new ModelStreamCallbacks
                {
                    OnTextDelta = delta => text.Append(delta),
                    OnThinkingDelta = delta => thinking.Append(delta),
                },
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await engine.RunObservedTurnAsync(new PureModelRequest(
                [new PureAgentMessage(PureAgentRole.User,
                    "Call read_file with path \"marker.txt\", then reply with exactly the file content and nothing else.")],
                ["read_file"], model, ReasoningEffort: Environment.GetEnvironmentVariable("FUFUCODE_LIVE_EFFORT") ?? "low"), callbacks, timeout.Token);
            var history = engine.GetState().History ?? [];
            var toolTurn = history.FirstOrDefault(m => m.Role == PureAgentRole.Assistant && m.ToolCalls is { Count: > 0 });
            Console.WriteLine($"[{api}] {model} status={result.Status} {watch.ElapsedMilliseconds}ms usage=in:{result.Usage?.InputTokens} out:{result.Usage?.OutputTokens} cached:{result.Usage?.CacheReadTokens}");
            Console.WriteLine($"[{api}] reply={result.Response.Text?.Trim()}");
            Console.WriteLine($"[{api}] streamedText={text.Length} chars, thinking={thinking.Length} chars, toolCall={toolTurn?.ToolCalls?[0].Name}, providerState={(toolTurn?.ProviderState is null ? "none" : toolTurn.ProviderState.Length + " chars")}");
            var ok = result.Response.Text?.Contains(marker) == true && toolTurn is not null && text.Length > 0;
            Console.WriteLine((ok ? "PASS" : "FAIL") + $" [{api}] 真实端点完成工具回合并返回文件内容");
            return ok ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.WriteLine($"FAIL [{api}] {error.GetType().Name}: {error.Message}");
            return 1;
        }
        finally { try { Directory.Delete(workspace, true); } catch (IOException) { } }
    }
}
