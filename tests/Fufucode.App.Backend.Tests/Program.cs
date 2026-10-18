namespace Fufucode.App.Backend.Tests;

/// <summary>
/// P1-BACKEND 独立测试入口（不注册到共享 SelfTestRunner）：
///   dotnet run --project tests/Fufucode.App.Backend.Tests
/// 覆盖四后端引擎注册与解析、运行时探测、全局默认后端存取、
/// 会话后端徽标映射、平滑切换、纯 C# 内核契约完整性，
/// CS-2 原生 SSE 流式模型客户端与流式 Agent Loop 调度，
/// 以及 CS-3 会话层实时流式事件接线（NativeCSharpChatSession）。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // 可选的真实端点探针：不进默认套件（需要网络与凭据）。
        if (args.Length >= 2 && args[0] == "--live-sse")
        {
            var model = args.Length >= 3 ? args[2] : "devin/swe-2";
            var apiKey = args.Length >= 4 ? args[3] : null;
            return await LiveSseProbe.RunAsync(args[1], model, apiKey);
        }

        // 可选的真实端点 Agent Loop 探针：验证 FUMO 在本机网关上完成多轮工具循环。
        if (args.Length >= 3 && args[0] == "--live-agent-loop")
        {
            // 参数：--live-agent-loop <baseUrl> <model> [apiKey]
            var liveModel = args.Length >= 3 ? args[2] : "group/auto-glm-5-3-flash";
            var liveKey = args.Length >= 4 ? args[3] : null;
            return await LiveAgentLoopProbe.RunAsync(args[1], liveModel, liveKey);
        }

        // 可选的真实端点会话层探针：验证 NativeCSharpChatSession 的实时事件序列。
        if (args.Length >= 2 && args[0] == "--live-session")
        {
            var model = args.Length >= 3 ? args[2] : "devin/swe-2";
            var apiKey = args.Length >= 4 ? args[3] : null;
            return await NativeChatSessionStreamingTests.RunLiveAsync(args[1], model, apiKey);
        }

        // 可选的真实联调探针：真实启动 seep MCP 子进程（需本机部署工作台 + Python）。
        if (args.Length >= 1 && args[0] == "--seep-live")
        {
            return await SeepRealMcpProbe.RunAsync(args.Length >= 2 ? args[1] : null);
        }

        // 可选的真实联调探针：真实启动 compliance-relay 中继（需本机部署工作台 + Python）。
        if (args.Length >= 1 && args[0] == "--seep-relay-live")
        {
            return await SeepRealRelayProbe.RunAsync(args.Length >= 2 ? args[1] : null);
        }

        var checks = new Checks();
        try
        {
            if (args.Length == 1 && args[0] == "--session-only")
            {
                await NativeChatSessionStreamingTests.RunAsync(checks);
                Console.WriteLine($"\n会话层通过：{checks.Passed} 项断言");
                return 0;
            }
            BackendContractTests.Run(checks);
            DefaultBackendTests.Run(checks);
            SessionBadgeTests.Run(checks);
            SessionSwitchTests.Run(checks);
            await PureCSharpContractTests.RunAsync(checks);
            PluginMarketplaceTests.Run(checks);
            await PluginCapabilityTests.RunAsync(checks);
            await PluginDependencyTests.RunAsync(checks);
            await AppUpdateServiceTests.RunAsync(checks);
            await UpdateDiscoveryTests.RunAsync(checks);
            await ModelCatalogTests.RunAsync(checks);
            await SeepReverseLabPluginTests.RunAsync(checks);
            await SseProtocolTests.RunAsync(checks);
            await ModelClientStreamingTests.RunAsync(checks);
            await StreamingLoopTests.RunAsync(checks);
            await SseCounterexampleTests.RunAsync(checks);
            await NativeChatSessionStreamingTests.RunAsync(checks);
            Console.WriteLine($"\n全部通过：{checks.Passed} 项断言");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
