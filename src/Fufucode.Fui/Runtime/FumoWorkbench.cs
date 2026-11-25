using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.Fui;

internal sealed partial class FumoTuiSession
{
    public async Task<IReadOnlyList<FuiChoice>> ChoicesAsync(string action, CancellationToken ct)
    {
        using var operation = BeginOperation(ct);
        ct = operation.Token;
        await _operations.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            return action switch
            {
                "resume" or "tree" => SessionChoices(),
                "fork" => await ForkChoicesAsync(ct),
                "skills" => BuildRuntimeCatalog().SkillChoices(),
                "capabilities" => BuildRuntimeCatalog().CapabilityChoices(),
                "model" => (await ModelsAsync(ct)).Select(m => new FuiChoice(m.Id, m.Name,
                    m.Id == Model ? $"当前 · {m.Id}" : m.Id)).ToArray(),
                "thinking" => await ThinkingChoicesAsync(ct),
                "mode" => ModeChoices(),
                _ => throw new ArgumentException($"没有选择器：{action}")
            };
        }
        finally { _operations.Release(); }
    }

    public async Task<FuiActionResult> ExecuteAsync(string action, string argument, CancellationToken ct)
    {
        using var operation = BeginOperation(ct);
        ct = operation.Token;
        await _operations.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (action is "queue" or "steer") return await DeliverAsync(action, argument, ct);
            if (action == "usage")
            {
                Empty(argument, action);
                // A live tool loop can alter in-memory history. Query its atomic persisted checkpoint rather
                // than race the worker or fabricate counters from incomplete streaming content.
                var active = _workerPending;
                var saved = ReadOnlySessionReport.Load(_project.Manager.Layout, Project, Id);
                return new(FuiTranscript.Usage(saved.History, active));
            }
            // Native BeginIdleMaintenance atomically reserves the session against a later submit. Check the
            // chat too: the worker may have accepted a prompt but not yet published ActivityChanged.
            using var idle = ReserveIdle();
            switch (action)
            {
                case "new":
                    Empty(argument, action);
                    return new(Replacement: await OpenSelectedAsync(_project, Project, null, ct));
                case "resume":
                case "tree":
                    var id = Required(argument, "会话 ID");
                    if (id == Id) return new("已在当前会话。");
                    var target = SelectSession(_project.Manager, Project, id, false);
                    return new(Replacement: await OpenSelectedAsync(_project, Project, target, ct));
                case "fork":
                    var entry = Required(argument, "分叉点 ID");
                    var fork = await _project.Manager.ForkAsync(_session, entry, ct);
                    // Core copies history/model into a new native session but not thinking or permission mode.
                    // Never silently upgrade a read-only parent, nor carry its elevated execution mode into a new session.
                    var child = await AttachAsync(_project, fork.Session, false, true, ct, async () =>
                    {
                        var parentThinking = await _session.Coordinator.QueryThinkingLevelsAsync(ct);
                        if (parentThinking.CurrentLevel is { } level)
                            RequireSuccess(await fork.Session.Coordinator.SetThinkingLevelAsync(level, ct));
                        if (fork.Session.Chat is not IExecutionModeSession childModes)
                            throw new InvalidOperationException("分叉无法确认原生权限模式。");
                        var mode = (_session.Chat as IExecutionModeSession)?.ExecutionMode == "plan" ? "plan" : childModes.ExecutionMode;
                        await childModes.SetExecutionModeAsync(mode, ct);
                    });
                    return new(Replacement: child);
                case "rename":
                    var name = Required(argument, "名称");
                    await _session.RenameAsync(name, ct);
                    return new($"会话名称：{_session.Session.Name}");
                case "export":
                    var path = await FuiTranscript.ExportAsync(await SnapshotAsync(ct), _session, _project.Manager.Layout, argument, ct);
                    return new(path);
                case "model":
                    var model = Required(argument, "模型 ID");
                    if (!(await ModelsAsync(ct)).Any(m => m.Id == model)) throw new ArgumentException($"端点目录没有模型：{model}");
                    RequireSuccess(await _session.Coordinator.SetModelAsync("fumo", model, ct));
                    if (Model != model) throw new InvalidOperationException("FUMO 未确认模型变更。");
                    await SaveSettingsAsync(ct);
                    return new($"模型：{model}");
                case "thinking":
                    var thinking = Required(argument, "思考等级").ToLowerInvariant();
                    if (!(await ThinkingChoicesAsync(ct)).Any(c => c.Value == thinking))
                        throw new ArgumentException($"此模型不可选的思考等级：{thinking}");
                    RequireSuccess(await _session.Coordinator.SetThinkingLevelAsync(thinking, ct));
                    if ((await _session.Coordinator.QueryThinkingLevelsAsync(ct)).CurrentLevel != thinking)
                        throw new InvalidOperationException("FUMO 未确认思考等级变更。");
                    await SaveSettingsAsync(ct);
                    return new($"思考等级：{thinking}");
                case "mode":
                    var mode = Required(argument, "执行模式").ToLowerInvariant();
                    if (ModeChoices().All(choice => choice.Value != mode))
                        throw new ArgumentException($"未知执行模式：{mode}");
                    if (_session.Chat is not IExecutionModeSession modes)
                        throw new InvalidOperationException("当前 FUMO 会话不支持执行模式。");
                    await modes.SetExecutionModeAsync(mode, ct);
                    if (!string.Equals(modes.ExecutionMode, mode, StringComparison.Ordinal))
                        throw new InvalidOperationException("FUMO 未确认执行模式变更。");
                    await SaveSettingsAsync(ct);
                    return new($"执行模式：{ModeChoices().First(choice => choice.Value == mode).Label}");
                default: throw new ArgumentException($"未知工作台操作：{action}");
            }
        }
        finally { _operations.Release(); }
    }

    private CancellationTokenSource BeginOperation(CancellationToken ct)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            return CancellationTokenSource.CreateLinkedTokenSource(ct, _workbenchLifetime.Token);
        }
    }

    private IDisposable ReserveIdle()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_workerPending || _session.Chat.QueuedCount != 0)
                throw new InvalidOperationException("会话仍在运行；请等待结束或先停止。");
            return _session.Coordinator.BeginIdleMaintenance();
        }
    }

    private IReadOnlyList<FuiChoice> SessionChoices() => _project.Manager.Metadata.ListSessions(_session.Project.Id)
        .Where(record => record.Backend == BackendId.NativeCSharp)
        .Select(record => new FuiChoice(record.Id, string.IsNullOrWhiteSpace(record.Name) ? record.Id : record.Name,
            string.Join(" · ", new[] { record.Id == Id ? (_workerPending ? "当前 · 运行中" : "当前")
                : record.WasActive ? "记录为活动" : null, record.LastOpened.ToLocalTime().ToString("g"), record.Id }.Where(v => v != null)),
            record.ParentSessionId)).ToArray();

    private async Task<IReadOnlyList<FuiChoice>> ForkChoicesAsync(CancellationToken ct)
    {
        using var idle = ReserveIdle();
        var history = await SnapshotAsync(ct);
        return history.Entries.Where(e => e.OnCurrentBranch && e.Message?.Role == "user")
            .Select(e => new FuiChoice(e.Id, string.Join(" ", e.Message!.Content.OfType<PiTextBlock>().Select(t => t.Text)) is { Length: > 0 } text
                    ? text : e.Id, e.Id)).Reverse().ToArray();
    }

    private async Task<SessionHistory> SnapshotAsync(CancellationToken ct)
    {
        // Enumerate only for an explicit action. Unlike Coordinator.GetUsage, the entry store preserves null vs zero.
        var response = await _session.Coordinator.QueryEntriesAsync(cancellationToken: ct);
        RequireSuccess(response);
        var history = new SessionHistory();
        history.ApplyEntriesResponse(response.Data ?? throw new InvalidDataException("FUMO 未返回会话历史。"), replace: true);
        return history;
    }

    private sealed record CatalogModel(string Id, string Name, IReadOnlyList<string> Reasoning, long? ContextLength);

    private async Task<IReadOnlyList<CatalogModel>> ModelsAsync(CancellationToken ct)
    {
        var response = await _session.Coordinator.QueryAvailableModelsAsync(ct);
        ct.ThrowIfCancellationRequested();
        RequireSuccess(response);
        if (response.Data is not { ValueKind: JsonValueKind.Object } data || !data.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("端点未返回模型目录。");
        return models.EnumerateArray().Select(row =>
        {
            var id = row.GetProperty("id").GetString() ?? throw new InvalidDataException("端点目录包含无 ID 的模型。");
            var name = row.TryGetProperty("name", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() : null;
            var levels = row.TryGetProperty("reasoningLevels", out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];
            long? contextLength = row.TryGetProperty("contextLength", out var context) &&
                context.ValueKind == JsonValueKind.Number && context.TryGetInt64(out var length) && length > 0
                    ? length
                    : null;
            return new CatalogModel(id, name ?? id, levels, contextLength);
        }).ToArray();
    }

    private async Task<IReadOnlyList<FuiChoice>> ThinkingChoicesAsync(CancellationToken ct)
    {
        var native = await _session.Coordinator.QueryThinkingLevelsAsync(ct);
        var selected = (await ModelsAsync(ct)).FirstOrDefault(m => m.Id == Model);
        if (selected is null) throw new InvalidOperationException("当前模型不在端点目录中，无法核对思考等级。");
        return native.Levels.Where(l => l == "off" || selected.Reasoning.Count == 0 || selected.Reasoning.Contains(l, StringComparer.OrdinalIgnoreCase))
            .Select(l => new FuiChoice(l, l, l == native.CurrentLevel ? "当前" : selected.Reasoning.Count == 0 && l != "off" ? "端点未声明；FUMO 可请求" : null)).ToArray();
    }

    private IReadOnlyList<FuiChoice> ModeChoices()
    {
        var current = (_session.Chat as IExecutionModeSession)?.ExecutionMode ?? "manual";
        return
        [
            new("plan", "Plan · 只读规划", current == "plan" ? "当前 · 只允许只读工具和 update_plan" : "只读探索与计划；禁止写入和执行命令"),
            new("manual", "Manual · 逐项确认", current == "manual" ? "当前 · 变更工具逐项确认" : "所有变更工具执行前确认"),
            new("auto", "Auto · 仅确认 Shell", current == "auto" ? "当前 · 变更工具自动，Shell 确认" : "文件变更自动执行；命令仍需确认"),
            new("full", "Full · 不弹确认", current == "full" ? "当前 · 不受工具确认阻挡" : "不弹工具确认；安全中心显式拒绝仍生效")
        ];
    }

    private async Task SaveSettingsAsync(CancellationToken ct)
    {
        // The native coordinator changes these values in memory; the native serializer (with mode unchanged)
        // also persists on an empty transcript, so switching away immediately does not lose the choice.
        if (_session.Chat is not IExecutionModeSession modes) throw new InvalidOperationException("无法保存原生会话设置。");
        await modes.SetExecutionModeAsync(modes.ExecutionMode, ct);
        using var input = File.OpenRead(_session.Session.FilePath!);
        var saved = PersistentSessionManager.ParseNativeMessages(input);
        var current = await _session.Coordinator.QueryThinkingLevelsAsync(ct);
        if (saved is null || saved.ModelId != Model || saved.ThinkingLevel != current.CurrentLevel)
            throw new IOException("会话设置已在内存变更，但未能确认写入历史文件。");
    }

    private async Task<FuiActionResult> DeliverAsync(string action, string argument, CancellationToken ct)
    {
        if (action == "queue" && string.IsNullOrWhiteSpace(argument))
        {
            var pending = (_session.Chat as IQueuedChatSession)?.PendingQueue ?? throw new InvalidOperationException("FUMO 未提供待发队列。");
            var lines = pending.QueuedTexts.Select((text, i) => $"{(pending.Steering is { } s && i < s.Count && s[i] ? "插话" : "排队")}：{text}")
                .Concat(_session.Coordinator.Snapshot.RecoverableQueue.Select(text => "已取消：" + text)).ToArray();
            return new(lines.Length == 0 ? "队列为空" : string.Join('\n', lines));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(argument);
        Task submitted;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_session.Chat is not NativeCSharpChatSession native)
                throw new InvalidOperationException("FUMO 未提供原生排队和插话入口。");
            if (!_workerPending || !_settling.IsCompleted ||
                native.Activity is not (SessionActivity.Running or SessionActivity.Compacting or SessionActivity.Retrying))
                throw new InvalidOperationException("仅能向运行中的会话排队或插话。");
            // The coordinator marks idle before the worker releases. Use the worker's actual delivery
            // method so a requested steer cannot change into a follow-up during that handoff.
            submitted = action == "steer" ? native.SteerAsync(argument, ct) : native.SendAsync(argument, ct);
        }
        await submitted;
        return new(action == "steer" ? "已提交插话" : "已提交后续消息");
    }

    private static void RequireSuccess(RpcResponse response)
    {
        if (!response.Success) throw new InvalidOperationException(response.Error ?? $"FUMO 拒绝了 {response.Command}。");
    }

    private static string Required(string argument, string what) => !string.IsNullOrWhiteSpace(argument)
        ? argument.Trim() : throw new ArgumentException($"缺少{what}。");

    private static void Empty(string argument, string action)
    {
        if (!string.IsNullOrWhiteSpace(argument)) throw new ArgumentException($"/{action} 不接受参数。");
    }
}
