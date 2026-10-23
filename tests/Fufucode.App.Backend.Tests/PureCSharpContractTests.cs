using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Plugins.Subagents;

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

            // ════════════════ 写盘前守卫：非法 JSON 不得落盘 ════════════════
            await File.WriteAllTextAsync(Path.Combine(tempDir, "good.json"), "{\"a\":1}");
            var badJsonWrite = await dispatcher.DispatchAsync(new PureToolCall("j1", "write_file",
                "{\"path\":\"good.json\",\"content\":\"{\\\"a\\\": 1,\"}"));
            checks.Check(!badJsonWrite.Success && badJsonWrite.Content.Contains("不是合法 JSON"),
                "非法 JSON 被写盘前守卫拒绝");
            checks.Equal("{\"a\":1}", await File.ReadAllTextAsync(Path.Combine(tempDir, "good.json")),
                "被拒绝的写入没有破坏原文件");

            var jsoncWrite = await dispatcher.DispatchAsync(new PureToolCall("j2", "write_file",
                "{\"path\":\"ok.json\",\"content\":\"{\\n  // 注释\\n  \\\"a\\\": 1,\\n}\"}"));
            checks.Check(jsoncWrite.Success, "允许注释与尾随逗号的 JSONC 不被误伤");

            var badJsonEdit = await dispatcher.DispatchAsync(new PureToolCall("j3", "edit_file",
                "{\"path\":\"good.json\",\"old_string\":\"1\",\"new_string\":\"}\"}"));
            checks.Check(!badJsonEdit.Success && badJsonEdit.Content.Contains("不是合法 JSON"),
                "edit_file 同样受写盘前守卫保护");

            // 整文件读取必须按行边界截断并给出续读指引，而不是整篇塞入后被循环层从中间切断。
            var longFile = Path.Combine(tempDir, "Long.cs");
            var longLines = string.Join('\n', Enumerable.Range(1, 12000)
                .Select(i => $"// line {i} padding padding padding"));
            await File.WriteAllTextAsync(longFile, longLines);
            // 工具层的整读上限必须低于循环层的 64KiB 结果预算，否则行边界会被二次截断。
            checks.Check(new FileInfo(longFile).Length > 64 * 1024, "测试文件同时超过整读上限与循环层预算");

            var longRead = await dispatcher.DispatchAsync(new PureToolCall("r_long", "read_file",
                "{\"path\":\"Long.cs\"}"));
            checks.Check(longRead.Success && longRead.Content.Contains("使用 offset:") &&
                         longRead.Content.Contains("// line 1 ") &&
                         !longRead.Content.Contains("// line 12000"),
                "超长文件整读按行截断并给出 offset 续读指引");

            // 行边界与续读提示必须完整穿过循环层的 64KiB 预算。
            var loopPruned = NativeAgentLoop.PruneForTest(longRead.Content);
            checks.Check(loopPruned.Contains("使用 offset:"),
                "续读提示不被循环层 64KiB 二次截断");

            var offsetHint = System.Text.RegularExpressions.Regex.Match(longRead.Content, @"使用 offset: (\d+)");
            checks.Check(offsetHint.Success, "截断提示包含具体的下一段 offset");
            var nextOffset = int.Parse(offsetHint.Groups[1].Value);
            var longContinue = await dispatcher.DispatchAsync(new PureToolCall("r_long2", "read_file",
                $"{{\"path\":\"Long.cs\",\"offset\":{nextOffset},\"limit\":5000}}"));
            checks.Check(longContinue.Success && longContinue.Content.Contains($"{nextOffset}\t// line {nextOffset} "),
                "按截断提示的 offset 可以继续读到后续内容");
            checks.Check(longContinue.Content.Contains($"{nextOffset + 4999}\t// line {nextOffset + 4999} "),
                "续读窗口覆盖 offset 之后 5000 行，文件深处可继续分页读取");

            // UTF-16 文本天然含 NUL 字节，不能被当成二进制拒绝——write_file/edit_file 都支持它。
            var utf16Path = Path.Combine(tempDir, "utf16.txt");
            await File.WriteAllTextAsync(utf16Path, "UTF16 内容", Encoding.Unicode);
            var utf16Read = await dispatcher.DispatchAsync(new PureToolCall("u16", "read_file",
                "{\"path\":\"utf16.txt\"}"));
            checks.Check(utf16Read.Success && utf16Read.Content.Contains("UTF16 内容"),
                "UTF-16 文本按 BOM 识别为文本，不被误判为二进制");

            // grep 跳过大文件时必须显式告知，否则"未找到"会变成假阴性。
            var bigDir = Path.Combine(tempDir, "bigscan");
            Directory.CreateDirectory(bigDir);
            await File.WriteAllTextAsync(Path.Combine(bigDir, "huge.log"),
                new string('z', 6 * 1024 * 1024) + "NEEDLE_IN_HUGE");
            var bigGrep = await dispatcher.DispatchAsync(new PureToolCall("bg1", "grep_search",
                "{\"pattern\":\"NEEDLE_IN_HUGE\",\"path\":\"bigscan\"}"));
            checks.Check(bigGrep.Success && bigGrep.Content.Contains("未参与检索"),
                "跳过大文件时结果明确告知，避免被误读为不存在");

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
            var compactEngine = new PureCSharpAgentEngine(dispatcher, new FixedTextClient("压缩后答复"))
            {
                // 压缩阈值来自模型真实窗口（这里注入 10k tokens），不再依赖写死的 400k 字符。
                ContextLengthResolver = (_, _) => Task.FromResult<long?>(10_000),
            };
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

            // ════════════════ grep 分组 + 相关度排序 + 分页 ════════════════
            var groupDir = Path.Combine(tempDir, "grouped");
            Directory.CreateDirectory(groupDir);
            // dense.cs 命中 4 处、sparse.cs 命中 1 处：按相关度 dense 应排在前面。
            await File.WriteAllTextAsync(Path.Combine(groupDir, "dense.cs"),
                "TARGET_MARK\nTARGET_MARK\nTARGET_MARK\nTARGET_MARK\n");
            await File.WriteAllTextAsync(Path.Combine(groupDir, "sparse.cs"), "TARGET_MARK\n");

            var grouped = await dispatcher.DispatchAsync(new PureToolCall("gg1", "grep_search",
                "{\"pattern\":\"TARGET_MARK\",\"path\":\"grouped\"}"));
            checks.Check(grouped.Success && grouped.Content.Contains("## grouped/dense.cs (4 处命中)"),
                "grep 结果按文件分组并标注命中数");
            checks.Check(grouped.Content.IndexOf("dense.cs", StringComparison.Ordinal) <
                         grouped.Content.IndexOf("sparse.cs", StringComparison.Ordinal),
                "命中更密集的文件排在前面（相关度排序）");

            var pagedGrep = await dispatcher.DispatchAsync(new PureToolCall("gg2", "grep_search",
                "{\"pattern\":\"TARGET_MARK\",\"path\":\"grouped\",\"offset\":1}"));
            checks.Check(pagedGrep.Success && !pagedGrep.Content.Contains("dense.cs") &&
                         pagedGrep.Content.Contains("sparse.cs"),
                "offset 按文件分页，跳过已看过的文件");

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

            // ════════════════ MCP：stdio 客户端 + 网关工具 ════════════════
            var mcpHome = Path.Combine(tempDir, "mcp-home");
            Directory.CreateDirectory(mcpHome);
            var processPath = Environment.ProcessPath ?? "dotnet";
            var stdioArgs = new List<string>();
            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet",
                    StringComparison.OrdinalIgnoreCase))
                stdioArgs.Add(typeof(PureCSharpContractTests).Assembly.Location);
            stdioArgs.Add("--fake-mcp-stdio");

            await using (var stdioManager = McpManager.Create(new[]
                         {
                             new McpServerConfig("fake", "stdio", Command: processPath,
                                 Args: stdioArgs, TimeoutSeconds: 20)
                         }, mcpHome))
            {
                var stdioTools = await stdioManager.ListToolsAsync("fake", CancellationToken.None);
                checks.Check(stdioTools.Any(tool => tool.Name == "echo") &&
                             stdioTools.Any(tool => tool.Name == "fail"),
                    "MCP stdio：tools/list 返回服务器声明的工具");
                var echo = await stdioManager.CallAsync("fake", "echo",
                    JsonSerializer.SerializeToElement(new { text = "ping" }), CancellationToken.None);
                checks.Check(echo.Success && echo.Content.Contains("echo:ping"),
                    "MCP stdio：tools/call 返回文本内容");
                var failed = await stdioManager.CallAsync("fake", "fail",
                    JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
                checks.Check(!failed.Success && failed.Content.Contains("expected failure"),
                    "MCP isError 映射为失败结果");

                var gateway = new McpGatewayDispatcher(new ProductionToolDispatcher(tempDir), stdioManager);
                var listServers = await gateway.DispatchAsync(new PureToolCall("m1", "mcp",
                    "{\"action\":\"list_servers\"}"));
                checks.Check(listServers.Success && listServers.Content.Contains("fake"), "MCP 网关列出服务器");
                var listTools = await gateway.DispatchAsync(new PureToolCall("m2", "mcp",
                    "{\"action\":\"list_tools\",\"server\":\"fake\"}"));
                checks.Check(listTools.Success && listTools.Content.Contains("echo") &&
                             listTools.Content.Contains("inputSchema"), "MCP 网关按需返回工具与 Schema");
                var gatewayCall = await gateway.DispatchAsync(new PureToolCall("m3", "mcp",
                    "{\"action\":\"call\",\"server\":\"fake\",\"tool\":\"echo\",\"arguments\":{\"text\":\"gw\"}}"));
                checks.Check(gatewayCall.Success && gatewayCall.Content.Contains("echo:gw") &&
                             gatewayCall.Content.Contains("[MCP fake/echo]"),
                    "MCP 网关调用工具并标注来源");
            }

            // ════════════════ MCP：Streamable HTTP（JSON 与 SSE） ════════════════
            using (var httpServer = new FakeMcpHttpServer())
            {
                await using var httpManager = McpManager.Create(new[]
                {
                    new McpServerConfig("http", "http", Url: httpServer.McpUrl, TimeoutSeconds: 10)
                }, mcpHome);
                var httpCall = await httpManager.CallAsync("http", "echo",
                    JsonSerializer.SerializeToElement(new { text = "json" }), CancellationToken.None);
                checks.Check(httpCall.Success && httpCall.Content.Contains("echo:json"),
                    "MCP HTTP：JSON 响应可正常调用");
                checks.Check(httpServer.McpRequestCount >= 3, "MCP HTTP 完成 initialize/list/call 往返");
            }

            using (var sseServer = new FakeMcpHttpServer { UseSse = true })
            {
                await using var sseManager = McpManager.Create(new[]
                {
                    new McpServerConfig("sse", "http", Url: sseServer.McpUrl, TimeoutSeconds: 10)
                }, mcpHome);
                var sseCall = await sseManager.CallAsync("sse", "echo",
                    JsonSerializer.SerializeToElement(new { text = "sse" }), CancellationToken.None);
                checks.Check(sseCall.Success && sseCall.Content.Contains("echo:sse"),
                    "MCP HTTP：SSE 响应可正常调用");
            }

            // ════════════════ MCP：Bearer 与 OAuth ════════════════
            using (var bearerServer = new FakeMcpHttpServer { RequiredBearerToken = "test-token" })
            {
                await using var bearerManager = McpManager.Create(new[]
                {
                    new McpServerConfig("bearer", "http", Url: bearerServer.McpUrl, TimeoutSeconds: 10,
                        Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer test-token" })
                }, mcpHome);
                var bearerCall = await bearerManager.CallAsync("bearer", "echo",
                    JsonSerializer.SerializeToElement(new { text = "bearer" }), CancellationToken.None);
                checks.Check(bearerCall.Success, "MCP HTTP：显式 Bearer 头通过认证");
            }

            using (var oauthServer = new FakeMcpHttpServer { RequiredBearerToken = "test-token" })
            {
                var oauthConfig = new McpServerConfig("oauth", "http", Url: oauthServer.McpUrl,
                    TimeoutSeconds: 10,
                    Auth: new McpAuthConfig("oauth", GrantType: "client_credentials",
                        TokenUrl: oauthServer.TokenUrl, ClientId: "client", ClientSecret: "secret"));
                await using var oauthManager = McpManager.Create(new[] { oauthConfig }, mcpHome);
                var oauthCall = await oauthManager.CallAsync("oauth", "echo",
                    JsonSerializer.SerializeToElement(new { text = "oauth" }), CancellationToken.None);
                checks.Check(oauthCall.Success, "MCP OAuth client_credentials 获取令牌并调用成功");
                checks.Check(oauthServer.TokenRequests.Any(body => body.Contains("grant_type=client_credentials")),
                    "OAuth 令牌请求使用 client_credentials");
            }

            using (var codeServer = new FakeMcpHttpServer())
            {
                string? authorizationUrlSeen = null;
                var codeConfig = new McpAuthConfig("oauth", GrantType: "authorization_code",
                    TokenUrl: codeServer.TokenUrl, AuthorizationUrl: "http://127.0.0.1:1/authorize",
                    ClientId: "client", Scopes: new[] { "read" });
                var provider = new OAuthTokenProvider("code-test", codeConfig, mcpHome, openBrowser: url =>
                {
                    authorizationUrlSeen = url;
                    var queryIndex = url.IndexOf('?');
                    var query = url[(queryIndex + 1)..];
                    var values = query.Split('&').Select(pair => pair.Split('=', 2))
                        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]),
                            parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(50);
                        using var browser = new HttpClient();
                        await browser.GetAsync($"{values["redirect_uri"]}?code=abc&state={values["state"]}");
                    });
                    return Task.CompletedTask;
                });
                var token = await provider.GetAccessTokenAsync(CancellationToken.None);
                checks.Check(token == "test-token", "OAuth authorization_code + PKCE 回调换取令牌");
                checks.Check(authorizationUrlSeen?.Contains("code_challenge_method=S256") == true,
                    "授权 URL 携带 PKCE challenge");
                checks.Check(codeServer.TokenRequests.Any(body => body.Contains("grant_type=authorization_code") &&
                                                                 body.Contains("code_verifier=")),
                    "令牌交换使用授权码与 PKCE verifier");
            }

            // ════════════════ MCP：项目配置信任门禁 ════════════════
            var projectMcpDir = Path.Combine(tempDir, ".fumo");
            Directory.CreateDirectory(projectMcpDir);
            await File.WriteAllTextAsync(Path.Combine(projectMcpDir, "mcp.json"),
                "{\"servers\":{\"project-server\":{\"transport\":\"stdio\",\"command\":\"echo\"}}}");
            await File.WriteAllTextAsync(McpConfigLoader.UserConfigPath(mcpHome),
                "{\"trustProjectConfig\":false,\"servers\":{\"user-server\":{\"transport\":\"stdio\",\"command\":\"echo\"}}}");
            var untrusted = McpConfigLoader.Load(tempDir, mcpHome);
            checks.Check(untrusted.Any(s => s.Name == "user-server") && !untrusted.Any(s => s.Name == "project-server"),
                "默认不加载项目级 MCP 服务器");
            await File.WriteAllTextAsync(McpConfigLoader.UserConfigPath(mcpHome),
                "{\"trustProjectConfig\":true,\"servers\":{\"user-server\":{\"transport\":\"stdio\",\"command\":\"echo\"}}}");
            var trusted = McpConfigLoader.Load(tempDir, mcpHome);
            checks.Check(trusted.Any(s => s.Name == "project-server"),
                "用户显式信任后加载项目级 MCP 服务器");

            // ════════════════ MCP：会话声明与系统提示 ════════════════
            var mcpCapturing = new CapturingTextClient("mcp 答复");
            await using (var mcpManager = McpManager.Create(new[]
                         {
                             new McpServerConfig("listed", "stdio", Command: "unused")
                         }, mcpHome))
            {
                var mcpDispatcher = new McpGatewayDispatcher(
                    new SkillAwareDispatcher(dispatcher, skills), mcpManager);
                var mcpEngine = new PureCSharpAgentEngine(mcpDispatcher, mcpCapturing);
                var mcpSession = new NativeCSharpChatSession(mcpEngine, "mcp-session", "m",
                    skills: skills, mcp: mcpManager);
                var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                mcpSession.Event += (_, e) => { if (e is SessionEvent.TurnSettled) settled.TrySetResult(); };
                await mcpSession.SendAsync("你好");
                await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var mcpRequest = mcpCapturing.Requests[^1];
                checks.Check(mcpRequest.ToolNames.Contains("mcp"), "配置 MCP 服务器后会话声明 mcp 工具");
                checks.Check(mcpRequest.ToolDefinitions?.Any(d => d.Name == "mcp") == true, "mcp 工具携带参数 Schema");
                checks.Check(mcpRequest.Messages[0].Role == PureAgentRole.System &&
                             mcpRequest.Messages[0].Content.Contains("MCP 服务器"),
                    "系统提示注入 MCP 服务器目录");
                await mcpSession.DisposeAsync();
            }

            // ════════════════ 子代理：定义、read 快速模式与只读不变量 ════════════════
            var agentDir = Path.Combine(tempDir, ".fumo", "agents");
            Directory.CreateDirectory(agentDir);
            await File.WriteAllTextAsync(Path.Combine(agentDir, "reviewer.md"),
                "---\nname: reviewer\ndescription: 只读代码审查\ntools: read_file, grep_search\nmaxTurns: 5\ntimeoutSeconds: 45\n---\n你是只读审查子代理。");
            await File.WriteAllTextAsync(Path.Combine(agentDir, "sneaky.md"),
                "---\nname: sneaky\ndescription: read 模式但声明了写工具\ntools: write_file, read_file\n---\n尝试写入。");
            var agents = AgentRegistry.Discover(tempDir, Path.Combine(tempDir, "agent-home"));
            checks.Check(agents.TryGet("explore", out var exploreAgent) &&
                         exploreAgent.Mode == SubagentMode.Read &&
                         exploreAgent.Tools.Contains("read_file") && !exploreAgent.Tools.Contains("run_command"),
                "内置 explore 是只读 read 模式");
            checks.Check(agents.TryGet("general", out var generalAgent) &&
                         generalAgent.Mode == SubagentMode.Patch &&
                         generalAgent.Tools.Contains("write_file"),
                "内置 general 是 patch 模式");
            checks.Check(agents.TryGet("reviewer", out var reviewerAgent) &&
                         reviewerAgent.Tools.SequenceEqual(new[] { "read_file", "grep_search" }) &&
                         reviewerAgent.MaxTurns == 5 && reviewerAgent.TimeoutSeconds == 45 &&
                         reviewerAgent.Mode == SubagentMode.Read,
                "发现 Markdown 子代理定义并解析工具白名单、轮次、超时与模式");
            checks.Check(agents.BuildPromptSection().Contains("explore") &&
                         agents.BuildPromptSection().Contains("reviewer") &&
                         agents.BuildPromptSection().Contains("read"),
                "系统提示目录列出子代理与模式");

            // ════════════════ 子代理：read 模式只读、快速返回 ════════════════
            var recording = new RecordingDispatcher();
            var runCommandCall = new PureToolCall("sc1", "run_command", "{\"command\":\"echo hi\"}");
            var exploreRunner = new SubagentRunner(agents,
                new FixedToolCallClient(runCommandCall, "子代理完成"), recording, "m");
            var exploreResult = await exploreRunner.RunAsync("explore", "检查项目", null, CancellationToken.None);
            checks.Check(exploreResult.Success && exploreResult.Mode == SubagentMode.Read &&
                         exploreResult.Content.Contains("子代理完成") && exploreResult.DurationMs >= 0,
                "read 子代理返回最终文本、模式与耗时");
            checks.Check(recording.Calls.Count == 0,
                "explore 子代理的越权 run_command 被工具白名单拦截");

            var sneakyCall = new PureToolCall("sc2", "write_file",
                "{\"path\":\"sneaky.txt\",\"content\":\"x\"}");
            var sneakyRecording = new RecordingDispatcher();
            var sneakyRunner = new SubagentRunner(agents,
                new FixedToolCallClient(sneakyCall, "尝试完成"), sneakyRecording, "m", tempDir);
            var sneakyResult = await sneakyRunner.RunAsync("sneaky", "写文件", null, CancellationToken.None);
            checks.Check(sneakyResult.Success && sneakyResult.Mode == SubagentMode.Read &&
                         sneakyRecording.Calls.Count == 0 && !File.Exists(Path.Combine(tempDir, "sneaky.txt")),
                "read 模式强制过滤写工具，不因定义声明而放开");

            // ════════════════ 子代理：工具入口与会话声明 ════════════════
            var subagentDispatcher = new SubagentDispatcher(new RecordingDispatcher(), exploreRunner);
            var subagentList = await subagentDispatcher.DispatchAsync(new PureToolCall("sa1", "subagent", "{}"));
            checks.Check(subagentList.Success && subagentList.Content.Contains("explore") &&
                         subagentList.Content.Contains("[read]"),
                "subagent 工具缺省 prompt 时列出可用子代理与模式");
            var subagentCall = await subagentDispatcher.DispatchAsync(new PureToolCall("sa2", "subagent",
                "{\"agent\":\"explore\",\"prompt\":\"查找入口\"}"));
            checks.Check(subagentCall.Success && subagentCall.Content.Contains("[子代理 explore · read") &&
                         subagentCall.Content.Contains("faster") &&
                         subagentCall.Content.Contains("子代理完成"),
                "subagent 工具调用返回带模式与档位标记的结论");

            checks.Check(NativeSubagentContract.Definition.Name == "subagent" &&
                         NativeSubagentContract.Definition.ParametersJsonSchema.Contains("prompt"),
                "Subagent 插件提供统一 subagent 工具契约");

            // context 预取：父会话已确认的线索必须进入子代理任务，避免子代理从零发现。
            var contextCapturing = new CapturingTextClient("线索完成");
            var contextRunner = new SubagentRunner(
                AgentRegistry.Discover(tempDir, Path.Combine(tempDir, "agent-home")),
                contextCapturing, new RecordingDispatcher(), "m", tempDir);
            var contextDispatcher = new SubagentDispatcher(new RecordingDispatcher(), contextRunner);
            await contextDispatcher.DispatchAsync(new PureToolCall("sa_ctx", "subagent",
                "{\"agent\":\"explore\",\"prompt\":\"定位入口\",\"context\":\"入口在 src/Program.cs:42\"}"));
            checks.Check(contextCapturing.Requests[0].Messages[^1].Content.Contains("src/Program.cs:42"),
                "subagent 的 context 参数被注入子代理任务说明");

            // 单次运行内同一只读调用只应真正下沉到调度器一次。
            var dedupRecording = new RecordingDispatcher();
            var dedupRunner = new SubagentRunner(
                AgentRegistry.Discover(tempDir, Path.Combine(tempDir, "agent-home")),
                new RepeatingToolCallClient(
                    new PureToolCall("dup1", "read_file", "{\"path\":\"sample.txt\"}"), rounds: 3, "去重完成"),
                dedupRecording, "m", tempDir);
            var dedupResult = await dedupRunner.RunAsync("explore", "重复读同一文件", null,
                CancellationToken.None);
            checks.Check(dedupResult.Success && dedupRecording.Calls.Count == 1,
                "子代理同一运行内重复的只读调用命中缓存，只真正执行一次");
            checks.Check(dedupResult.ToolCalls >= 3,
                "重复调用仍然计入子代理的工具调用次数（便于观察去重效果）");

            // ════════════════ 轮次预算：临近上限时提示，最后一轮禁用工具 ════════════════
            var budgetClient = new RecordingToolCallClient(
                new PureToolCall("budget1", "read_file", "{\"path\":\"sample.txt\"}"));
            var budgetDispatcher = new RecordingDispatcher();
            var budgetLoop = new NativeAgentLoop(budgetClient, budgetDispatcher, maxIterations: 4);
            var budgetHistory = new List<PureAgentMessage>
            {
                new(PureAgentRole.User, "一直读文件直到被切断"),
            };
            var budgetResult = await budgetLoop.ExecuteLoopAsync(budgetHistory, new[] { "read_file" }, "m");

            checks.Check(!budgetHistory.Any(m => m.Content.Contains("[预算提示]")),
                "显式轮次预算不向模型注入伪造的用户预算提示");
            checks.Check(!budgetClient.LastRequestHadNoTools,
                "显式预算的最后一轮仍保留工具，不能人为禁止模型完成任务");
            checks.Check(budgetHistory[^1].Role == PureAgentRole.Assistant &&
                         budgetHistory[^1].Content.Contains("外部任务轮次预算已耗尽"),
                "显式预算耗尽时给出可解释的预算终态");
            checks.Check(budgetDispatcher.Calls.Count == 4,
                "显式预算允许预算内的每一轮工具调用");
            checks.Check(budgetResult.Truncated,
                "显式预算耗尽标记为 Truncated，调用方不会把半成品当完整结论");
            checks.Equal(budgetHistory[0].Content, "一直读文件直到被切断",
                "预算提示不挤占用户原始请求的位置");
            checks.Check(NativeSubagentContract.Definition.ParametersJsonSchema.Contains("context") &&
                         NativeSubagentContract.Definition.ParametersJsonSchema.Contains("action"),
                "subagent 契约声明 context 与 action，父/子两层参数一致");

            // ════════════════ 权限模式穿透调度链 ════════════════
            await using (var chainMcp = McpManager.Create(Array.Empty<McpServerConfig>(), mcpHome))
            {
                var chained = new McpGatewayDispatcher(
                    new SkillAwareDispatcher(new SubagentDispatcher(dispatcher, exploreRunner), skills), chainMcp);
                var chainedEngine = new PureCSharpAgentEngine(chained, new FixedTextClient("x"));
                chainedEngine.Mode = PermissionMode.Plan;
                checks.Equal(PermissionMode.Default, dispatcher.Mode,
                    "Subagent 插件未安装时不改变真实调度器权限链");
                chainedEngine.Mode = PermissionMode.Default;
            }

            // ════════════════ 子代理：结构化输出 / fork / 嵌套 / 能力工具 ════════════════
            await File.WriteAllTextAsync(Path.Combine(agentDir, "structured.md"),
                "---\nname: structured\n" +
                "description: 返回结构化结论\n" +
                "tools: read_file\n" +
                "outputSchema: {\"type\":\"object\",\"required\":[\"answer\"]}\n---\n只返回 JSON。");
            var structuredAgents = AgentRegistry.Discover(tempDir, Path.Combine(tempDir, "agent-home"));

            var structuredRunner = new SubagentRunner(structuredAgents,
                new FixedTextClient("{\"answer\":\"ok\"}"), recording, "m");
            var structuredResult = await structuredRunner.RunAsync("structured", "给结论", null,
                CancellationToken.None);
            checks.Check(structuredResult.Success && structuredResult.StructuredOutput == "{\"answer\":\"ok\"}",
                "outputSchema 校验通过并返回结构化结果");

            var badSchemaRunner = new SubagentRunner(structuredAgents,
                new FixedTextClient("not json"), recording, "m");
            var badSchemaResult = await badSchemaRunner.RunAsync("structured", "给结论", null,
                CancellationToken.None);
            checks.Check(!badSchemaResult.Success && badSchemaResult.Error?.Contains("JSON") == true,
                "不符合 outputSchema 的子代理结果明确失败");

            var forkCapturing = new CapturingTextClient("fork 完成");
            var forkRunner = new SubagentRunner(AgentRegistry.Discover(tempDir, Path.Combine(tempDir, "agent-home")),
                forkCapturing, recording, "m", tempDir, historyProvider: () => new[]
                {
                    new PureAgentMessage(PureAgentRole.User, "父会话历史标记"),
                    new PureAgentMessage(PureAgentRole.Assistant, "父会话答复")
                });
            var forkResult = await forkRunner.RunAsync("explore", "继续分析", null, CancellationToken.None, fork: true);
            checks.Check(forkResult.Success &&
                         forkCapturing.Requests[0].Messages.Any(m => m.Content.Contains("父会话历史标记")),
                "fork 子代理继承父会话最近历史");
            checks.Check(forkCapturing.Requests[0].ReasoningEffort is null,
                "read 子代理不强制低推理强度，沿用模型默认");

            await File.WriteAllTextAsync(Path.Combine(agentDir, "lead.md"),
                "---\nname: lead\ndescription: 嵌套探索\ntools: read_file, subagent\nmode: read\n---\n组织探索。");
            var nestedProgress = new List<SubagentProgress>();
            var nestedCall = new PureToolCall("nest1", "subagent",
                "{\"agent\":\"explore\",\"prompt\":\"再派一个\"}");
            var nestedRunner = new SubagentRunner(AgentRegistry.Discover(tempDir, Path.Combine(tempDir, "agent-home")),
                new FixedToolCallClient(nestedCall, "嵌套完成"), new RecordingDispatcher(), "m", tempDir,
                depth: 0, maxDepth: 1)
            {
                Progress = nestedProgress.Add
            };
            nestedRunner.SetTier(SubagentTier.Stronger);
            var nestedResult = await nestedRunner.RunAsync("lead", "开始", null, CancellationToken.None);
            checks.Check(nestedResult.Success &&
                         nestedProgress.Count(p => p.Kind == "started") >= 2,
                "read 子代理在 maxDepth 内可继续派生只读子代理");
            checks.Equal(SubagentTier.Stronger, nestedResult.Tier, "子代理结果标注实际档位");

            // stronger 必须对内置 explore 开箱生效：旧实现要求代理定义显式声明 subagent，
            // 内置代理都没有声明，导致切换档位没有任何实际效果。
            var builtinNestedCapturing = new CapturingTextClient("内置嵌套完成");
            var builtinNestedRunner = new SubagentRunner(
                AgentRegistry.Discover(tempDir, Path.Combine(tempDir, "agent-home")),
                builtinNestedCapturing, new RecordingDispatcher(), "m", tempDir, depth: 0, maxDepth: 1);
            builtinNestedRunner.SetTier(SubagentTier.Stronger);
            await builtinNestedRunner.RunAsync("explore", "检查入口", null, CancellationToken.None);
            checks.Check(builtinNestedCapturing.Requests[0].ToolNames.Contains("subagent"),
                "stronger 档位下内置 explore 自动获得嵌套派发能力");

            var fasterNestedCapturing = new CapturingTextClient("faster 完成");
            var fasterNestedRunner = new SubagentRunner(
                AgentRegistry.Discover(tempDir, Path.Combine(tempDir, "agent-home")),
                fasterNestedCapturing, new RecordingDispatcher(), "m", tempDir, depth: 0, maxDepth: 1);
            fasterNestedRunner.SetTier(SubagentTier.Faster);
            await fasterNestedRunner.RunAsync("explore", "检查入口", null, CancellationToken.None);
            checks.Check(!fasterNestedCapturing.Requests[0].ToolNames.Contains("subagent"),
                "faster 档位不声明嵌套 subagent，保持轻量");

            // 轮次耗尽必须如实标注，不能把半成品当完整交付。
            var truncatedCall = new PureToolCall("trunc1", "read_file", "{\"path\":\"sample.txt\"}");
            var truncatedAgentDir = Path.Combine(tempDir, ".fumo", "agents");
            await File.WriteAllTextAsync(Path.Combine(truncatedAgentDir, "tiny.md"),
                "---\nname: tiny\ndescription: 单轮\ntools: read_file\nmaxTurns: 1\n---\n只读。");
            var tinyRunner = new SubagentRunner(
                AgentRegistry.Discover(tempDir, Path.Combine(tempDir, "agent-home")),
                new FixedToolCallClient(truncatedCall, "结论"), new RecordingDispatcher(), "m", tempDir);
            var tinyResult = await tinyRunner.RunAsync("tiny", "读取", null, CancellationToken.None);
            checks.Check(tinyResult.Truncated || tinyResult.Content.Contains("最大轮次"),
                "达到轮次上限的子代理结果被标注为可能不完整");

            var capabilityAgentDir = Path.Combine(tempDir, ".fumo", "agents");
            await File.WriteAllTextAsync(Path.Combine(capabilityAgentDir, "plugin-user.md"),
                "---\nname: plugin-user\ndescription: 使用插件工具\ntools: browser_click\n---\n调用插件工具。");
            var pluginCapabilityAgent = AgentRegistry.Discover(tempDir, Path.Combine(tempDir, "agent-home"));
            var capabilityRecording = new RecordingDispatcher();
            var capabilityDefs = new[]
            {
                new PureToolDefinition("browser_click", "点击网页元素", "{\"type\":\"object\"}")
            };
            var capabilityRunner = new SubagentRunner(pluginCapabilityAgent,
                new FixedToolCallClient(new PureToolCall("cap1", "browser_click", "{}"), "插件完成"),
                capabilityRecording, "m", tempDir, capabilityDefs);
            var capabilityResult = await capabilityRunner.RunAsync("plugin-user", "点一下", null,
                CancellationToken.None);
            checks.Check(capabilityResult.Success && capabilityRecording.Calls.Count == 1,
                "read 子代理可按定义使用显式声明的能力工具");

            // ════════════════ 子代理：patch 模式在隔离 worktree 中返回补丁 ════════════════
            var gitRepo = Path.Combine(tempDir, "wt-repo");
            Directory.CreateDirectory(gitRepo);
            var gitOk = RunGit(gitRepo, "init") && RunGit(gitRepo, "config user.email test@example.com") &&
                        RunGit(gitRepo, "config user.name Test");
            if (gitOk)
            {
                await File.WriteAllTextAsync(Path.Combine(gitRepo, "base.txt"), "base");
                gitOk = RunGit(gitRepo, "add .") && RunGit(gitRepo, "commit -m init");
            }
            if (gitOk)
            {
                var readInRepo = new SubagentRunner(agents, new FixedTextClient("只读完成"),
                    new ProductionToolDispatcher(gitRepo), "m", gitRepo);
                var readRepoResult = await readInRepo.RunAsync("explore", "只读检查", null,
                    CancellationToken.None);
                var worktreeStore = Path.Combine(gitRepo, ".git", "worktrees");
                checks.Check(readRepoResult.Success && readRepoResult.Mode == SubagentMode.Read &&
                             readRepoResult.Patch is null &&
                             (!Directory.Exists(worktreeStore) ||
                              Directory.GetDirectories(worktreeStore).Length == 0),
                    "read 子代理在 git 工作区中也不创建 worktree");

                await File.WriteAllTextAsync(Path.Combine(tempDir, ".fumo", "agents", "writer.md"),
                    "---\nname: writer\ndescription: 隔离写入\ntools: write_file, read_file\nmode: patch\n---\n写入文件。");
                var writerAgents = AgentRegistry.Discover(tempDir, Path.Combine(tempDir, "agent-home"));
                checks.Check(writerAgents.TryGet("writer", out var writerDefinition) &&
                             writerDefinition.Mode == SubagentMode.Patch,
                    "Markdown 定义可声明 patch 模式");
                var wtWriteCall = new PureToolCall("wt1", "write_file",
                    "{\"path\":\"new.txt\",\"content\":\"isolated\"}");
                var writerRunner = new SubagentRunner(writerAgents,
                    new FixedToolCallClient(wtWriteCall, "写入完成"), new ProductionToolDispatcher(gitRepo),
                    "m", gitRepo);
                var wtResult = await writerRunner.RunAsync("writer", "写入新文件", null, CancellationToken.None);
                checks.Check(wtResult.Success && wtResult.Mode == SubagentMode.Patch &&
                             wtResult.Patch?.Contains("new.txt") == true &&
                             wtResult.ChangedFiles?.Contains("new.txt") == true &&
                             wtResult.BaseHead is { Length: > 0 } &&
                             wtResult.PatchPath is { Length: > 0 } patchFile && File.Exists(patchFile),
                    "patch 子代理返回 unified diff、补丁文件、改动文件与基线 HEAD");
                checks.Check(!File.Exists(Path.Combine(gitRepo, "new.txt")),
                    "patch 子代理的改动未写入父工作区");

                var cleanRunner = new SubagentRunner(writerAgents, new FixedTextClient("无改动"),
                    new ProductionToolDispatcher(gitRepo), "m", gitRepo);
                var cleanResult = await cleanRunner.RunAsync("writer", "只读思考", null, CancellationToken.None);
                checks.Check(cleanResult.Success && cleanResult.Patch is null &&
                             cleanResult.ChangedFiles is { Count: 0 },
                    "无改动的 patch 子代理返回空补丁并清理 worktree");

                var generalRunner = new SubagentRunner(writerAgents,
                    new FixedToolCallClient(runCommandCall, "通用子代理完成"),
                    new ProductionToolDispatcher(gitRepo), "m", gitRepo);
                var generalResult = await generalRunner.RunAsync("general", "执行命令", null,
                    CancellationToken.None);
                checks.Check(generalResult.Success && generalResult.Mode == SubagentMode.Patch &&
                             generalResult.Patch is null,
                    "patch 子代理允许 run_command 且无文件改动时返回空补丁");

                var planRunner = new SubagentRunner(writerAgents, new FixedTextClient("不应执行"),
                    new ProductionToolDispatcher(gitRepo), "m", gitRepo)
                {
                    ParentMode = () => PermissionMode.Plan
                };
                var planResult = await planRunner.RunAsync("writer", "写文件", null, CancellationToken.None);
                checks.Check(!planResult.Success && planResult.Error?.Contains("Plan 模式") == true,
                    "Plan 模式拒绝 patch 子代理，只允许 read");
                checks.Check(ProductionToolDispatcher.IsConcurrencySafe(SubagentDispatcher.ToolName),
                    "subagent 工具可并行派发（read 只读、patch 独立 worktree）");

                var patchDispatcher = new SubagentDispatcher(new RecordingDispatcher(), writerRunner);
                var mismatch = await patchDispatcher.DispatchAsync(new PureToolCall("sa3", "subagent",
                    "{\"agent\":\"explore\",\"mode\":\"patch\",\"prompt\":\"写\"}"));
                checks.Check(!mismatch.Success && mismatch.Content.Contains("read 模式"),
                    "显式 mode 与代理定义冲突时明确拒绝");
            }

            // ════════════════ 子代理：会话进度事件 ════════════════
            var progressEvents = new List<SubagentProgress>();
            var progressRunner = new SubagentRunner(AgentRegistry.Discover(tempDir, Path.Combine(tempDir, "agent-home")),
                new FixedTextClient("进度结论"), new RecordingDispatcher(), "m", tempDir)
            {
                Progress = progressEvents.Add
            };
            await progressRunner.RunAsync("explore", "检查", null, CancellationToken.None);
            checks.Check(progressEvents.Any(e => e.Kind == "started") &&
                         progressEvents.Any(e => e.Kind == "completed"),
                "子代理进度上报开始与完成事件");

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

            // ════════════════ Plan 模式工具裁剪：不向模型声明必然被拒绝的工具 ════════════════
            var planCapturing = new CapturingTextClient("计划中");
            var planSession = new NativeCSharpChatSession(
                new PureCSharpAgentEngine(dispatcher, planCapturing), "plan-tools", "m");
            var planSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            planSession.Event += (_, e) => { if (e is SessionEvent.TurnSettled) planSettled.TrySetResult(); };
            await planSession.SetExecutionModeAsync("plan");
            await planSession.SendAsync("先规划，不要改动");
            await planSettled.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var planRequest = planCapturing.Requests[^1];
            checks.Check(!planRequest.ToolNames.Contains("write_file") &&
                         !planRequest.ToolNames.Contains("edit_file") &&
                         !planRequest.ToolNames.Contains("run_command"),
                "Plan 模式不声明写文件与命令工具");
            checks.Check(planRequest.ToolNames.Contains("read_file") &&
                         planRequest.ToolNames.Contains("grep_search") &&
                         planRequest.ToolNames.Contains("update_plan"),
                "Plan 模式仍声明只读工具与 update_plan");
            checks.Check(planRequest.ToolDefinitions?.Any(d => d.Name == "write_file") != true,
                "Plan 模式的工具 Schema 与工具名同步裁剪");
            await planSession.DisposeAsync();

            // ════════════════ 工具 Schema 解析复用与非法 Schema 诊断 ════════════════
            var schemaOne = OpenAiCompatibleModelClient.ParseToolSchema("t1",
                "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}");
            var schemaTwo = OpenAiCompatibleModelClient.ParseToolSchema("t1",
                "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}");
            checks.Equal(schemaOne.GetRawText(), schemaTwo.GetRawText(),
                "同一 Schema 文本重复解析结果一致（可安全复用）");
            checks.Check(schemaOne.GetProperty("properties").TryGetProperty("a", out _),
                "解析后的 Schema 保留参数结构而不是被抹平");
            var invalidSchema = OpenAiCompatibleModelClient.ParseToolSchema("broken", "{not json");
            checks.Check(invalidSchema.ValueKind == JsonValueKind.Object &&
                         invalidSchema.GetProperty("type").GetString() == "object",
                "非法 Schema 退化为宽松对象，插件工具仍可调度");

            // ════════════════ 压缩：保留原始任务陈述、最近窗口与文件线索 ════════════════
            var compactCapturing = new CapturingTextClient("## 1. 用户目标与硬约束\n实现图引擎");
            var planDispatcher = new ProductionToolDispatcher(tempDir);
            await planDispatcher.DispatchAsync(new PureToolCall("plan_keep", "update_plan",
                "{\"steps\":[{\"title\":\"实现图引擎\",\"status\":\"in_progress\"},{\"title\":\"运行测试\",\"status\":\"pending\"}]}"));
            var compactEngine2 = new PureCSharpAgentEngine(planDispatcher, compactCapturing)
            {
                ContextLengthResolver = (_, _) => Task.FromResult<long?>(10_000),
            };
            var pinnedTask = new PureAgentMessage(PureAgentRole.User,
                "请在 src/graph 下实现图引擎，必须使用 Dart，禁止引入第三方依赖，并运行全部测试。");
            var history2 = new List<PureAgentMessage> { new(PureAgentRole.System, "系统设定"), pinnedTask };
            for (var i = 0; i < 40; i++)
            {
                history2.Add(new PureAgentMessage(PureAgentRole.Assistant,
                    $"读取 src/graph/node_{i}.dart 并确认接口。",
                    ToolCalls: new[]
                    {
                        new PureToolCall($"call_{i}", "read_file", $"{{\"path\":\"src/graph/node_{i}.dart\"}}")
                    }));
                history2.Add(new PureAgentMessage(PureAgentRole.Tool, new string('x', 12_000),
                    ToolCallId: $"call_{i}"));
            }
            history2.Add(new PureAgentMessage(PureAgentRole.Assistant, "最近一条助手消息"));
            compactEngine2.RestoreHistory(history2);
            await compactEngine2.RunTurnAsync(new PureModelRequest(
                new[] { new PureAgentMessage(PureAgentRole.User, "继续") }, Array.Empty<string>()));

            var compacted2 = compactEngine2.GetState().History!;
            checks.Equal(PureAgentRole.System, compacted2[0].Role, "压缩后 System 仍在首位");
            checks.Check(compacted2.Any(m => ReferenceEquals(m, pinnedTask) || m.Content == pinnedTask.Content),
                "压缩逐字保留原始任务陈述（用户硬约束不交给摘要改写）");
            checks.Check(compacted2.Any(m => m.Content.Contains("上下文摘要")),
                "压缩插入结构化摘要");
            checks.Check(compacted2.Any(m => m.Content == "最近一条助手消息"),
                "压缩保留最近消息");
            checks.Check(compacted2.Any(m => m.Content.Contains("压缩时保留的计划快照") &&
                                             m.Content.Contains("实现图引擎")),
                "压缩原样保留结构化计划，模型不会忘记剩余步骤");
            var summarizeRequest = compactCapturing.Requests[0];
            checks.Check(summarizeRequest.Messages[0].Content.Contains("## 5. 验证状态") &&
                         summarizeRequest.Messages[0].Content.Contains("## 7. 关键事实"),
                "压缩摘要提示要求验证状态与关键事实");
            checks.Check(summarizeRequest.Messages[^1].Content.Contains("涉及文件") &&
                         summarizeRequest.Messages[^1].Content.Contains("node_"),
                "压缩转录附带从工具调用提取的文件线索");

            // 状态台账是确定性事实，不依赖摘要模型，必须作为独立消息保留。
            checks.Check(compacted2.Any(m => m.Content.Contains("[压缩时保留的状态台账]") &&
                                             m.Content.Contains("node_")),
                "压缩生成确定性状态台账（已改文件不依赖摘要模型）");

            // ════════════════ 多轮压缩：原始约束在连续两次压缩后仍可召回 ════════════════
            var round2Capturing = new CapturingTextClient("## 1. 用户目标与硬约束\n继续实现");
            var multiCompact = new PureCSharpAgentEngine(dispatcher, round2Capturing);
            multiCompact.RestoreHistory(compacted2.ToList());
            for (var i = 0; i < 40; i++)
            {
                await multiCompact.RunTurnAsync(new PureModelRequest(
                    new[] { new PureAgentMessage(PureAgentRole.User, new string('y', 20_000) + i) },
                    Array.Empty<string>()));
            }

            var twiceCompacted = multiCompact.GetState().History!;
            checks.Check(twiceCompacted.Count(m => m.Role == PureAgentRole.System) == 1,
                "连续多轮压缩后 System 仍只有一条");
            checks.Check(twiceCompacted.Any(m => m.Content.Contains("禁止引入第三方依赖")),
                "连续多轮压缩后原始任务硬约束仍逐字保留（约束不随摘要衰减）");
        }
        finally
        {
            // git worktree 元数据在 Windows 上可能是只读的；清理失败不影响断言结果。
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    private static bool RunGit(string workingDirectory, string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
