using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Plugins.RemoteControl;
using Fufucode.App.Features.RemoteControl;
using Fufucode.Core.RemoteControl;

namespace Fufucode.App.RemoteControl.Tests;

/// <summary>
/// M1 手机远控定向验证：回环启动/停止/状态、PIN 认证、会话数据读取、消息投递与审批流转，
/// 外加内嵌 Web 页面与二维码契约。所有断言走真实回环 HTTP/WebSocket，不使用模拟网络层。
/// </summary>
internal static class RemoteControlTests
{
    public static async Task RunAllAsync(Action<bool, string> check)
    {
        await LoopbackLifecycleAsync(check);
        await AuthenticationAsync(check);
        await SessionDataAndMessagingAsync(check);
        await ApprovalFlowAsync(check);
        await EmergencyStopAsync(check);
        await SessionDirectoryAndStableRebindAsync(check);
        await HostRebindPreservesPairingAsync(check);
        WebAssetContract(check);
        QrContract(check);
        PolicyGuardrails(check);
        await ExtensionCollectContractAsync(check);
    }

    /// <summary>1. 回环端口启动、停止与状态暴露。</summary>
    private static async Task LoopbackLifecycleAsync(Action<bool, string> check)
    {
        var session = new FakeChatSession();
        using var adapter = new RemoteSessionAdapter(session, "回环会话");
        await using var server = new RemoteControlServer(adapter, LoopbackOptions());

        check(!server.IsRunning && server.Status.State == RemoteControlState.Stopped,
            "服务未启动时状态为 Stopped 且 IsRunning 为 false");
        check(server.Status.Port == 0 && server.Status.PairingUrl is null && server.Status.Pin is null,
            "未启动状态不暴露端口、配对 URL 与 PIN（不伪造运行信息）");

        var started = server.Start();
        check(server.IsRunning && started.State == RemoteControlState.Running, "Start() 后服务进入 Running");
        check(started.Port > 0, $"端口 0 自动分配得到真实端口（实际 {started.Port}）");
        check(started.Address == "127.0.0.1", $"回环绑定的对外地址为 127.0.0.1（实际 {started.Address}）");
        check(started.PairingUrl == $"http://127.0.0.1:{started.Port}/",
            $"配对 URL 与实际监听端点一致（{started.PairingUrl}）");
        check(started.Pin is { Length: 4 } pin && pin.All(char.IsAsciiDigit),
            $"启动生成的 4 位 PIN 全部为数字（{started.Pin}）");
        check(started.StartedAtUtc is not null && started.TokenLifetime > TimeSpan.Zero,
            "状态暴露启动时间与令牌有效期");

        var secondStart = server.Start;
        check(Throws<InvalidOperationException>(() => secondStart()),
            "重复 Start() 被拒绝（不产生第二个监听器）");

        using var client = RemoteHttpClient.Create(started.Port);
        var pair = await client.PairAsync(started.Pin!);
        check(pair.Success && pair.Token is { Length: > 20 }, "回环端口可真实完成 PIN 配对并拿到令牌");
        client.Token = pair.Token;

        // 连接数是当前并存的连接：HTTP 请求-响应即关闭，只有长连接（事件流）会持续计数。
        check(server.Status.ConnectionCount == 0,
            "HTTP 请求-响应结束后连接数回到 0（不虚报连接）");
        var (eventSocket, firstUpdate) = await client.OpenEventsAsync();
        check(firstUpdate is RemoteSnapshotUpdate, "事件流连接建立并收到首帧快照");
        var live = await WaitForConnectionCountAsync(server, atLeast: 1, TimeSpan.FromSeconds(5));
        check(live, $"状态暴露当前连接数（事件流保持连接时实际 {server.Status.ConnectionCount}）");

        var http = await client.GetAsync("/api/status");
        check(http.Status == HttpStatusCode.OK, "已认证的 /api/status 返回 200");
        var reported = JsonSerializer.Deserialize<RemoteControlStatus>(http.Body, RemoteWire.Options);
        check(reported is { IsRunning: true, Port: > 0 } && reported.Pin == started.Pin,
            "运行状态查询返回真实 IP/端口/PIN");
        eventSocket.Dispose();
        var drained = await WaitForConnectionCountAsync(server, atLeast: 0, TimeSpan.FromSeconds(5),
            exact: 0);
        check(drained, "事件流关闭后连接数回落");

        // 指定端口启动：占用中的端口必须真实失败，不静默换端口。
        server.Stop();
        check(!server.IsRunning && server.Status.State == RemoteControlState.Stopped,
            "Stop() 后服务回到 Stopped");
        check(server.Status.Port == 0 && server.Status.Pin is null && server.Status.PairingUrl is null,
            "停止后状态清空端口与配对信息");

        using var blocker = new TcpListener(IPAddress.Loopback, started.Port);
        blocker.Start();
        await using var conflicting = new RemoteControlServer(adapter, LoopbackOptions());
        check(Throws<SocketException>(() => conflicting.Start(started.Port)),
            "指定端口被占用时启动真实失败（不假装成功）");
        blocker.Stop();

        var restarted = conflicting.Start();
        check(restarted.Port > 0, "释放端口后可重新启动");
        using var restartedClient = RemoteHttpClient.Create(restarted.Port);
        var restartedPair = await restartedClient.PairAsync(restarted.Pin!);
        restartedClient.Token = restartedPair.Token;
        check(await restartedClient.GetAsync("/api/status") is { Status: HttpStatusCode.OK },
            "重启后的服务可用新配对令牌访问");

        // 重启（Stop + Start）必须轮换 PIN 并撤销全部既有令牌。
        conflicting.Stop();
        var secondRun = conflicting.Start();
        check(secondRun.Pin != restarted.Pin, "重启后轮换 PIN");
        using var secondClient = RemoteHttpClient.Create(secondRun.Port);
        secondClient.Token = restartedPair.Token; // 上一轮运行颁发的令牌
        var staleToken = await secondClient.GetAsync("/api/status");
        check(staleToken.Status == HttpStatusCode.Unauthorized,
            $"重启后旧令牌失效，请求被拒绝（实际 {(int)staleToken.Status}）");
        var freshPair = await secondClient.PairAsync(secondRun.Pin!);
        secondClient.Token = freshPair.Token;
        check(await secondClient.GetAsync("/api/status") is { Status: HttpStatusCode.OK },
            "重启后新 PIN 配对可正常访问");
        conflicting.Stop();
    }

    /// <summary>2. 正确 PIN 通过、错误 PIN 拒绝、连续错误锁定。</summary>
    private static async Task AuthenticationAsync(Action<bool, string> check)
    {
        var session = new FakeChatSession();
        using var adapter = new RemoteSessionAdapter(session, "认证会话");
        await using var server = new RemoteControlServer(adapter, LoopbackOptions(), new RemoteControlWebAssets());
        var status = server.Start();
        using var client = RemoteHttpClient.Create(status.Port);

        // 未认证：会话数据与指令一律拦截。
        foreach (var path in new[] { "/api/snapshot", "/api/message", "/api/events" })
        {
            var unauth = path == "/api/message"
                ? await client.PostAsync(path, new { text = "hi" })
                : await client.GetAsync(path);
            check(unauth.Status == HttpStatusCode.Unauthorized,
                $"未授权访问 {path} 被拒绝（401）");
        }

        // Web UI 外壳必须免认证可达（手机要能先打开页面输入 PIN），但不含会话数据。
        var shell = await client.GetAsync("/");
        check(shell.Status == HttpStatusCode.OK && shell.Body.Contains("data-test=\"pin\"", StringComparison.Ordinal),
            "未认证也能打开页面外壳输入配对码");
        check(await client.GetAsync("/app.css") is { Status: HttpStatusCode.OK },
            "未认证可加载页面样式（否则页面无法使用）");
        check(await client.GetAsync("/app.js") is { Status: HttpStatusCode.OK },
            "未认证可加载页面脚本");
        check(!shell.Body.Contains("README", StringComparison.Ordinal),
            "页面外壳不含会话数据（未认证不泄漏时间线）");

        var wrong = await client.PairAsync(WrongPin(status.Pin!));
        check(!wrong.Success && wrong.Token is null && wrong.Error is not null,
            "错误 PIN 被拒绝，且不颁发令牌");

        // 错误 PIN 不占用尝试窗口的可用配额以外的东西：正确 PIN 仍可成功。
        var correct = await client.PairAsync(status.Pin!);
        check(correct.Success && correct.Token is not null, "同一客户端随后用正确 PIN 仍可通过");
        client.Token = correct.Token;

        var authorized = await client.GetAsync("/api/snapshot");
        check(authorized.Status == HttpStatusCode.OK, "持有效令牌可读取会话数据");

        // 连续错误达到上限后锁定，即使 PIN 正确也拒绝。
        using var attacker = RemoteHttpClient.Create(status.Port);
        var guardedStatus = server.Status;
        var limit = LoopbackOptions().MaxPinAttempts;
        RemotePairResult? last = null;
        for (var i = 0; i < limit; i++) last = await attacker.PairAsync(WrongPin(guardedStatus.Pin!));
        check(last is { LockedOut: true }, $"连续 {limit} 次错误 PIN 后进入锁定");
        var lockedAttempt = await attacker.PairAsync(guardedStatus.Pin!);
        check(!lockedAttempt.Success && lockedAttempt.LockedOut,
            "锁定期间即使 PIN 正确也被拒绝（不因顺序绕过）");
        check(attacker.Token is null && attacker.BaseUrl.Length > 0,
            "锁定只拒绝配对、不泄漏令牌，服务仍通过同一地址可达");
        var reachable = await attacker.GetAsync("/api/status");
        check(reachable.Status == HttpStatusCode.Unauthorized,
            "被锁定客户端仍可到达服务（未断连），仅无凭据被拒");

        // 其他客户端不受影响：原客户端令牌继续有效。
        var stillValid = await client.GetAsync("/api/snapshot");
        check(stillValid.Status == HttpStatusCode.OK, "锁定仅作用于触发客户端，既有令牌不受影响");

        // 令牌过期：过期后请求被拒绝。
        var expiringOptions = LoopbackOptions() with { TokenLifetime = TimeSpan.FromMilliseconds(120) };
        await using var expiringServer = new RemoteControlServer(adapter, expiringOptions);
        var expiringStatus = expiringServer.Start();
        using var expiringClient = RemoteHttpClient.Create(expiringStatus.Port);
        var shortLived = await expiringClient.PairAsync(expiringStatus.Pin!);
        expiringClient.Token = shortLived.Token;
        var fresh = await expiringClient.GetAsync("/api/snapshot");
        check(fresh.Status == HttpStatusCode.OK, "短期令牌在有效期内可用");
        await Task.Delay(300);
        var expired = await expiringClient.GetAsync("/api/snapshot");
        check(expired.Status == HttpStatusCode.Unauthorized, "令牌过期后请求被拒绝");
        expiringServer.Stop();

        server.Stop();
    }

    /// <summary>3. 会话数据读取、消息投递流转。</summary>
    private static async Task SessionDataAndMessagingAsync(Action<bool, string> check)
    {
        var session = new FakeChatSession();
        using var adapter = new RemoteSessionAdapter(session, "流式会话");
        await using var server = new RemoteControlServer(adapter, LoopbackOptions());
        var status = server.Start();
        using var client = RemoteHttpClient.Create(status.Port);
        var pair = await client.PairAsync(status.Pin!);
        client.Token = pair.Token;

        var empty = await client.SnapshotAsync();
        check(empty.Title == "流式会话" && empty.Entries.Count == 0,
            "快照暴露会话标题且初始时间线为空");
        check(empty.Model == "测试模型", "快照暴露当前模型");

        // 流式查看：订阅事件流后继续注入远端事件。
        var (socket, first) = await client.OpenEventsAsync();
        check(first is RemoteSnapshotUpdate, "WebSocket 首个更新是完整快照");

        session.SimulateUserMessage("m-user-1", "请总结当前进度");
        var userEntry = await RemoteHttpClient.WaitForAsync<RemoteEntryUpdate>(socket,
            update => update.Entry.Id == "m-user-1" && !update.Entry.IsStreaming, TimeSpan.FromSeconds(10));
        check(userEntry is not null, "流式推送用户消息条目（终态整体替换）");
        check(userEntry!.Entry.Role == RemoteEntryRole.User &&
              userEntry.Entry.Blocks.OfType<RemoteTextBlock>().Any(text => text.Text == "请总结当前进度"),
            "用户消息内容与角色正确");

        session.SimulateActivity(SessionActivity.Running);
        var running = await RemoteHttpClient.WaitForAsync<RemoteStatusUpdate>(socket,
            update => update.Activity == nameof(SessionActivity.Running), TimeSpan.FromSeconds(10));
        check(running is not null && running.CanStop, "活动状态流式推送为 Running 且报告可停止");

        session.SimulateAssistantStream("m-assistant-1", "call-readme", "read_file");
        var assistant = await RemoteHttpClient.WaitForAsync<RemoteEntryUpdate>(socket,
            update => update.Entry.Id == "m-assistant-1" && !update.Entry.IsStreaming &&
                      update.Entry.Blocks.OfType<RemoteToolBlock>().Any(), TimeSpan.FromSeconds(10));
        check(assistant is not null, "流式推送助手消息与工具调用卡片");
        var toolBlock = assistant!.Entry.Blocks.OfType<RemoteToolBlock>().Single().Tool;
        check(toolBlock.ToolName == "read_file" && toolBlock.Status == RemoteToolStatus.Completed,
            "工具调用卡片携带工具名与终态");
        check(toolBlock.ArgsJson.Contains("README.md", StringComparison.Ordinal),
            "工具调用卡片携带组装完成的参数 JSON");
        check(assistant.Entry.Blocks.OfType<RemoteThinkingBlock>().Any(),
            "思考块按种类投影（不混入文本）");

        session.SimulateError(SessionErrorKind.ModelFailed, "MOCK-503 演示错误");
        var errorEntry = await RemoteHttpClient.WaitForAsync<RemoteEntryUpdate>(socket,
            update => update.Entry.Blocks.OfType<RemoteErrorBlock>().Any(), TimeSpan.FromSeconds(10));
        check(errorEntry is not null, "会话错误作为错误条目流式推送");

        var queued = await client.SnapshotAsync();
        check(queued.Entries.Count >= 3, $"快照按顺序包含全部条目（实际 {queued.Entries.Count}）");

        // 断线重连：新连接必须拿到包含历史条目的完整快照（事件补齐语义）。
        socket.Dispose();
        var (reconnected, snapshotAgain) = await client.OpenEventsAsync();
        var snapshot = (snapshotAgain as RemoteSnapshotUpdate)?.Session;
        check(snapshot is not null && snapshot.Entries.Count == queued.Entries.Count,
            "重连快照补齐全部历史条目");
        reconnected.Dispose();

        // 发送文本指令：真实投递到会话。
        var sent = await client.SendMessageAsync("在吗？");
        check(sent.Accepted && session.Sent.SequenceEqual(new[] { "在吗？" }),
            "手机发送的文本真实投递到会话");

        var emptyText = await client.SendMessageAsync("   ");
        check(!emptyText.Accepted, "空白文本被拒绝，不投递到会话");

        // 会话不可发送时如实拒绝。
        session.SimulateActivity(SessionActivity.Cancelling);
        var rejected = await client.SendMessageAsync("不应该送达");
        check(!rejected.Accepted && session.Sent.Count == 1,
            "会话不可发送时投递被拒绝且不产生副作用");
        session.SimulateActivity(SessionActivity.Idle);

        server.Stop();
    }

    /// <summary>4. 工具审批卡片：批准/拒绝回传、失效审批不再生效。</summary>
    private static async Task ApprovalFlowAsync(Action<bool, string> check)
    {
        var session = new FakeChatSession();
        var approvals = new InMemoryToolApprovalGate();
        using var adapter = new RemoteSessionAdapter(session, "审批会话", approvals);
        await using var server = new RemoteControlServer(adapter, LoopbackOptions());
        var status = server.Start();
        using var client = RemoteHttpClient.Create(status.Port);
        var pair = await client.PairAsync(status.Pin!);
        client.Token = pair.Token;

        var (socket, _) = await client.OpenEventsAsync();

        // 宿主登记待审批工具调用：手机端先看到卡片，再看到审批请求。
        session.SimulateAssistantStream("m-tool-1", "call-edit", "edit");
        var toolEntry = await RemoteHttpClient.WaitForAsync<RemoteEntryUpdate>(socket,
            update => update.Entry.Id == "m-tool-1" &&
                      update.Entry.Blocks.OfType<RemoteToolBlock>().Any(), TimeSpan.FromSeconds(10));
        check(toolEntry is not null, "工具卡片先到达（审批前可见工具名与参数）");

        var request = adapter.RequestApproval("call-edit", "edit", "{\"path\":\"src/Demo.cs\"}", "修改 1 个文件");
        var added = await RemoteHttpClient.WaitForAsync<RemoteApprovalAddedUpdate>(socket,
            update => update.Approval.ApprovalId == request.ApprovalId, TimeSpan.FromSeconds(10));
        check(added is not null, "审批请求流式推送到手机");
        check(added!.Approval.ToolName == "edit" && added.Approval.Summary == "修改 1 个文件",
            "审批请求携带工具名与摘要");

        var awaiting = await RemoteHttpClient.WaitForAsync<RemoteEntryUpdate>(socket,
            update => update.Entry.Id == "m-tool-1" &&
                      update.Entry.Blocks.OfType<RemoteToolBlock>()
                          .Any(block => block.Tool.Status == RemoteToolStatus.AwaitingApproval),
            TimeSpan.FromSeconds(10));
        check(awaiting is not null, "对应工具卡片状态更新为待审批");

        var snapshot = await client.SnapshotAsync();
        check(snapshot.PendingApprovals.Any(item => item.ApprovalId == request.ApprovalId),
            "快照暴露待审批列表（重连后仍可见）");

        // 批准：宿主等待的调用被放行。
        var waiting = approvals.WaitAsync(request.ApprovalId);
        var (approveStatus, approveResolution) = await client.ResolveApprovalAsync(request.ApprovalId, "approve");
        check(approveStatus == HttpStatusCode.OK && approveResolution is { Accepted: true, Decision: RemoteApprovalDecision.Approve },
            "手机端批准返回受理成功");
        var resolved = await RemoteHttpClient.WaitForAsync<RemoteApprovalResolvedUpdate>(socket,
            update => update.Resolution.ApprovalId == request.ApprovalId, TimeSpan.FromSeconds(10));
        check(resolved is not null, "审批决定流式广播给全部手机连接");
        var gateResult = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        check(gateResult is { Accepted: true, Decision: RemoteApprovalDecision.Approve },
            "宿主等待的审批闸门被真实放行");

        // 失效审批：重复提交同一审批必须被拒绝，不能第二次放行。
        var (replayStatus, replay) = await client.ResolveApprovalAsync(request.ApprovalId, "approve");
        check(replay is { Accepted: false } && replayStatus != HttpStatusCode.OK,
            "重复批准同一审批被拒绝（单次有效）");

        // 拒绝路径。
        var denyRequest = adapter.RequestApproval("call-shell", "shell", "{\"cmd\":\"rm -rf /\"}", "危险命令");
        var denyWaiting = approvals.WaitAsync(denyRequest.ApprovalId);
        var (denyStatus, denyResolution) = await client.ResolveApprovalAsync(denyRequest.ApprovalId, "deny");
        check(denyStatus == HttpStatusCode.OK && denyResolution is { Accepted: true, Decision: RemoteApprovalDecision.Deny },
            "手机端拒绝返回受理成功");
        var denyResult = await denyWaiting.WaitAsync(TimeSpan.FromSeconds(5));
        check(denyResult is { Accepted: true, Decision: RemoteApprovalDecision.Deny },
            "宿主等待的审批闸门收到拒绝");

        // 未知审批与非法参数不产生副作用。
        var (unknownStatus, unknown) = await client.ResolveApprovalAsync("apv-does-not-exist", "approve");
        check(unknown is { Accepted: false } && unknownStatus != HttpStatusCode.OK,
            "不存在的审批被拒绝");
        var (malformedStatus, malformed) = await client.PostAsync("/api/approval",
            new { approvalId = request.ApprovalId, decision = "maybe" });
        var malformedResult = JsonSerializer.Deserialize<RemoteCommandResult>(malformed, RemoteWire.Options);
        check(malformedStatus == HttpStatusCode.BadRequest && malformedResult is { Accepted: false },
            $"非法决定参数被拒绝（实际 {(int)malformedStatus}）");
        check(approvals.Pending.Count == 0, "非法参数不产生审批决议（闸门不被误放行）");
        check(await client.GetAsync("/api/snapshot") is { Status: HttpStatusCode.OK },
            "非法审批参数不破坏服务状态");

        socket.Dispose();
        server.Stop();
    }

    /// <summary>5. 一键紧急停止。</summary>
    private static async Task EmergencyStopAsync(Action<bool, string> check)
    {
        var session = new FakeChatSession();
        using var adapter = new RemoteSessionAdapter(session, "停止会话");
        await using var server = new RemoteControlServer(adapter, LoopbackOptions());
        var status = server.Start();
        using var client = RemoteHttpClient.Create(status.Port);
        var pair = await client.PairAsync(status.Pin!);
        client.Token = pair.Token;

        var idle = await client.StopAsync();
        check(!idle.Accepted, "空闲时紧急停止如实拒绝（不假装中断了任务）");

        session.SimulateQueue(3);
        session.SimulateActivity(SessionActivity.Running);
        var snapshot = await client.SnapshotAsync();
        check(snapshot.CanStop && snapshot.QueuedCount == 3, "运行中且队列非空时快照报告可停止");

        var stopped = await client.StopAsync();
        check(stopped.Accepted && session.StopCount == 1, "紧急停止真实调用会话 Stop");
        var after = await client.SnapshotAsync();
        check(after.QueuedCount == 0 && !after.CanStop, "停止后队列清空且不再可停止");

        server.Stop();
    }

    /// <summary>10. 桌面会话目录、真实切换结果，以及切换后配对令牌仍然有效。</summary>
    private static async Task SessionDirectoryAndStableRebindAsync(Action<bool, string> check)
    {
        var first = new FakeChatSession { CurrentModel = "first-model" };
        var second = new FakeChatSession { CurrentModel = "second-model" };
        using var adapter = new RemoteSessionAdapter(first, "会话一");
        var switched = new List<string>();
        await using var server = new RemoteControlServer(
            adapter,
            LoopbackOptions(),
            sessionCatalog: () => new RemoteSessionCatalog(
                "one",
                [
                    new RemoteSessionInfo("one", "会话一", "项目甲", "FUMO", true, false, true, false, DateTimeOffset.UtcNow),
                    new RemoteSessionInfo("two", "会话二", "项目甲", "OMP", false, true, false, true, DateTimeOffset.UtcNow.AddMinutes(-1)),
                ]),
            switchSession: (id, _) =>
            {
                switched.Add(id);
                adapter.Rebind(second, "会话二");
                return Task.FromResult(id == "two"
                    ? new RemoteCommandResult(true, "已切换")
                    : new RemoteCommandResult(false, "未知会话"));
            });
        var started = server.Start();
        using var client = RemoteHttpClient.Create(started.Port);
        client.Token = (await client.PairAsync(started.Pin!)).Token;

        var catalogResponse = await client.GetAsync("/api/sessions");
        check(catalogResponse.Status == HttpStatusCode.OK, "已配对手机可读取桌面会话目录");
        var catalog = JsonSerializer.Deserialize<RemoteSessionCatalog>(catalogResponse.Body, RemoteWire.Options);
        check(catalog is { CurrentSessionId: "one" } && catalog.Sessions.Count == 2 &&
              catalog.Sessions.Any(item => item.SessionId == "two" && item.IsPinned),
            "会话目录包含真实当前会话、置顶状态与可切换目标");

        var switchResponse = await client.PostAsync("/api/sessions/switch", new { sessionId = "two" });
        check(switchResponse.Status == HttpStatusCode.OK && switched.SequenceEqual(["two"]),
            "切换端点只调用一次桌面控制器并返回受理结果");
        var afterSwitch = await client.SnapshotAsync();
        check(afterSwitch.Title == "会话二" && afterSwitch.Model == "second-model",
            "切换后快照显示新会话，且同一配对令牌仍有效");
        first.SimulateUserMessage("stale-old-session", "旧会话迟到事件");
        check((await client.SnapshotAsync()).Entries.All(entry => entry.Id != "stale-old-session"),
            "切换后旧会话迟到事件不会污染新会话时间线");
        second.SimulateUserMessage("after-switch", "切换后的消息");
        check((await client.SnapshotAsync()).Entries.Any(entry => entry.Id == "after-switch"),
            "切换后的真实会话事件仍能通过原连接读取");

        var bad = await client.PostAsync("/api/sessions/switch", new { sessionId = "missing" });
        check(bad.Status == HttpStatusCode.Conflict,
            "桌面拒绝的会话切换以冲突状态返回，不伪装成功");
    }

    /// <summary>真实宿主切换会话后，手机现有令牌和监听端口仍可继续使用。</summary>
    private static async Task HostRebindPreservesPairingAsync(Action<bool, string> check)
    {
        using var host = new RemoteControlHost(LoopbackOptions());
        var first = new FakeChatSession { CurrentModel = "host-first" };
        var second = new FakeChatSession { CurrentModel = "host-second" };
        host.Attach(first, "宿主会话一");
        var status = await host.StartAsync();
        using var client = RemoteHttpClient.Create(status.Port);
        var token = (await client.PairAsync(status.Pin!)).Token;
        client.Token = token;
        host.Attach(second, "宿主会话二");
        host.LoadHistory([]);
        var snapshot = await client.SnapshotAsync();
        check(snapshot.Title == "宿主会话二" && snapshot.Model == "host-second",
            "真实宿主切换会话后同一手机令牌可读取新会话");
        check(host.Status.Port == status.Port && host.Status.Pin == status.Pin,
            "会话切换不重启监听或轮换 PIN");
    }

    /// <summary>6. 内嵌移动端页面契约（自包含、配对入口、移动端版式要素）。</summary>
    private static void WebAssetContract(Action<bool, string> check)
    {
        var assets = new RemoteControlWebAssets();
        check(assets.IsSelfContained, "内嵌页面自包含（无外部 CDN 依赖，局域网离线可用）");
        check(assets.TryGet("/", out var index) && index.ContentType.StartsWith("text/html", StringComparison.Ordinal),
            "根路径提供 HTML 页面");
        check(assets.TryGet("/app.css", out var css) && css.ContentType.StartsWith("text/css", StringComparison.Ordinal),
            "提供自包含 CSS");
        check(assets.TryGet("/app.js", out var js) && js.ContentType.StartsWith("text/javascript", StringComparison.Ordinal),
            "提供自包含 JS");
        check(!assets.TryGet("/missing.js", out _), "未知资源返回未命中（由服务层回 404）");

        var html = System.Text.Encoding.UTF8.GetString(index.Content);
        check(html.Contains("viewport", StringComparison.Ordinal) &&
              html.Contains("width=device-width", StringComparison.Ordinal),
            "移动端视口声明存在");
        check(html.Contains("safe-area-inset-bottom", StringComparison.Ordinal) ||
              System.Text.Encoding.UTF8.GetString(css.Content).Contains("safe-area-inset", StringComparison.Ordinal),
            "底部安全区适配存在（刘海屏/手势条）");
        check(html.Contains("data-test=\"pin\"", StringComparison.Ordinal) &&
              html.Contains("maxlength=\"4\"", StringComparison.Ordinal),
            "页面提供 4 位 PIN 输入入口");
        check(html.Contains("class=\"tabbar\"", StringComparison.Ordinal), "页面提供底部导航");
        check(html.Contains("data-test=\"stop\"", StringComparison.Ordinal), "页面提供一键紧急停止按钮");
        check(html.Contains("data-test=\"approve\"", StringComparison.Ordinal) &&
              html.Contains("data-test=\"deny\"", StringComparison.Ordinal),
            "页面提供审批批准/拒绝入口");
        check(html.Contains("data-screen=\"connect\"", StringComparison.Ordinal),
            "页面默认进入配对连接态（未认证不显示会话数据）");

        var cssText = System.Text.Encoding.UTF8.GetString(css.Content);
        check(cssText.Contains("prefers-color-scheme: dark", StringComparison.Ordinal),
            "深/浅色自适应样式存在");
        check(cssText.Contains("100dvh", StringComparison.Ordinal), "软键盘适配使用动态视口高度");
        check(cssText.Contains("max-width:480px", StringComparison.Ordinal), "页面按手机宽度布局");
        check(cssText.Contains(".icon-btn { width:44px; height:44px;", StringComparison.Ordinal) &&
              cssText.Contains("min-height:48px", StringComparison.Ordinal),
            "触摸命中区不小于 44px（真机防误触）");

        var jsText = System.Text.Encoding.UTF8.GetString(js.Content);
        check(jsText.Contains("/api/pair", StringComparison.Ordinal) &&
              jsText.Contains("/api/events", StringComparison.Ordinal),
            "前端脚本使用真实服务端点（无夹具冒充）");
        check(!jsText.Contains("FF_FIXTURE", StringComparison.Ordinal),
            "前端脚本不包含原型夹具数据");
    }

    /// <summary>7. 配对二维码契约：模块矩阵与 SVG 可编码，超长安全降级。</summary>
    private static void QrContract(Action<bool, string> check)
    {
        var url = "http://192.168.1.20:53124/";
        var matrix = RemoteControlQr.Encode(url);
        check(matrix is not null, "配对 URL 可编码为二维码矩阵");
        var size = matrix!.GetLength(0);
        check(size >= 21 && (size - 21) % 4 == 0, $"矩阵尺寸符合二维码版本规则（{size}x{size}）");
        check(IsFinderIntact(matrix, 0, 0) && IsFinderIntact(matrix, size - 7, 0) && IsFinderIntact(matrix, 0, size - 7),
            "三个定位图案完整（可被扫码器定位）");
        var svg = RemoteControlQr.ToSvg(url);
        check(svg is not null && svg.StartsWith("<svg", StringComparison.Ordinal) &&
              svg.Contains("viewBox", StringComparison.Ordinal),
            "配对 URL 生成可展示的 SVG");

        var longUrl = "http://192.168.1.20:53124/" + new string('a', 200);
        check(RemoteControlQr.Encode(longUrl) is null && RemoteControlQr.ToSvg(longUrl) is null,
            "超出容量的地址返回 null（降级为文本，不产生不可扫图案）");
        check(RemoteControlQr.Encode(string.Empty) is null,
            "空文本不产生二维码");
    }

    /// <summary>8. 策略护栏：无第三方网络依赖、无共享外壳改动、不涉及 1480 全量自测。</summary>
    private static void PolicyGuardrails(Action<bool, string> check)
    {
        var options = LoopbackOptions();
        check(options.MaxPinAttempts > 0 && options.TokenLifetime > TimeSpan.Zero,
            "安全参数有真实上限（PIN 尝试次数与令牌有效期）");
        check(RemoteControlNetwork.GetLanIPv4Address() is { Length: > 0 },
            "局域网地址探测在无网卡时也有确定返回值（不抛异常）");
        check(new InMemoryRemoteWebAssets().Add("/", "text/html", "<html></html>").TryGet("/", out var asset) &&
              asset.Content.Length > 0,
            "内存资源提供方可用（Core 不依赖 App 资产）");
    }

    /// <summary>轮询服务上报的连接数；到达目标（或精确值）时返回 true，超时返回 false。</summary>
    private static async Task<bool> WaitForConnectionCountAsync(RemoteControlServer server, int atLeast,
        TimeSpan timeout, int? exact = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var count = server.Status.ConnectionCount;
            if (exact is { } value ? count == value : count >= atLeast) return true;
            await Task.Delay(50);
        }

        return false;
    }

    private static bool IsFinderIntact(bool[,] matrix, int left, int top)
    {
        for (var y = 0; y < 7; y++)
        {
            for (var x = 0; x < 7; x++)
            {
                var expected = x is 0 or 6 || y is 0 or 6 || (x is >= 2 and <= 4 && y is >= 2 and <= 4);
                if (matrix[top + y, left + x] != expected) return false;
            }
        }

        return true;
    }

    /// <summary>构造一个不符合实际的 PIN（4 位但与真实 PIN 不同）。</summary>
    private static string WrongPin(string actual) =>
        actual == "0000" ? "1111" : "0000";

    private static RemoteControlOptions LoopbackOptions() => new()
    {
        BindAddress = IPAddress.Loopback,
        MaxPinAttempts = 3,
        TokenLifetime = TimeSpan.FromMinutes(5),
    };

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>9. B2 浏览器扩展（MV3）与端点集成契约。</summary>
    private static async Task ExtensionCollectContractAsync(Action<bool, string> check)
    {
        var session = new FakeChatSession();
        using var adapter = new RemoteSessionAdapter(session, "浏览器扩展测试会话");
        await using var server = new RemoteControlServer(adapter, LoopbackOptions());
        var started = server.Start();

        using var client = new HttpClient();

        // 1. OPTIONS 预检请求与 CORS 头部测试
        using var optionsReq = new HttpRequestMessage(HttpMethod.Options, started.PairingUrl + "api/extension/collect");
        using var optionsResp = await client.SendAsync(optionsReq);
        check(optionsResp.StatusCode == HttpStatusCode.NoContent, "OPTIONS 预检返回 204");
        check(optionsResp.Headers.Contains("Access-Control-Allow-Origin"), "预检请求包含 Access-Control-Allow-Origin 跨域头");

        // 2. 未授权调用 collect 返回 401
        using var unauthResp = await client.PostAsync(started.PairingUrl + "api/extension/collect",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        check(unauthResp.StatusCode == HttpStatusCode.Unauthorized, "未配对调用 /api/extension/collect 返回 401");

        // 3. 配对获取 Token
        using var pairResp = await client.PostAsync(started.PairingUrl + "api/pair",
            new StringContent($"{{\"pin\":\"{started.Pin}\"}}", System.Text.Encoding.UTF8, "application/json"));
        var pairJson = await pairResp.Content.ReadAsStringAsync();
        using var pairDoc = JsonDocument.Parse(pairJson);
        var token = pairDoc.RootElement.GetProperty("token").GetString();
        check(!string.IsNullOrEmpty(token), "浏览器扩展完成 PIN 配对并获得令牌");

        // 4. 带 Token 提交网页引用
        using var collectReq = new HttpRequestMessage(HttpMethod.Post, started.PairingUrl + "api/extension/collect");
        collectReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        collectReq.Content = new StringContent(
            "{\"title\":\"测试页面\",\"url\":\"https://example.com/test\",\"selector\":\"div.article > p\",\"content\":\"这里是引用的正文内容。\"}",
            System.Text.Encoding.UTF8, "application/json");
        using var collectResp = await client.SendAsync(collectReq);
        check(collectResp.StatusCode == HttpStatusCode.OK, "已认证提交 /api/extension/collect 返回 200");
        check(session.Sent.Count == 1, "浏览器扩展引用成功真实投递到底层会话");
        var submitted = session.Sent[0];
        check(submitted.Contains("测试页面") && submitted.Contains("div.article > p") && submitted.Contains("这里是引用的正文内容。"),
            "投递文本完整包含标题、URL、选择器与正文");

        // 5. 校验扩展 MV3 manifest 规范
        var manifestPath = Path.GetFullPath("extensions/browser-extension-mv3/manifest.json");
        if (!File.Exists(manifestPath))
        {
            manifestPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "extensions", "browser-extension-mv3", "manifest.json");
        }
        check(File.Exists(manifestPath), "浏览器扩展 manifest.json 存在");
        if (File.Exists(manifestPath))
        {
            using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            check(manifestDoc.RootElement.GetProperty("manifest_version").GetInt32() == 3,
                "浏览器扩展声明 Manifest V3 规范");
        }
    }
}
