using System.Text;

namespace Fufucode.Fui;

// Only the terminal loop mutates view state. Backend callbacks and commands never write to the screen.
internal sealed partial class TuiController(ITuiSession session, CancellationToken token, string? dataRoot = null)
{
    internal const string KeyHelp = "Enter 发送 · Alt+Enter / Ctrl+J 换行 · ↑↓ 历史 · PgUp/PgDn 滚动\n" +
        "Esc 停止 · Ctrl+C 停止 / 清空输入 / 退出 · Ctrl+D 退出\n" +
        "Ctrl+P 命令 · Tab 补全 /命令 或 @文件 · Ctrl+R 输入历史 · Ctrl+F 搜索\n" +
        "Ctrl+U / Ctrl+K 删除至行首 / 行尾 · Ctrl+W 删除前一词\n审批：F2 允许 · F3 / Esc 拒绝";

    private readonly Composer _composer = new();
    private readonly List<TuiLine> _lines = [.. session.History];
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
    private readonly Dictionary<int, StringBuilder> _deltas = [];
    private readonly List<TuiEvent.Approval> _approvals = [];
    private string? _paintedApproval, _answeredApproval;
    private string _status = "就绪";
    private bool _busy;
    private int _scroll;
    private Task? _send, _stop, _reply;
    private ITuiSession _active = session;

    public bool Dirty { get; private set; } = true;
    public bool Exiting { get; private set; }

    public TuiView View() => new(_active.Project, _active.Id, _active.Model ?? "未配置模型", StatusText, _lines,
        _composer.Text, _composer.Cursor, _scroll, _approvals.FirstOrDefault() is { } a
            ? $"F2 允许 · F3 拒绝\n{a.Text}" : null, _busy, Overlay(), _expandTools, _showThinking);

    public void Painted(int maxScroll)
    {
        _paintedApproval = _approvals.FirstOrDefault()?.Id;
        _scroll = Math.Clamp(_scroll, 0, maxScroll);
        Dirty = false;
    }

    public void Update()
    {
        // Bound each batch so an always-ready token producer cannot starve keys or resize.
        for (var n = 0; n < 2048 && _active.Events.TryRead(out var ev); n++)
        {
            Dirty = true;
            switch (ev)
            {
                case TuiEvent.Message m: Message(m); break;
                case TuiEvent.Status s: (_status, _busy) = (s.Text, s.Busy); break;
                case TuiEvent.Approval a when !_approvals.Exists(x => x.Id == a.Id):
                    ClosePicker();
                    _approvals.Add(a);
                    _lines.Add(new("info", a.Text)); // Full arguments stay available via scrollback.
                    _scroll = 0;
                    break;
                case TuiEvent.ApprovalClosed a:
                    _approvals.RemoveAll(x => x.Id == a.Id);
                    if (_answeredApproval == a.Id) _answeredApproval = null;
                    break;
            }
        }
        foreach (var (i, delta) in _deltas) _lines[i] = _lines[i] with { Text = _lines[i].Text + delta };
        _deltas.Clear();
        Observe(ref _send, () => { _busy = false; _status = "发送失败"; });
        Observe(ref _stop, () => _status = "停止失败");
        Observe(ref _reply, () => _answeredApproval = null);
        UpdateWorkbench();
    }

    private void Message(TuiEvent.Message m)
    {
        if (!_indices.TryGetValue(m.Id, out var i))
        {
            _indices.Add(m.Id, _lines.Count);
            _lines.Add(new(m.Kind, m.Text));
        }
        else if (m.Append)
        {
            if (!_deltas.TryGetValue(i, out var buffer)) _deltas.Add(i, buffer = new());
            buffer.Append(m.Text);
        }
        else
        {
            _deltas.Remove(i);
            _lines[i] = new(m.Kind, m.Text); // A final snapshot supersedes all preceding deltas.
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
                _paintedApproval == approval.Id && _answeredApproval != approval.Id && _reply is null)
            {
                _answeredApproval = approval.Id;
                _reply = Invoke(() => _active.RespondAsync(approval.Id, key.Key == ConsoleKey.F2, token));
            }
            return;
        }
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
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.StartsWith('/'))
        {
            if (text.StartsWith("//", StringComparison.Ordinal)) text = text[1..];
            else if (!text.StartsWith("/skill:", StringComparison.Ordinal)) { SubmitCommand(text); return; }
        }
        if (_busy || _send is not null || _stop is not null || _job is not null) return; // Keep the next draft editable.
        if (_attachments.Count != 0) { SendWithContext(text); return; }
        _scroll = 0;
        _busy = true;
        _status = "发送中";
        _composer.Take();
        _send = Invoke(() => _active.SendAsync(text, token));
    }

    private void Stop()
    {
        if (!_busy || _stop is not null) return;
        _status = "正在停止";
        _stop = Invoke(() => _active.StopAsync(token));
    }

    private void Info(string text) { _lines.Add(new("info", text)); _scroll = 0; }

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
        // This controller owns the current lease, including any replacement which completed during exit.
        try
        {
            if (_job is not null)
            {
                try { var result = await _job.Task; if (result.Action?.Replacement is { } fresh && !ReferenceEquals(fresh, _active)) await fresh.DisposeAsync(); }
                catch (OperationCanceledException) when (token.IsCancellationRequested || _job.Lifetime.IsCancellationRequested) { }
            }
            foreach (var t in new[] { _reply, _send, _stop, _release })
            {
                if (t is null) continue;
                try { await t; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
        }
        finally { _job?.Lifetime.Dispose(); await _active.DisposeAsync(); }
    }
}
