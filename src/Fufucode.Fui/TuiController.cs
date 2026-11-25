using System.Text;
using Fufucode.App.Abstractions;

namespace Fufucode.Fui;

// Only the terminal loop mutates view state. Backend callbacks and commands never write to the screen.
internal sealed partial class TuiController(ITuiSession session, CancellationToken token, string? dataRoot = null,
    FuiSettingsStore? settings = null, IFuiClipboard? clipboard = null, FuiTerminalDetection? terminal = null,
    FuiTerminalRuntimeState? terminalState = null,
    Action<FuiSettings, string, string>? notification = null)
{
    internal const string KeyHelp = "Enter 发送 · Alt+Enter / Ctrl+J 换行 · ↑↓ 历史 · PgUp/PgDn 滚动\n" +
        "Esc 清选择 / 关闭候选 / 停止 · Ctrl+C 复制选区 / 停止 / 清空 / 退出 · Ctrl+D 退出\n" +
        "Ctrl+A 全选 · Shift+方向/Home/End/PgUp/PgDn 扩选 · Ctrl+X 剪切 · Ctrl+V/Shift+Insert 粘贴\n" +
        "Ctrl+Space 本地词补全 · :smile: Emoji 短码 · /vim 切换 Insert/Normal\n" +
        "Vim Normal：h/j/k/l · 0/$ · w/b · x · dd · yy/p · u · i/a/o · Esc\n" +
        "Ctrl+P 命令 · Tab 补全 /命令 或 @文件 · Alt+V 粘贴图片 · Ctrl+R 输入历史 · Ctrl+F 搜索\n" +
        "Ctrl+Z 撤销 · Ctrl+Shift+Z / Alt+Z 重做 · Ctrl+Y / Alt+Y 粘贴 / 轮换删除\n" +
        "Ctrl+U / Ctrl+K / Ctrl+W 删除至行首 / 行尾 / 前一词\n审批：F2 允许 · F3 / Esc 拒绝";

    private readonly Composer _composer = ConfiguredComposer(settings?.Current ?? FuiSettings.Default);
    private readonly FuiSettingsStore _settingsStore = settings ?? FuiSettingsStore.CreateEphemeral();
    private FuiSettings _settings = settings?.Current ?? FuiSettings.Default;
    private readonly FuiTerminalDetection _terminal = terminal ?? FuiTerminalDetector.DetectCurrent();
    private readonly FuiTerminalRuntimeState? _terminalState = terminalState;
    private readonly Action<FuiSettings, string, string>? _notification = notification;
    private readonly List<TuiLine> _lines = InitialLines(session, settings);
    private readonly List<string?> _lineSources = InitialSources(session, settings);
    private readonly IFuiClipboard _clipboard = clipboard ?? WindowsTextClipboard.Instance;
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

    private static string?[] HistorySources(ITuiSession session)
    {
        var stored = session.HistorySources;
        return Enumerable.Range(0, session.History.Count)
            .Select(i => i < stored.Count ? stored[i] : null)
            .ToArray();
    }

    public TuiView View() => new(_active.Project, _active.Id, _active.Model ?? "未配置模型", StatusText, _lines,
        _composer.Text, _composer.Cursor, _scroll, _approvals.FirstOrDefault()?.Text,
        _busy, Overlay(), _expandTools, _showThinking,
        _attachments.Count == 0 ? null : [.. _attachments], _settings.CompactCards, _settings.MouseWheel,
        _settings.Theme, FuiTerminalDetector.HyperlinksEnabled(_settings, _terminal), StatusSegments(), CommandKeyHint,
        ResolveProjectImage, _composer.SelectionStart, _composer.SelectionEnd, _composer.ModeLabel);

    private static Composer ConfiguredComposer(FuiSettings settings)
    {
        var composer = new Composer();
        composer.SetVimEnabled(settings.VimMode);
        return composer;
    }

    private static List<TuiLine> InitialLines(ITuiSession session, FuiSettingsStore? store)
    {
        var lines = new List<TuiLine>(session.History);
        if (store?.LoadIssue is { Length: > 0 } issue) lines.Add(new("error", issue));
        return lines;
    }

    private static List<string?> InitialSources(ITuiSession session, FuiSettingsStore? store)
    {
        var sources = HistorySources(session).ToList();
        if (store?.LoadIssue is { Length: > 0 }) sources.Add(null);
        return sources;
    }

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
                    AppendLine("approval", a.Text, "approval:" + a.Id); // Full arguments stay available via scrollback.
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
                case TuiEvent.TurnFinished turn:
                    NotifyTurn(turn);
                    break;
            }
        }
        if (_deltas.Count != 0)
        {
            foreach (var (i, delta) in _deltas) _lines[i] = _lines[i] with { Text = _lines[i].Text + delta };
            _transcriptGeneration++;
        }
        _deltas.Clear();
        ObserveSend();
        Observe(ref _stop, () => { _stopping = false; _status = "停止失败"; });
        Observe(ref _reply, () => _answeredApproval = null);
        // There may still be an approval behind a large batch of streaming events.
        if (!_active.Events.TryPeek(out _)) UpdateWorkbench();
        RefreshOpenTranscriptPanels();
        RefreshPendingCompletion();
    }

    private void Message(TuiEvent.Message m)
    {
        if (!_indices.TryGetValue(m.Id, out var i))
        {
            _indices.Add(m.Id, _lines.Count);
            AppendLine(m.Kind, m.Text, m.Id, tool: m.Tool, images: m.Images);
        }
        else if (m.Append)
        {
            if (!_deltas.TryGetValue(i, out var buffer)) _deltas.Add(i, buffer = new());
            buffer.Append(m.Text);
            if (m.Tool is not null) _lines[i] = _lines[i] with { Tool = m.Tool };
        }
        else
        {
            _deltas.Remove(i);
            _lines[i] = _lines[i] with
            {
                Kind = m.Kind,
                Text = m.Text,
                Tool = m.Tool,
                Images = m.Images ?? _lines[i].Images
            }; // A final snapshot supersedes all preceding deltas.
            _transcriptGeneration++;
        }
    }

    public void Key(ConsoleKeyInfo key, int viewportRows, int maxScroll, int pickerRows = 5)
    {
        // MouseInput is the only producer of this key. The pending event carries
        // a layout hit; a bare F24 is ignored rather than becoming a shortcut.
        if (MouseInput.IsRoutedKey(key.Key))
        {
            if (MouseInput.TryTake(out var mouse, out var hit))
                Mouse(mouse, viewportRows, maxScroll, pickerRows, hit);
            return;
        }
        Dirty = true;
        var ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        var alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        if (key.Key != 0) _pendingCompletionRefresh = 0;
        if (ctrl && key.Key == ConsoleKey.D) { Exiting = true; return; }
        if ((_approvals.Count != 0 || (_picker is null && _completion is null && !shift)) &&
            key.Key is (ConsoleKey.PageUp or ConsoleKey.PageDown))
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
        if (_keyCaptureAction is not null)
        {
            CaptureKey(key);
            return;
        }
        if (!ctrl && !alt && key.Key == ConsoleKey.Escape && _picker is null && _composer.HasSelection)
        {
            _composer.ClearSelection();
            return;
        }
        if (CompletionKey(key)) return;
        if (ctrl && key.Key == ConsoleKey.Spacebar)
        {
            OpenWordCompletion(announceIfEmpty: true);
            return;
        }
        if (key.Key == ConsoleKey.Escape && _composer.VimEnabled && _picker is null)
        {
            CloseCompletion();
            _composer.HandleVimKey(key, Math.Max(1, viewportRows - 1));
            return;
        }
        if (ctrl && key.Key == ConsoleKey.C && _picker is null && _composer.HasSelection)
        {
            CopyComposerSelection();
            return;
        }
        if (((ctrl && key.Key == ConsoleKey.V) || (shift && key.Key == ConsoleKey.Insert)) && _picker is null)
        {
            if (PasteClipboardText()) return;
        }
        if (ctrl && key.Key == ConsoleKey.X && _composer.HasSelection && _picker is null)
        {
            CutComposerSelection();
            return;
        }
        if (!ctrl && !alt && _picker is null && _composer is { VimEnabled: true, VimMode: VimEditorMode.Normal } &&
            HandleVimNormal(key, viewportRows))
        {
            CloseCompletion();
            return;
        }
        if (key is { Key: ConsoleKey.Enter, Modifiers: 0 } && _pickerAction == "commands" &&
            _picker?.Current is { Value: "paste-image" or "paste" } command &&
            CanRun(FuiCommands.Get(command.Value)!))
        {
            ClosePicker();
            PasteClipboardImage();
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
        if (key.Key == ConsoleKey.F1) { Info(FuiCommands.HelpFor(_settings)); return; }
        if (key.Key == ConsoleKey.Enter && key.Modifiers == 0) { Submit(); return; }
        var revision = _composer.Revision;
        var cursor = _composer.Cursor;
        _composer.Edit(key, Math.Max(1, viewportRows - 1));
        if (_composer.Revision == revision && _composer.Cursor == cursor) return;
        _pendingCompletionRefresh = 0;
        if (key.Key != 0 || _completion is not null) RefreshCompletionAfterEdit();
        else ScheduleCompletionRefresh();
    }

    private void CopyComposerSelection()
    {
        var selected = _composer.SelectedText;
        var result = _clipboard.CopyText(selected);
        if (result.Success) _status = $"已复制 {selected.EnumerateRunes().Count()} 字符";
        else Error("复制选区失败：" + (result.Error ?? "未知原因"));
        Dirty = true;
    }

    private void CutComposerSelection()
    {
        var selected = _composer.SelectedText;
        var copied = _clipboard.CopyText(selected);
        if (!copied.Success)
        {
            Error("剪切选区失败：" + (copied.Error ?? "未知原因"));
            return;
        }
        PrepareComposerMutation();
        _composer.CutSelection();
        CloseCompletion();
        _status = $"已剪切 {selected.EnumerateRunes().Count()} 字符";
        Dirty = true;
    }

    private bool PasteClipboardText()
    {
        if (!_clipboard.TryReadText(out var text, out _)) return false;
        var clean = TerminalText.Clean(text, 0, out _, expandTabs: false);
        if (clean.Length == 0) return false;
        PrepareComposerMutation();
        _composer.InsertPastedText(clean);
        CloseCompletion();
        RefreshCompletionAfterEdit();
        Dirty = true;
        return true;
    }

    private bool HandleVimNormal(ConsoleKeyInfo key, int viewportRows)
    {
        if (key.KeyChar is 'x' or 'p' or 'u' or 'i' or 'a' or 'o' or 'd' or 'y')
            PrepareComposerMutation();
        return _composer.HandleVimKey(key, Math.Max(1, viewportRows - 1));
    }

    internal void Mouse(MouseEvent mouse, int viewportRows, int maxScroll, int pickerRows, MouseHit hit)
    {
        Dirty = true;
        if (MouseInput.WheelKey(mouse) is { } wheel)
        {
            if (_settings.MouseWheel) Key(wheel, viewportRows, maxScroll, pickerRows);
            return;
        }
        if (!mouse.LeftClick || mouse.Release || mouse.Motion || hit.Kind == MouseHitKind.None) return;

        if (_approvals.FirstOrDefault() is { } approval)
        {
            // A click is not a keyboard shortcut. It may only use the exact
            // painted action hit and the same painted-approval gate as F2/F3.
            if (!hit.IsApproval || _approvalFocusChanged || _stopping ||
                _paintedApproval != approval.Id || _answeredApproval == approval.Id || _reply is not null)
                return;
            var allow = hit.Kind == MouseHitKind.ApprovalAllow;
            if (allow && !_canApprove) return;
            _answeredApproval = approval.Id;
            _reply = Invoke(() => _active.RespondAsync(approval.Id, allow, token));
            return;
        }

        if (hit.Kind == MouseHitKind.PickerItem && _picker is not null)
            SelectPicker(hit.Index);
        else if (hit.Kind == MouseHitKind.Composer)
        {
            _composer.SetCursor(hit.Index);
            CloseCompletion();
            Dirty = true;
        }
    }

    private void SelectPicker(int index)
    {
        if (_picker is not { } picker) return;
        var view = picker.View;
        if (index < 0 || index >= view.Items.Count ||
            FuiChoiceGroups.IsGroup(view.Items[index])) return;
        var current = view.Selected;
        if (current == index) return;
        var key = index > current ? ConsoleKey.DownArrow : ConsoleKey.UpArrow;
        for (var guard = 0; guard <= view.Items.Count; guard++)
        {
            picker.Edit(new('\0', key, false, false, false), 1);
            var next = picker.View.Selected;
            if (next == index) return;
            if (next == current) return;
            current = next;
        }
    }

    private void Submit()
    {
        var text = _composer.Text;
        if (string.IsNullOrWhiteSpace(text) && !_attachments.Any(file => file.MimeType is not null)) return;
        if (text.StartsWith('/'))
        {
            if (text.StartsWith("//", StringComparison.Ordinal)) text = text[1..];
            else if (!text.StartsWith("/skill:", StringComparison.Ordinal))
            {
                var command = text.TrimEnd();
                if (command is "/paste-image" or "/paste" ||
                    command.Equals("/paste-image", StringComparison.OrdinalIgnoreCase) ||
                    command.Equals("/paste", StringComparison.OrdinalIgnoreCase))
                {
                    if (CanRun(FuiCommands.Get(command[1..])!))
                    {
                        _composer.Take();
                        PasteClipboardImage();
                    }
                }
                else SubmitCommand(text);
                return;
            }
        }
        if (_busy || _send is not null || _stop is not null || _job is not null) return; // Keep the next draft editable.
        if (_settings.EmojiAutocomplete == "auto") text = EmojiShortcodes.ExpandCompletedShortcodes(text);
        if (_attachments.Count != 0) { SendWithContext(text); return; }
        Send(new FuiPrompt(text, []));
    }

    private void PasteClipboardImage()
    {
        if (_attachments.Count >= ProjectFiles.MaxAttachments)
        {
            Error($"每条消息最多附加 {ProjectFiles.MaxAttachments} 个文件。");
            return;
        }
        Begin("paste-image", async ct => new(File: await Files().ReadClipboardImageAsync(ct)), cancellable: true);
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

    private void Info(string text, IReadOnlyList<TuiImage>? images = null)
    {
        AppendLine("info", text, images: images);
        _scroll = 0;
    }

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
            AppendLine("error", e.GetBaseException().Message);
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
