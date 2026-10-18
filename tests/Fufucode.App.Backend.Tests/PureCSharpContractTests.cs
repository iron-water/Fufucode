using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>纯 C# Agent 内核契约完整性：无外部 Node/Python 依赖的循环接口。</summary>
internal static class PureCSharpContractTests
{
    public static async Task RunAsync(Checks checks)
    {
        // 契约面：接口必须存在并可由纯托管实现。
        var type = typeof(IPureCSharpAgentEngine);
        checks.Check(type.GetMethod(nameof(IPureCSharpAgentEngine.RunTurnAsync)) is not null,
            "纯 C# 契约提供 RunTurnAsync");
        checks.Check(type.GetMethod(nameof(IPureCSharpAgentEngine.GetState)) is not null,
            "纯 C# 契约提供 GetState");
        checks.Check(type.GetMethod(nameof(IPureCSharpAgentEngine.Reset)) is not null,
            "纯 C# 契约提供 Reset");
        checks.Check(typeof(IPureToolDispatcher).GetMethod(nameof(IPureToolDispatcher.DispatchAsync)) is not null,
            "工具调度契约提供 DispatchAsync");

        var messages = new List<PureAgentMessage>
        {
            new(PureAgentRole.System, "你是助手。"),
            new(PureAgentRole.User, "你好。"),
        };
        var request = new PureModelRequest(messages, Array.Empty<string>(), "local-model");

        // 文本回合（Fake 引擎）
        var textEngine = new FakePureEngine { Model = _ => PureModelResponse.FromText("完成") };
        var textTurn = await textEngine.RunTurnAsync(request);
        checks.Equal(PureAgentTurnStatus.Completed, textTurn.Status, "文本回合状态为 Completed");
        checks.Equal("完成", textTurn.Response.Text, "文本回合返回模型文本");
        checks.Equal(0, textTurn.ToolResults.Count, "文本回合无工具结果");
        checks.Equal(2, textEngine.GetState().MessageCount, "内核状态记录消息数量");

        // 工具回合（Fake 引擎）
        var toolEngine = new FakePureEngine
        {
            Model = _ => PureModelResponse.FromToolCalls(new[]
            {
                new PureToolCall("call-1", "read_file", "{\"path\":\"README.md\"}"),
            }),
        };
        var toolTurn = await toolEngine.RunTurnAsync(request);
        checks.Equal(PureAgentTurnStatus.ToolCalls, toolTurn.Status, "工具回合状态为 ToolCalls");
        checks.Equal(1, toolTurn.ToolResults.Count, "工具回合返回一条工具结果");
        checks.Check(toolTurn.ToolResults[0].Success, "工具结果标记成功");
        checks.Equal("call-1", toolEngine.Dispatched[0].Id, "内核记录并调度工具调用");

        // 状态重置
        toolEngine.Reset();
        checks.Equal(0, toolEngine.GetState().MessageCount, "Reset 清空消息计数");
        checks.Check(!toolEngine.GetState().IsRunning, "Reset 后内核不再处于运行中");

        // 取消传播
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        await checks.ThrowsAsync<OperationCanceledException>(
            () => new FakePureEngine().RunTurnAsync(request, cts.Token),
            "纯 C# 契约尊重取消令牌");

        // ════════════════ 生产级工具调度器（ProductionToolDispatcher）测试 ════════════════
        var tempDir = Path.Combine(Path.GetTempPath(), "fufucode-native-tool-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var dispatcher = new ProductionToolDispatcher(tempDir);

            // 1. 写文件
            var writeRes = await dispatcher.DispatchAsync(new PureToolCall(
                "c1", "write_file", "{\"path\":\"sample.txt\",\"content\":\"Hello Native C# Agent\"}"));
            checks.Check(writeRes.Success, "ProductionToolDispatcher 成功写入文件");
            checks.Check(File.Exists(Path.Combine(tempDir, "sample.txt")), "真实文件已落盘");

            // 2. 读文件
            var readRes = await dispatcher.DispatchAsync(new PureToolCall(
                "c2", "read_file", "{\"path\":\"sample.txt\"}"));
            checks.Check(readRes.Success, "ProductionToolDispatcher 成功读取文件");
            checks.Equal("Hello Native C# Agent", readRes.Content, "文件内容正确读回");

            // 3. 目录列表
            var listRes = await dispatcher.DispatchAsync(new PureToolCall("c3", "list_dir", "{}"));
            checks.Check(listRes.Success && listRes.Content.Contains("sample.txt"), "ProductionToolDispatcher 列出目录内容");

            // 4. 未知工具报错
            var unknownRes = await dispatcher.DispatchAsync(new PureToolCall("c4", "non_exist_tool", "{}"));
            checks.Check(!unknownRes.Success && unknownRes.Content.Contains("未知工具"), "未知工具如实报错");

            // ════════════════ 真实多轮 Agent Loop 与结果回填闭环测试 ════════════════
            // 创建真实包含 ProductionToolDispatcher 的原生引擎
            var realEngine = new PureCSharpAgentEngine(dispatcher, new ScriptedToolEchoClient());

            // 模拟用户请求："请读取 sample.txt 文件"
            var loopMessages = new List<PureAgentMessage>
            {
                new(PureAgentRole.System, "你是助手。"),
                new(PureAgentRole.User, "请读取 sample.txt。"),
            };
            var loopRequest = new PureModelRequest(loopMessages, new[] { "read_file", "list_dir" }, "csharp-native");

            // 推进一轮：内置客户端第一步生成 ToolCall，调度器读取 sample.txt，结果回填，第二步模型生成最终答复
            var loopTurn = await realEngine.RunTurnAsync(loopRequest);

            checks.Equal(PureAgentTurnStatus.Completed, loopTurn.Status, "真实原生 Agent 循环顺利达到终态 Completed");
            checks.Equal(1, loopTurn.ToolResults.Count, "全流程产生并记录了 1 个工具执行结果");
            checks.Check(loopTurn.ToolResults[0].Success, "工具执行结果标记成功");
            checks.Check(loopTurn.Response.Text?.Contains("Hello Native C# Agent") == true, "模型最终回复中完整综合并呈现了工具回填结果");

            // 验证真实上下文历史：包含 System, User, Assistant(带ToolCall), Tool(回填结果), Assistant(最终答复)
            var finalState = realEngine.GetState();
            checks.Equal(5, finalState.MessageCount, "完整 Agent Loop 多轮交互历史条目为 5");
            checks.Check(finalState.History is not null && finalState.History[3].Role == PureAgentRole.Tool, "历史中精确记录了 Tool 角色的回填消息");
            checks.Check(finalState.History![2].Role == PureAgentRole.Assistant && finalState.History[2].ToolCalls?.Count == 1, "历史中记录了带有 ToolCall 的 Assistant 消息");

            // 验证 Reset 清空历史
            realEngine.Reset();
            checks.Equal(0, realEngine.GetState().MessageCount, "Reset 成功清空完整历史");
            checks.Equal(0, realEngine.GetState().History?.Count ?? 0, "Reset 后历史快照列表为空");

            // ════════════════ edit_file 差异编辑能力测试 ════════════════
            var editTarget = Path.Combine(tempDir, "code.cs");
            await File.WriteAllTextAsync(editTarget, "public class Calculator {\n    public int Add(int a, int b) => a + b;\n    public int Sub(int a, int b) => a - b;\n}");

            // 1) 精确单处替换
            var editRes1 = await dispatcher.DispatchAsync(new PureToolCall(
                "e1", "edit_file", "{\"path\":\"code.cs\",\"old_string\":\"public int Add(int a, int b) => a + b;\",\"new_string\":\"public int Add(int a, int b) { return a + b; }\"}"));
            checks.Check(editRes1.Success, "edit_file 单处精准替换成功");
            var updatedCode = await File.ReadAllTextAsync(editTarget);
            checks.Check(updatedCode.Contains("public int Add(int a, int b) { return a + b; }"), "目标代码已被精确修改");

            // 2) 零匹配校验
            var editRes2 = await dispatcher.DispatchAsync(new PureToolCall(
                "e2", "edit_file", "{\"path\":\"code.cs\",\"old_string\":\"non_existent_code();\",\"new_string\":\"foo();\"}"));
            checks.Check(!editRes2.Success && editRes2.Content.Contains("未找到匹配"), "edit_file 无匹配时如实报错");

            // 3) 多重匹配守卫（replace_all 为 false 时拒绝并警告）
            await File.WriteAllTextAsync(editTarget, "foo\nbar\nfoo\n");
            var editRes3 = await dispatcher.DispatchAsync(new PureToolCall(
                "e3", "edit_file", "{\"path\":\"code.cs\",\"old_string\":\"foo\",\"new_string\":\"baz\"}"));
            checks.Check(!editRes3.Success && editRes3.Content.Contains("2 处匹配"), "多重匹配且未开启 replace_all 时安全拒绝");

            // 4) 开启 replace_all: true 全部替换
            var editRes4 = await dispatcher.DispatchAsync(new PureToolCall(
                "e4", "edit_file", "{\"path\":\"code.cs\",\"old_string\":\"foo\",\"new_string\":\"baz\",\"replace_all\":true}"));
            checks.Check(editRes4.Success && editRes4.Content.Contains("2 处"), "replace_all 成功替换全部实例");
            checks.Equal("baz\nbar\nbaz\n", await File.ReadAllTextAsync(editTarget), "文件内容完全符合全局替换预期");

            // 5) CRLF 保持测试
            await File.WriteAllTextAsync(editTarget, "line1\r\nline2\r\nline3\r\n");
            var editCrlf = await dispatcher.DispatchAsync(new PureToolCall(
                "e5", "edit_file", "{\"path\":\"code.cs\",\"old_string\":\"line2\",\"new_string\":\"modified_line2\"}"));
            checks.Check(editCrlf.Success, "CRLF 文件编辑成功");
            var crlfText = await File.ReadAllTextAsync(editTarget);
            checks.Check(crlfText.Contains("\r\nmodified_line2\r\n"), "编辑后原生保持 CRLF 换行风格");

            // ════════════════ read_file 行号与分页测试 ════════════════
            await File.WriteAllTextAsync(editTarget, "First Line\nSecond Line\nThird Line\nFourth Line\nFifth Line\n");
            var readPaged = await dispatcher.DispatchAsync(new PureToolCall(
                "r1", "read_file", "{\"path\":\"code.cs\",\"offset\":2,\"limit\":2}"));
            checks.Check(readPaged.Success, "read_file 支持 offset 与 limit 分页");
            checks.Check(readPaged.Content.Contains("2\tSecond Line") && readPaged.Content.Contains("3\tThird Line"), "read_file 分页输出带行号前缀");
            checks.Check(!readPaged.Content.Contains("First Line") && !readPaged.Content.Contains("Fourth Line"), "分页切片范围精确");

            // ════════════════ grep_search 正则搜索与目录忽略测试 ════════════════
            var subDir = Path.Combine(tempDir, "src", "Controllers");
            var binDir = Path.Combine(tempDir, "bin", "Debug");
            Directory.CreateDirectory(subDir);
            Directory.CreateDirectory(binDir);
            await File.WriteAllTextAsync(Path.Combine(subDir, "UserController.cs"), "public class UserController { void Login() { } }");
            await File.WriteAllTextAsync(Path.Combine(binDir, "Ignored.cs"), "public class IgnoredController { void Login() { } }");

            var grepRes = await dispatcher.DispatchAsync(new PureToolCall(
                "g1", "grep_search", "{\"pattern\":\"Login\\\\(\\\\)\"}"));
            checks.Check(grepRes.Success, "grep_search 执行成功");
            checks.Check(grepRes.Content.Contains("UserController.cs") && grepRes.Content.Contains("Login()"), "grep_search 命中源码文件中的符号");
            checks.Check(!grepRes.Content.Contains("Ignored.cs"), "grep_search 自动跳过 bin/ 等构建输出目录");

            // ════════════════ glob_files 通配路径发现测试 ════════════════
            var globRes = await dispatcher.DispatchAsync(new PureToolCall(
                "gl1", "glob_files", "{\"pattern\":\"*.cs\"}"));
            checks.Check(globRes.Success, "glob_files 执行成功");
            checks.Check(globRes.Content.Contains("code.cs"), "glob_files 发现匹配的文件路径");

            // ════════════════ 路径沙箱越界拦截测试 ════════════════
            var escapeRes = await dispatcher.DispatchAsync(new PureToolCall(
                "sec1", "read_file", "{\"path\":\"../../../../Windows/System32/drivers/etc/hosts\"}"));
            checks.Check(!escapeRes.Success && escapeRes.Content.Contains("超出工作区范围"), "路径越界访问被沙箱守卫拦截拒绝");

            // ════════════════ 动态 Tool Schema 注入与生成测试 ════════════════
            var customToolDef = new PureToolDefinition("custom_browser_click", "点击网页元素",
                "{\"type\":\"object\",\"properties\":{\"selector\":{\"type\":\"string\"}},\"required\":[\"selector\"]}");
            var schemaReq = new PureModelRequest(
                new[] { new PureAgentMessage(PureAgentRole.User, "test") },
                new[] { "read_file", "edit_file", "custom_browser_click" },
                "test-model",
                new[] { customToolDef });

            var body = OpenAiCompatibleModelClient.BuildRequestBody(schemaReq, "test-model", false);
            checks.Check(body.ContainsKey("tools"), "BuildRequestBody 包含 tools 列表");
            var toolsArray = (List<object>)body["tools"];
            checks.Equal(3, toolsArray.Count, "注册的 3 个工具全部进入 tools");

            // ════════════════ System Prompt 单例防累积与多轮上下文测试 ════════════════
            var multiTurnEngine = new PureCSharpAgentEngine(dispatcher, new FixedTextClient("ok"));
            var turn1Req = new PureModelRequest(
                new[] { new PureAgentMessage(PureAgentRole.System, "系统设定v1"), new PureAgentMessage(PureAgentRole.User, "轮次1") },
                Array.Empty<string>());
            await multiTurnEngine.RunTurnAsync(turn1Req);

            var turn2Req = new PureModelRequest(
                new[] { new PureAgentMessage(PureAgentRole.System, "系统设定v2"), new PureAgentMessage(PureAgentRole.User, "轮次2") },
                Array.Empty<string>());
            await multiTurnEngine.RunTurnAsync(turn2Req);

            var multiTurnHistory = multiTurnEngine.GetState().History!;
            var systemCount = multiTurnHistory.Count(m => m.Role == PureAgentRole.System);
            checks.Equal(1, systemCount, "多轮会话后历史中始终只保留一条 System 消息（不发生重复堆叠）");
            checks.Equal("系统设定v2", multiTurnHistory[0].Content, "System 消息在上下文开头维持最新版本");

            // ════════════════ 会话历史还原（RestoreHistory）测试 ════════════════
            var restoredEngine = new PureCSharpAgentEngine(dispatcher, new FixedTextClient("ok"));
            restoredEngine.RestoreHistory(multiTurnHistory);
            checks.Equal(multiTurnHistory.Count, restoredEngine.GetState().MessageCount, "RestoreHistory 精确还原完整历史消息数");

            // ════════════════ 思考流（Thinking / Reasoning）流式解析测试 ════════════════
            var parser = new ChatCompletionSseParser();
            var thinkingDeltas = new List<string>();
            var sseFrame = new ServerSentEvent(null, "{\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"思考阶段分析代码\"}}]}", null, null);
            var applied = parser.TryApply(sseFrame, new ModelStreamCallbacks { OnThinkingDelta = thinkingDeltas.Add });
            checks.Check(applied, "ChatCompletionSseParser 解析出思考帧");
            checks.Equal("思考阶段分析代码", parser.Completion.Primary?.Thinking, "StreamedChoice 聚合完整思考文本");
            checks.Equal("思考阶段分析代码", thinkingDeltas.Single(), "OnThinkingDelta 回调收到思考增量");

            // ════════════════ 用量帧解析测试 ════════════════
            var usageEvents = new List<PureUsage>();
            var usageFrame = new ServerSentEvent(null,
                "{\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}",
                null, null);
            parser.TryApply(usageFrame, new ModelStreamCallbacks { OnUsage = usageEvents.Add });
            checks.Equal(10L, usageEvents.Single().InputTokens, "解析 prompt_tokens");
            checks.Equal(5L, usageEvents.Single().OutputTokens, "解析 completion_tokens");

            // ════════════════ 多模态 Vision 图像载荷装配测试 ════════════════
            var imageMsg = new PureAgentMessage(PureAgentRole.User, "分析此图",
                Images: new[] { new PureImageAttachment("iVBORw0KGgoAAAANSUhEUg==", "image/png") });
            var visionReq = new PureModelRequest(new[] { imageMsg }, Array.Empty<string>(), "gpt-4o");
            var visionBody = OpenAiCompatibleModelClient.BuildRequestBody(visionReq, "gpt-4o", false);
            var messagesArray = (List<object>)visionBody["messages"];
            checks.Check(messagesArray.Count > 0, "BuildRequestBody 包含多模态消息");

            // ════════════════ 统一接线能力接口测试（NativeCSharpChatSession） ════════════════
            var nativeSession = new NativeCSharpChatSession(restoredEngine, "test-session", "deepseek-v4");
            checks.Check(nativeSession.SupportsExecutionModes, "NativeCSharpChatSession 声明支持执行模式");
            checks.Check(nativeSession.CanSendImages, "NativeCSharpChatSession 声明支持多模态图像");
            string? modeChanged = null;
            nativeSession.ExecutionModeChanged += m => modeChanged = m;
            await nativeSession.SetExecutionModeAsync("auto");
            checks.Equal("auto", nativeSession.ExecutionMode, "执行模式成功切换为 auto");
            checks.Equal("auto", modeChanged, "ExecutionModeChanged 事件正确分发");

            nativeSession.SetModel("gpt-5-preview");
            checks.Equal("gpt-5-preview", nativeSession.CurrentModel, "SetModel 成功动态切换会话模型");

            // ════════════════ 统一会话协调器（NativeSessionCoordinator）测试 ════════════════
            var coordinator = new NativeSessionCoordinator(nativeSession);
            checks.Equal(ApprovalAvailability.Unavailable, coordinator.ApprovalAvailability, "FUMO 不使用外部审批桥，状态如实为不可用");
            checks.Check(coordinator.ApprovalDiagnostic is not null, "审批状态附带真实原因说明");

            var thinkingLevels = await coordinator.QueryThinkingLevelsAsync();
            checks.Check(thinkingLevels.Supports("high"), "NativeSessionCoordinator 支持 high 思考等级");
            var setLevelRes = await coordinator.SetThinkingLevelAsync("high");
            checks.Check(setLevelRes.Success, "SetThinkingLevelAsync 成功生效");

            var commandsRes = await coordinator.QueryCommandsAsync();
            checks.Check(commandsRes.Success, "QueryCommandsAsync 成功查询到命令列表");

            var entriesRes = await coordinator.QueryEntriesAsync();
            checks.Check(entriesRes.Success, "QueryEntriesAsync 成功返回历史条目树");

            var forkRes = await coordinator.ForkAsync("entry_0");
            checks.Check(!forkRes.Success, "FUMO 未实现分叉时如实返回失败，不伪造成功");

            // ════════════════ update_plan 计划任务卡片测试 ════════════════
            var planCall = new PureToolCall("p1", "update_plan",
                "{\"steps\":[{\"title\":\"读取代码\",\"status\":\"completed\"},{\"title\":\"重构模块\",\"status\":\"in_progress\"},{\"title\":\"执行测试\",\"status\":\"pending\"}],\"notes\":\"注意向后兼容\"}");
            var planRes = await dispatcher.DispatchAsync(planCall);
            checks.Check(planRes.Success, "update_plan 执行成功");
            checks.Check(planRes.Content.Contains("[x] 1. 读取代码 (completed)"), "计划卡片包含已完成标记");
            checks.Check(planRes.Content.Contains("[>] 2. 重构模块 (in_progress)"), "计划卡片包含进行中标记");
            checks.Check(dispatcher.CurrentPlan is not null && dispatcher.CurrentPlan.Steps.Count == 3, "Dispatcher 记录当前结构化计划");

            // 多于 1 个 in_progress 必须安全拒绝
            var badPlanCall = new PureToolCall("p2", "update_plan",
                "{\"steps\":[{\"title\":\"任务1\",\"status\":\"in_progress\"},{\"title\":\"任务2\",\"status\":\"in_progress\"}]}");
            var badPlanRes = await dispatcher.DispatchAsync(badPlanCall);
            checks.Check(!badPlanRes.Success && badPlanRes.Content.Contains("最多只能有 1 个步骤"), "同时多个 in_progress 步骤被安全拒绝");

            // ════════════════ Plan 模式只读权限守卫测试 ════════════════
            dispatcher.Mode = PermissionMode.Plan;
            var mutateCall = new PureToolCall("m1", "write_file", "{\"path\":\"fail.txt\",\"content\":\"fail\"}");
            var mutateRes = await dispatcher.DispatchAsync(mutateCall);
            checks.Check(!mutateRes.Success && mutateRes.Content.Contains("Plan 规划模式拦截"), "Plan 模式下拦截文件修改操作");

            var readUnderPlan = await dispatcher.DispatchAsync(new PureToolCall("r_plan", "read_file", "{\"path\":\"sample.txt\"}"));
            checks.Check(readUnderPlan.Success, "Plan 模式下放行只读工具");
            dispatcher.Mode = PermissionMode.Default;

            // ════════════════ 运行中排队、QueueChanged 与 Stop 草稿恢复测试 ════════════════
            var blockingClient = new BlockingTextClient();
            await using (var queueSession = new NativeCSharpChatSession(
                             new PureCSharpAgentEngine(dispatcher, blockingClient), "queue-session", "m"))
            {
                var queueEvents = new List<SessionEvent>();
                queueSession.Event += (_, e) => queueEvents.Add(e);

                await queueSession.SendAsync("第一条");
                await blockingClient.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await queueSession.SendAsync("排队消息测试");

                checks.Equal(1, queueSession.QueuedCount, "运行中发送的消息进入会话队列");
                checks.Check(queueSession.CanStop, "队列非空时 CanStop 为 true");
                checks.Check(queueEvents.OfType<SessionEvent.QueueChanged>().Any(e => e.QueuedTexts.Contains("排队消息测试")),
                    "入队时派发 QueueChanged 事件（UI 可见）");

                await queueSession.StopAsync();
                checks.Equal(0, queueSession.QueuedCount, "Stop 清空队列");
                checks.Check(queueEvents.OfType<SessionEvent.QueueCleared>()
                        .Any(e => e.ClearedTexts.Contains("排队消息测试")),
                    "Stop 通过 QueueCleared 把队列交还为可恢复草稿");
                checks.Equal(SessionActivity.Idle, queueSession.Activity, "Stop 收敛后会话回到 Idle");
            }

            // 空闲时的 FollowUp 不应滞留队列，而是立即发送。
            await coordinator.SendPromptAsync("空闲排队消息", PromptDelivery.FollowUp);
            checks.Check(!coordinator.Snapshot.QueuedTexts.Contains("空闲排队消息"),
                "空闲时 FollowUp 立即执行而不是无限期滞留队列");

            // ════════════════ 工具参数与输入安全回归 ════════════════
            var emptyOldRes = await dispatcher.DispatchAsync(new PureToolCall(
                "e_empty", "edit_file", "{\"path\":\"code.cs\",\"old_string\":\"\",\"new_string\":\"x\"}"));
            checks.Check(!emptyOldRes.Success && emptyOldRes.Content.Contains("不能为空"),
                "空 old_string 被立即拒绝（不会死循环）");

            await File.WriteAllTextAsync(editTarget, "keep-me");
            var noContentRes = await dispatcher.DispatchAsync(new PureToolCall(
                "w_nc", "write_file", "{\"path\":\"code.cs\"}"));
            checks.Check(!noContentRes.Success, "缺少 content 的 write_file 被拒绝");
            checks.Equal("keep-me", await File.ReadAllTextAsync(editTarget), "缺少 content 时原文件未被清空");

            var binPath = Path.Combine(tempDir, "blob.bin");
            await File.WriteAllBytesAsync(binPath, new byte[] { 0, 1, 2, 3, 65 });
            var binRes = await dispatcher.DispatchAsync(new PureToolCall("b1", "read_file", "{\"path\":\"blob.bin\"}"));
            checks.Check(!binRes.Success && binRes.Content.Contains("二进制"), "二进制文件被 read_file 拒绝");

            var pastEndRes = await dispatcher.DispatchAsync(new PureToolCall(
                "r_end", "read_file", "{\"path\":\"code.cs\",\"offset\":9999}"));
            checks.Check(!pastEndRes.Success && pastEndRes.Content.Contains("超出文件总行数"),
                "offset 越界明确报错而不是静默返回末行");

            await File.WriteAllTextAsync(Path.Combine(tempDir, "Root.cs"), "class Root {}");
            var globRootRes = await dispatcher.DispatchAsync(new PureToolCall(
                "gl_root", "glob_files", "{\"pattern\":\"**/*.cs\"}"));
            checks.Check(globRootRes.Content.Contains("Root.cs"), "**/*.cs 能匹配根目录文件");

            // ════════════════ 取消时 tool_call ↔ tool_result 配对 ════════════════
            var cancelDispatcher = new CancelBlockingDispatcher();
            var cancelLoop = new NativeAgentLoop(new ScriptedToolEchoClient(), cancelDispatcher, 3);
            var cancelHistory = new List<PureAgentMessage> { new(PureAgentRole.User, "读取 sample.txt") };
            using var cancelCts = new CancellationTokenSource();
            var cancelRun = cancelLoop.ExecuteLoopAsync(cancelHistory, new[] { "read_file" }, "m",
                null, null, cancelCts.Token);
            await cancelDispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelCts.Cancel();
            await checks.ThrowsAsync<OperationCanceledException>(() => cancelRun, "取消沿 Agent 循环传播");
            checks.Check(cancelHistory.Any(m => m.Role == PureAgentRole.Assistant && m.ToolCalls is { Count: > 0 }),
                "取消前已记录 assistant tool_call");
            checks.Equal(1, cancelHistory.Count(m => m.Role == PureAgentRole.Tool),
                "取消后补齐配对的 tool_result，历史仍可继续请求");

            // ════════════════ 协调器空闲 Stop 不得卡在 Cancelling ════════════════
            var idleCoordinator = new NativeSessionCoordinator(nativeSession);
            await idleCoordinator.StopAsync();
            checks.Equal(ActivityState.Idle, idleCoordinator.Snapshot.Activity,
                "空闲时 Stop 后协调器回到 Idle，而不是停留在 Cancelling");

            // ════════════════ 超长上下文确定性压缩 ════════════════
            var compactEngine = new PureCSharpAgentEngine(dispatcher, new FixedTextClient("压缩后答复"));
            var largeHistory = new List<PureAgentMessage>
            {
                new(PureAgentRole.System, "系统设定")
            };
            for (var i = 0; i < 30; i++)
                largeHistory.Add(new PureAgentMessage(PureAgentRole.User, new string('x', 20_000) + i));
            compactEngine.RestoreHistory(largeHistory);
            await compactEngine.RunTurnAsync(new PureModelRequest(
                new[] { new PureAgentMessage(PureAgentRole.User, "继续") }, Array.Empty<string>()));
            var compacted = compactEngine.GetState().History!;
            checks.Check(compacted.Count < largeHistory.Count + 1, "超长历史被压缩而不是原样发送");
            checks.Check(compacted.Any(m => m.Content.Contains("上下文") && m.Content.Contains("压缩")),
                "压缩后插入明确的摘要/省略说明");
            checks.Equal(PureAgentRole.System, compacted[0].Role, "压缩后 System 消息仍保留在首位");

            // ════════════════ 人工确认（manual）真实拦截变更工具 ════════════════
            var approvalFile = Path.Combine(tempDir, "approval.txt");
            var writeCall = new PureToolCall("w_approval", "write_file",
                "{\"path\":\"approval.txt\",\"content\":\"created\"}");
            var denyEngine = new PureCSharpAgentEngine(dispatcher, new FixedToolCallClient(writeCall));
            await using (var denySession = new NativeCSharpChatSession(denyEngine, "deny", "m"))
            {
                var denySettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var asked = false;
                denySession.Event += (_, e) => { if (e is SessionEvent.TurnSettled) denySettled.TrySetResult(); };
                denySession.ExtensionUiRequestReceived += req =>
                {
                    asked = true;
                    _ = denySession.RespondToExtensionUiAsync(req, PiExtensionUiResponse.Confirm(false));
                };
                await denySession.SetExecutionModeAsync("manual");
                await denySession.SendAsync("写入文件");
                await denySettled.Task.WaitAsync(TimeSpan.FromSeconds(10));

                checks.Check(asked, "manual 模式下变更工具触发人工确认请求");
                checks.Check(!File.Exists(approvalFile), "拒绝后文件未创建");
            }

            var allowEngine = new PureCSharpAgentEngine(dispatcher, new FixedToolCallClient(writeCall));
            await using (var allowSession = new NativeCSharpChatSession(allowEngine, "allow", "m"))
            {
                var allowSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                allowSession.Event += (_, e) => { if (e is SessionEvent.TurnSettled) allowSettled.TrySetResult(); };
                allowSession.ExtensionUiRequestReceived += req =>
                    _ = allowSession.RespondToExtensionUiAsync(req, PiExtensionUiResponse.Confirm(true));
                await allowSession.SetExecutionModeAsync("manual");
                await allowSession.SendAsync("写入文件");
                await allowSettled.Task.WaitAsync(TimeSpan.FromSeconds(10));

                checks.Check(File.Exists(approvalFile), "批准后文件真实创建");
            }

            // ════════════════ .gitignore 遵循 ════════════════
            await File.WriteAllTextAsync(Path.Combine(tempDir, ".gitignore"), "ignored/\n*.log\n");
            var ignoredDir = Path.Combine(tempDir, "ignored");
            Directory.CreateDirectory(ignoredDir);
            await File.WriteAllTextAsync(Path.Combine(ignoredDir, "Skip.cs"), "class SkipController { }");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Ignored.log"), "class LogOnly { }");

            var gitignoreGrep = await dispatcher.DispatchAsync(new PureToolCall("gi1", "grep_search",
                "{\"pattern\":\"SkipController|LogOnly\"}"));
            checks.Check(!gitignoreGrep.Content.Contains("Skip.cs") && !gitignoreGrep.Content.Contains("Ignored.log"),
                "grep_search 遵循 .gitignore 排除文件");
            var gitignoreGlob = await dispatcher.DispatchAsync(new PureToolCall("gi2", "glob_files",
                "{\"pattern\":\"**/*.cs\"}"));
            checks.Check(!gitignoreGlob.Content.Contains("ignored/Skip.cs"), "glob_files 遵循 .gitignore 排除目录");

            // ════════════════ 插件工具组合路由 ════════════════
            var plugin = new FakePluginDispatcher("browser_click", "clicked");
            var composite = new NativeToolComposite(new ProductionToolDispatcher(tempDir), () => new[] { plugin });
            checks.Check(composite.ActiveToolNames.Contains("browser_click"), "组合目录暴露插件工具");
            var pluginResult = await composite.DispatchAsync(new PureToolCall("pc1", "browser_click", "{}"));
            checks.Check(pluginResult.Success && pluginResult.Content == "clicked", "插件工具路由到插件注册表");
            var builtinResult = await composite.DispatchAsync(new PureToolCall("pc2", "list_dir", "{\"path\":\".\"}"));
            checks.Check(builtinResult.Success, "非插件工具回落到内置调度器（工作区正确）");

            // ════════════════ 技能发现与渐进披露 ════════════════
            var skillDir = Path.Combine(tempDir, ".fumo", "skills", "pdf");
            var hiddenSkillDir = Path.Combine(tempDir, ".fumo", "skills", "secret");
            Directory.CreateDirectory(skillDir);
            Directory.CreateDirectory(hiddenSkillDir);
            await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"),
                "---\nname: pdf\ndescription: 处理 PDF 文件\n---\nPDF 技能说明：使用 pdftotext 提取文本。");
            await File.WriteAllTextAsync(Path.Combine(skillDir, "extract.sh"), "#!/bin/sh\necho hi");
            await File.WriteAllTextAsync(Path.Combine(hiddenSkillDir, "SKILL.md"),
                "---\nname: secret\ndescription: 仅用户可调用\ndisable-model-invocation: true\n---\nSECRET 技能正文。");

            var skills = SkillRegistry.Discover(tempDir, Path.Combine(tempDir, "userhome"));
            checks.Check(skills.TryGet("pdf", out var pdfSkill) && pdfSkill.Description.Contains("PDF"),
                "发现 SKILL.md 并解析 frontmatter");
            checks.Check(skills.ModelInvocable.Any(skill => skill.Name == "pdf"), "普通技能对模型可见");
            checks.Check(!skills.ModelInvocable.Any(skill => skill.Name == "secret"),
                "disable-model-invocation 技能不进入模型目录");
            checks.Check(skills.BuildPromptSection().Contains("pdf") &&
                         !skills.BuildPromptSection().Contains("secret"),
                "系统提示段只列出模型可调用技能");
            checks.Check(skills.ExpandUserInvocation("/skill:secret 参数") is { } expanded &&
                         expanded.Contains("SECRET 技能正文"),
                "用户仍可通过 /skill:name 调用受限技能");

            var toolResult = await skills.DispatchAsync(new PureToolCall("s1", "skill", "{\"name\":\"pdf\"}"));
            checks.Check(toolResult.Success && toolResult.Content.Contains("PDF 技能说明") &&
                         toolResult.Content.Contains("Base directory") && toolResult.Content.Contains("extract.sh"),
                "skill 工具返回完整说明、基目录与附带文件");
            var deniedSkill = await skills.DispatchAsync(new PureToolCall("s2", "skill", "{\"name\":\"secret\"}"));
            checks.Check(!deniedSkill.Success, "模型不能自动加载 disable-model-invocation 技能");

            // ════════════════ 会话接线：技能目录、skill 工具与 /skill 展开 ════════════════
            var capturingClient = new CapturingTextClient("技能轮答复");
            var skillDispatcher = new SkillAwareDispatcher(dispatcher, skills);
            var skillEngine = new PureCSharpAgentEngine(skillDispatcher, capturingClient);
            await using (var skillSession = new NativeCSharpChatSession(skillEngine, "skill-session", "m", skills: skills))
            {
                var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                skillSession.Event += (_, e) => { if (e is SessionEvent.TurnSettled) settled.TrySetResult(); };

                await skillSession.SendAsync("你好");
                await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var first = capturingClient.Requests[0];
                checks.Check(first.ToolNames.Contains("skill"), "会话向模型声明 skill 工具");
                checks.Check(first.ToolDefinitions?.Any(d => d.Name == "skill") == true, "skill 工具携带参数 Schema");
                checks.Check(first.Messages[0].Role == PureAgentRole.System &&
                             first.Messages[0].Content.Contains("可用技能"),
                    "系统提示注入紧凑技能目录");

                settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await skillSession.SendAsync("/skill:pdf 指定页");
                await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var expandedMessage = capturingClient.Requests[^1].Messages[^1];
                checks.Check(expandedMessage.Content.Contains("PDF 技能说明") && expandedMessage.Content.Contains("指定页"),
                    "/skill:name 把技能正文与用户参数注入本轮消息");
            }

            // ════════════════ CLI：cwd 与超时参数 ════════════════
            var cliSubDir = Path.Combine(tempDir, "sub");
            Directory.CreateDirectory(cliSubDir);
            var cwdCommand = OperatingSystem.IsWindows() ? "cd" : "pwd";
            var cwdResult = await dispatcher.DispatchAsync(new PureToolCall("cli1", "run_command",
                "{\"command\":\"" + cwdCommand + "\",\"cwd\":\"sub\",\"timeout_seconds\":5}"));
            checks.Check(cwdResult.Success && cwdResult.Content.Contains("sub"),
                "run_command 支持 cwd 并在指定目录执行");
            var badCwd = await dispatcher.DispatchAsync(new PureToolCall("cli2", "run_command",
                "{\"command\":\"" + cwdCommand + "\",\"cwd\":\"../outside\"}"));
            checks.Check(!badCwd.Success && badCwd.Content.Contains("超出工作区"),
                "run_command 的 cwd 仍受工作区沙箱约束");

            // ════════════════ 会话原子落盘（版本信封） ════════════════
            var persistPath = Path.Combine(tempDir, "persist-session.json");
            var persistSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var persistSession = new NativeCSharpChatSession(
                             new PureCSharpAgentEngine(dispatcher, new FixedTextClient("持久化答复")),
                             "persist-session", "m", persistFilePath: persistPath))
            {
                persistSession.Event += (_, e) => { if (e is SessionEvent.TurnSettled) persistSettled.TrySetResult(); };
                persistSession.SetModel("persisted-model");
                persistSession.SetThinkingLevel("high");
                await persistSession.SetExecutionModeAsync("auto");
                await persistSession.SendAsync("写入历史");
                await persistSettled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            checks.Check(File.Exists(persistPath), "FUMO 会话文件已落盘: " + persistPath);
            using var persistedDoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(persistPath));
            var persistedMessages = persistedDoc.RootElement.GetProperty("messages");
            checks.Check(persistedMessages.ValueKind == System.Text.Json.JsonValueKind.Array &&
                         persistedMessages.GetArrayLength() > 0,
                "FUMO 会话以版本信封原子落盘");
            checks.Check(persistedMessages.EnumerateArray().Any(m =>
                    m.GetProperty("Content").GetString() == "持久化答复"),
                "落盘历史包含本轮助手答复");
            var persistedRoot = persistedDoc.RootElement;
            checks.Equal("persisted-model", persistedRoot.GetProperty("modelId").GetString(),
                "会话模型随历史持久化");
            checks.Equal("high", persistedRoot.GetProperty("thinkingLevel").GetString(),
                "思考等级随历史持久化");
            checks.Equal("auto", persistedRoot.GetProperty("executionMode").GetString(),
                "执行模式随历史持久化");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
