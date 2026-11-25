using System.Text;
using Fufucode.App.Abstractions;

namespace Fufucode.Fui;

// Only the terminal loop mutates view state. Backend callbacks and commands never write to the screen.
internal sealed partial class TuiController(ITuiSession session, CancellationToken token, string? dataRoot = null)
{
    internal const string KeyHelp = "Enter 发送 · Alt+Enter / Ctrl+J 换行 · ↑↓ 历史 · PgUp/PgDn 滚动\n" +
        "Esc 停止 · Ctrl+C 停止 / 清空输入 / 退出 · Ctrl+D 退出\n" +
        "Ctrl+P 命令 · Tab 补全 /命令 或 @文件 · Alt+V 粘贴图片 · Ctrl+R 输入历史 · Ctrl+F 搜索\n" +
        "Ctrl+Z 撤销 · Ctrl+Shift+Z / Alt+Z 重做 · Ctrl+Y / Alt+Y 粘贴 / 轮换删除\n" +
        "Ctrl+U / Ctrl+K / Ctrl+W 删除至行首 / 行尾 / 前一词\n审批：F2 允许 · F3 / Esc 拒绝";

    private readonly Composer _composer = new();
    private readonly List<TuiLine> _lines = [.. session.History];
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
    private readonly Dictionary<int, StringBuilder> _deltas = [];
    private readonly List<TuiEvent.Approval> _approvals = [];
    private string? _paintedApproval, _answeredApproval;
    private bool _canApprove, _approvalFocusChanged, _stopping;
    private string _status = "就绪";
    private bool _busy;
    private int _scroll;
    private sealed record Submission(Task Acknowledged, long DraftRevision, FileAttachment[] Files);
    private Submission? _send;
    private Task? _stop, _reply;
    private ITuiSession _active = session;

    public bool Dirty { get; private set; } = true;
    public bool Exiting { get; private set; }

    public TuiView View() => new(_active.Project, _active.Id, _active.Model ?? "未配置模型", StatusText, _lines,
        _composer.Text, _composer.Cursor, _scroll, _approvals.FirstOrDefault()?.Text,
        _busy, Overlay(), _expandTools, _showThinking, ResolveProjectImage);

    public void Painted(int maxScroll, bool canApprove)
    {
        _paintedApproval = _approvals.FirstOrDefault()?.Id;
        _canApprove = canApprove;
        _approvalFocusChanged = false;
        _scroll = Math.Clamp(_scroll, 0, maxScroll);
        Dirty = false;
    }

    public void InvalidateApprovalFrame() => _canApprove = false;

    public void Update()
    {
        // Bound each batch so an always-ready token producer cannot starve keys or resize.
        for (var n = 0; n < 2048 && _active.Events.TryRead(out var ev); n++)
        {
            Dirty = true;
            switch (ev)
            {
                case TuiEvent.Message m: Message(m); break;
                case TuiEvent.Status s:
                    (_status, _busy) = (s.Text, s.Busy);
                    if (!s.Busy) _stopping = false;
                    else if (_job?.Id == "send-files") _job.Lifetime.Cancel();
                    break;
                case TuiEvent.Approval a when !_approvals.Exists(x => x.Id == a.Id):
                    PreemptWorkbench();
                    if (_approvals.Count == 0) _approvalFocusChanged = true;
                    _approvals.Add(a);
                    _lines.Add(new("info", a.Text)); // Full arguments stay available via scrollback.
                    _scroll = 0;
                    break;
                case TuiEvent.ApprovalClosed a:
                    if (_approvals.FirstOrDefault()?.Id == a.Id) _approvalFocusChanged = true;
                    _approvals.RemoveAll(x => x.Id == a.Id);
                    if (_answeredApproval == a.Id) _answeredApproval = null;
                    break;
                case TuiEvent.AgentsChanged a:
                    UpdateAgentActivity(a.Snapshot);
                    break;
            }
        }
        foreach (var (i, delta) in _deltas) _lines[i] = _lines[i] with { Text = _lines[i].Text + delta };
        _deltas.Clear();
        ObserveSend();
        Observe(ref _stop, () => { _stopping = false; _status = "停止失败"; });
        Observe(ref _reply, () => _answeredApproval = null);
        // There may still be an approval behind a large batch of streaming events.
        if (!_active.Events.TryPeek(out _)) UpdateWorkbench();
    }

    private void Message(TuiEvent.Message m)
    {
        if (!_indices.TryGetValue(m.Id, out var i))
        {
            _indices.Add(m.Id, _lines.Count);
            _lines.Add(new(m.Kind, m.Text, m.Images));
        }
        else if (m.Append)
        {
            if (!_deltas.TryGetValue(i, out var buffer)) _deltas.Add(i, buffer = new());
            buffer.Append(m.Text);
        }
        else
        {
            _deltas.Remove(i);
            _lines[i] = new(m.Kind, m.Text, m.Images ?? _lines[i].Images); // A final snapshot supersedes all preceding deltas.
        }
    }

    public void Key(ConsoleKeyInfo key, int viewportRows, int maxScroll, int pickerRows = 5)
    {
        Dirty = true;
        var ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (ctrl && key.Key == ConsoleKey.D) { Exiting = true; return; }
        if ((_approvals.Count != 0 || _picker is null) && key.Key is (ConsoleKey.PageUp or ConsoleKey.PageDown))
        {
            var step = Math.Max(1, viewportRows - 1);
            _scroll = key.Key == ConsoleKey.PageUp ? (int)Math.Min((long)_scroll + step, maxScroll) : Math.Max(0, _scroll - step);
            return;
        }
        if (_approvals.FirstOrDefault() is { } approval)
        {
            if (ctrl && key.Key == ConsoleKey.C) { Stop(); return; }
            // A buffered key must not approve a newly arrived or newly revealed request before it was painted.
            if (key.Modifiers == 0 && key.Key is (ConsoleKey.F2 or ConsoleKey.F3 or ConsoleKey.Escape) &&
                !_approvalFocusChanged && !_stopping && (key.Key != ConsoleKey.F2 || _canApprove) &&
                _paintedApproval == approval.Id && _answeredApproval != approval.Id && _reply is null)
            {
                _answeredApproval = approval.Id;
                _reply = Invoke(() => _active.RespondAsync(approval.Id, key.Key == ConsoleKey.F2, token));
            }
            return;
        }
        // The terminal still shows the old approval until the next frame. Its Enter/Esc cannot operate the draft.
        if (_approvalFocusChanged) return;
        if (WorkbenchKey(key, pickerRows)) return;
        if (ctrl && key.Key == ConsoleKey.C)
        {
            if (_busy) Stop();
            else if (_composer.Text.Length != 0) _composer.Clear();
            else Exiting = true;
            return;
        }
        if (key.Key == ConsoleKey.Escape) { Stop(); return; }
        if (key.Key == ConsoleKey.F1) { Info(FuiCommands.Help); return; }
        if (key.Key == ConsoleKey.Enter && key.Modifiers == 0) { Submit(); return; }
        _composer.Edit(key);
    }

    private void Submit()
    {
        var text = _composer.Text;
        if (string.IsNullOrWhiteSpace(text) && !_attachments.Any(file => file.MimeType is not null)) return;
        if (text.StartsWith('/'))
        {
            if (text.StartsWith("//", StringComparison.Ordinal)) text = text[1..];
            else if (!text.StartsWith("/skill:", StringComparison.Ordinal)) { SubmitCommand(text); return; }
        }
        if (_busy || _send is not null || _stop is not null || _job is not null) return; // Keep the next draft editable.
        if (_attachments.Count != 0) { SendWithContext(text); return; }
        Send(new FuiPrompt(text, []));
    }

    private void Send(FuiPrompt prompt)
    {
        _scroll = 0;
        _busy = true;
        _status = "发送中";
        var acknowledged = Invoke(() => prompt.Images.Count == 0
            ? _active.SendAsync(prompt.Text, token)
            : _active is IChatAttachments images ? images.SendWithImagesAsync(prompt.Text, prompt.Images, token)
            : throw new InvalidOperationException("当前会话没有图片发送入口。"));
        _send = new(acknowledged, _composer.Revision, [.. _attachments]);
        ObserveSend();
    }

    private void ObserveSend()
    {
        if (_send is not { Acknowledged.IsCompleted: true } sent) return;
        _send = null;
        Dirty = true;
        try
        {
            sent.Acknowledged.GetAwaiter().GetResult();
            if (_composer.Revision == sent.DraftRevision) _composer.Take();
            foreach (var file in sent.Files) _attachments.Remove(file);
        }
        catch (Exception error)
        {
            _busy = false;
            _status = "发送失败";
            Error(error.GetBaseException().Message);
        }
    }

    private void Stop()
    {
        if ((!_busy && _approvals.Count == 0) || _stopping || _stop is not null) return;
        _stopping = true;
        _status = "正在停止";
        _stop = Invoke(() => _active.StopAsync(token));
    }

    private void Info(string text, IReadOnlyList<TuiImage>? images = null) { _lines.Add(new("info", text, images)); _scroll = 0; }

    private static Task Invoke(Func<Task> command)
    {
        try { return command(); }
        catch (Exception e) { return Task.FromException(e); }
    }

    private void Observe(ref Task? task, Action onError)
    {
        if (task is not { IsCompleted: true } finished) return;
        task = null;
        try { finished.GetAwaiter().GetResult(); }
        catch (Exception e)
        {
            _lines.Add(new("error", e.GetBaseException().Message));
            Dirty = true;
            onError();
        }
    }

    public async Task FinishAsync()
    {
        try
        {
            var finishJob = _job is { } job ? FinishJobAsync(job) : Task.CompletedTask;
            await Task.WhenAll(new[] { finishJob, _reply, _send?.Acknowledged, _stop, _release }.OfType<Task>());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { _job?.Lifetime.Dispose(); await _active.DisposeAsync(); }
    }

    private async Task FinishJobAsync(Job job)
    {
        if (job.Cancellable) job.Lifetime.Cancel();
        try
        {
            var result = await job.Task;
            if (result.Action?.Replacement is { } next && !ReferenceEquals(next, _active)) await next.DisposeAsync();
        }
        catch (OperationCanceledException) when (job.Lifetime.IsCancellationRequested) { }
    }
}
