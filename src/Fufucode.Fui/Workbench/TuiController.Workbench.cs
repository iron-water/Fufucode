using Fufucode.Core;

namespace Fufucode.Fui;

internal sealed partial class TuiController
{
    private readonly string? _dataRoot = dataRoot;
    private ProjectFiles? _files;
    private ProjectFiles Files() => LazyInitializer.EnsureInitialized(ref _files,
        () => new ProjectFiles(_active.Project, _dataRoot ?? AppDataLayout.DefaultRoot()));
    private readonly List<FileAttachment> _attachments = [];
    private readonly Dictionary<string, (string Text, int Cursor, FileAttachment[] Files)> _drafts = new();
    private FuiPicker? _picker;
    private string? _pickerAction;
    private int _mentionStart = -1;
    private bool _expandTools, _showThinking = true;
    private int? _scrollTarget;
    private Task? _release;
    private (FuiCommand Command, string Argument)? _deferred;
    private sealed record Result(FuiActionResult? Action = null, IReadOnlyList<FuiChoice>? Choices = null,
        string? Title = null, FileAttachment? File = null, FuiPrompt? Prompt = null);
    private sealed record Job(string Id, Task<Result> Task, CancellationTokenSource Lifetime, bool Cancellable)
    {
        public bool PreemptedByApproval { get; set; }
    }
    private Job? _job;
    private string StatusText
    {
        get
        {
            var status = (_job is null || _approvals.Count != 0 ? _status : FuiCommands.Get(_job.Id)?.Label ?? "读取上下文") +
                (_attachments.Count == 0 ? "" : $" · 附件 {_attachments.Count}");
            var agents = AgentStatusSummary;
            return agents.Length == 0 ? status : agents + " · " + status;
        }
    }

    public int? TakeScrollTarget() { var target = _scrollTarget; _scrollTarget = null; return target; }
    public void ScrollTo(int scroll) => _scroll = scroll;
    private void Error(string message) { _lines.Add(new("error", message)); _scroll = 0; Dirty = true; }

    private void Begin(string id, Func<CancellationToken, Task<Result>> work, bool cancellable = false)
    {
        if (_job is not null) { Error("上一项操作尚未结束。"); return; }
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        // SQLite, project policy, filesystem and model registry can all do synchronous work before
        // returning a Task. Keep them off the terminal thread. Only UpdateWorkbench changes UI state.
        _job = new(id, Task.Run(() => work(lifetime.Token), lifetime.Token), lifetime, cancellable);
        Dirty = true;
    }

    private void PreemptWorkbench()
    {
        _deferred = null;
        ClosePicker();
        _scrollTarget = null;
        if (_job is not { } job) return;
        job.PreemptedByApproval = true;
        if (job.Cancellable) job.Lifetime.Cancel();
    }

    private void UpdateWorkbench()
    {
        var releasing = _release is not null;
        Observe(ref _release, () => { _deferred = null; });
        if (releasing && _release is null && _deferred is { } deferred && _job is null)
        {
            _deferred = null;
            Run(deferred.Command, deferred.Argument);
        }
        if (_job is not { Task.IsCompleted: true } done) return;
        _job = null;
        Dirty = true;
        try
        {
            var result = done.Task.GetAwaiter().GetResult();
            if (result.Action?.Replacement is { } replacement && !ReferenceEquals(replacement, _active))
            {
                // A committed new/fork may already exist on disk. Keep the approval's session and release
                // the unused lease, even if its approval opened and closed while the switch was running.
                if (done.PreemptedByApproval || done.Lifetime.IsCancellationRequested)
                {
                    _release = Invoke(() => replacement.DisposeAsync().AsTask());
                    _lines.Add(new("info", $"保留当前会话。可用 /resume {replacement.Id} 打开目标会话。"));
                    return;
                }
                SwitchTo(replacement);
            }
            if (done.Lifetime.IsCancellationRequested) return;
            if (result.Choices is { } choices)
                OpenPicker(done.Id, result.Title ?? FuiCommands.Get(done.Id)?.Label ?? done.Id, choices);
            if (result.Prompt is { } prompt) Send(prompt);
            if (result.File is { } file)
            {
                if (_attachments.Count >= ProjectFiles.MaxAttachments && !_attachments.Any(x => SamePath(x.Path, file.Path)))
                    throw new InvalidOperationException($"每条消息最多附加 {ProjectFiles.MaxAttachments} 个文件。");
                _attachments.RemoveAll(x => SamePath(x.Path, file.Path));
                _attachments.Add(file);
                if (_mentionStart >= 0)
                {
                    var s = _composer.Text;
                    _composer.Replace(s.Remove(_mentionStart, _composer.Cursor - _mentionStart), _mentionStart);
                }
                _mentionStart = -1;
                Info($"已附加 {file.Path}");
            }
            if (result.Action?.Notice is { Length: > 0 } notice)
            {
                if (done.PreemptedByApproval) _lines.Add(new("info", notice));
                else Info(notice);
            }
            else if (result.Action?.Replacement is not null) Info(done.Id switch { "fork" => "已打开分叉会话。", "new" => "已新建会话。", _ => "已恢复会话。" });
        }
        catch (Exception) when (done.Cancellable && done.Lifetime.IsCancellationRequested) { ClosePicker(); }
        catch (Exception ex)
        {
            ClosePicker();
            if (done.PreemptedByApproval) _lines.Add(new("error", ex.GetBaseException().Message));
            else Error(ex.GetBaseException().Message);
        }
        finally { done.Lifetime.Dispose(); }
    }

    private void SwitchTo(ITuiSession next)
    {
        var prior = _active;
        if (_composer.Text.Length != 0 || _attachments.Count != 0)
            _drafts[prior.Id] = (_composer.Text, _composer.Cursor, [.. _attachments]);
        _active = next;
        ClosePicker(); _composer.Clear(); _attachments.Clear(); _lines.Clear(); _indices.Clear(); _deltas.Clear();
        ResetAgentActivity();
        if (_drafts.Remove(next.Id, out var saved)) { _composer.Replace(saved.Text, saved.Cursor); _attachments.AddRange(saved.Files); }
        _files = null; _lines.AddRange(next.History); _scroll = 0; _scrollTarget = null; _busy = false; _status = "就绪";
        _release = Invoke(() => prior.DisposeAsync().AsTask());
    }

    private static bool SamePath(string a, string b) => a.Equals(b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void SendWithContext(string prompt)
    {
        var attached = _attachments.ToArray();
        Begin("send-files", async ct => new(Prompt: await Files().PrepareAsync(prompt, attached, ct)), cancellable: true);
    }

    private void SubmitCommand(string text)
    {
        var words = text.AsSpan(1).TrimEnd();
        var sep = words.IndexOfAny(" \t\r\n");
        var id = (sep < 0 ? words : words[..sep]).ToString();
        if (id.Length == 0) id = "commands";
        if (FuiCommands.Get(id) is not { } command) { Error($"未知命令 /{id}；按 Tab 补全，或用 // 发送以 / 开头的原文。"); return; }
        var argument = sep < 0 ? "" : words[(sep + 1)..].Trim().ToString();
        if (command.NeedsText && argument.Length == 0) { Error($"用法：/{command.Id} {command.Argument}"); return; }
        if (argument.Length != 0 && command.Argument.Length == 0) { Error($"/{command.Id} 不接受参数。"); return; }
        if (DeferSessionCommand(command, argument)) return;
        if (!CanRun(command, argument)) return;
        _composer.Take();
        Run(command, argument);
    }

    private bool DeferSessionCommand(FuiCommand command, string argument)
    {
        var browsing = command.Picker && argument.Length == 0 && command.Id != "fork";
        if (browsing || _release is null || _job is not null || _busy || _send is not null || _stop is not null || _reply is not null)
            return false;
        if (command.Id is not ("new" or "resume" or "tree" or "fork" or "rename" or "export" or "model" or "thinking"))
            return false;
        _deferred = (command, argument);
        _status = "等待切换完成";
        _composer.Take();
        Dirty = true;
        return true;
    }

    private bool CanRun(FuiCommand c, string argument = "")
    {
        if (c.Id is "stop" or "quit") return true;
        if (_job is not null) { Error("上一项操作尚未结束。"); return false; }
        if (c.Kind == CommandKind.Session && _active is not IFuiWorkbenchSession) { Error("当前会话未提供此操作。"); return false; }
        if (c.Id == "steer" && !_busy) { Error("仅能向运行中的回合插话。"); return false; }
        var browsing = c.Picker && argument.Length == 0 && c.Id != "fork";
        if (!browsing && c.Id is ("new" or "resume" or "tree" or "fork" or "rename" or "export" or "model" or "thinking"))
        {
            Observe(ref _release, () => { });
            if (_busy || _send is not null || _stop is not null || _reply is not null || _release is not null)
            { Error("请在回合和上次切换结束后再执行此操作。"); return false; }
        }
        return true;
    }

    private void Run(FuiCommand c, string argument)
    {
        if (c.Kind == CommandKind.Session)
        {
            var backend = (IFuiWorkbenchSession)_active;
            if (c.Picker && argument.Length == 0)
                Begin(c.Id, async ct => new(Choices: await backend.ChoicesAsync(c.Id, ct)), cancellable: true);
            else Begin(c.Id, async ct => new(Action: await backend.ExecuteAsync(c.Id, argument, ct)),
                cancellable: c.Id == "usage" || (c.Id == "queue" && argument.Length == 0));
            return;
        }
        if (c.Kind == CommandKind.Files)
        {
            if (c.Id == "detach")
            {
                if (argument.Length == 0) OpenPicker("detach", c.Label, _attachments.Select(a => new FuiChoice(a.Path, a.Path)).ToArray());
                else if (_attachments.RemoveAll(a => SamePath(a.Path, argument)) != 0) Info($"已移除 {argument}");
                else Error("该文件不在当前草稿中。");
                return;
            }
            if (argument.Length == 0 || argument.EndsWith('/')) { Browse(c.Id, argument.Length == 0 ? "." : argument); return; }
            var path = argument.Length >= 2 && argument[0] == '"' && argument[^1] == '"' ? argument[1..^1] : argument;
            if (c.Id == "attach")
            {
                if (_attachments.Count >= ProjectFiles.MaxAttachments && !_attachments.Any(a => SamePath(a.Path, path)))
                { Error($"每条消息最多附加 {ProjectFiles.MaxAttachments} 个文件。"); return; }
                Begin(c.Id, async ct => new(File: await Files().ReadAttachmentAsync(path, ct)), cancellable: true);
            }
            else if (c.Id == "diff") Begin(c.Id, async ct => new(Action: new(await Files().DiffAsync(path, ct))), cancellable: true);
            else Begin(c.Id, async ct => { var file = await Files().ReadAsync(path, ct); return new(Action: new($"{file.Path}\n{file.Text}")); }, cancellable: true);
            return;
        }
        switch (c.Id)
        {
            case "commands": Commands(); break;
            case "help": Info(FuiCommands.Help); break;
            case "session": Info($"FUMO · {_active.Id}\n{_active.Project}\n{_active.Model ?? "未配置模型"}"); break;
            case "context": Info(_attachments.Count == 0 ? "草稿没有附加文件。" : string.Join('\n', _attachments.Select(a => a.Path))); break;
            case "tools": _expandTools = !_expandTools; break;
            case "thoughts": _showThinking = !_showThinking; break;
            case "agents": Agents(); break;
            case "plan": Plan(c.Id); break;
            case "todo": Plan(c.Id); break;
            case "stats": Stats(); break;
            case "changes": Changes(argument); break;
            case "review": Review(); break;
            case "history": OpenPicker(c.Id, c.Label, _composer.History.Reverse().Where(s => !s.StartsWith("/history", StringComparison.OrdinalIgnoreCase)).Select(s => new FuiChoice(s, s)).ToArray(), argument); break;
            case "find": OpenPicker(c.Id, c.Label, _lines.Select((l, i) => new FuiChoice(i.ToString(), l.Text, l.Kind)).Reverse().ToArray(), argument); break;
            case "links":
                var links = MarkdownLinks.Choices(_lines);
                if (links.Count == 0) Info("当前会话没有链接目标。");
                else OpenPicker(c.Id, c.Label, links, argument,
                    new FuiPickerOptions(Detail: choice => choice.Detail ?? choice.Value, PreserveOrder: true));
                break;
            case "stop": Stop(); break;
            case "quit": Exiting = true; break;
        }
    }

    private void Browse(string action, string folder, string filter = "")
    {
        _browseFilter = filter;
        Begin(action, ct =>
        {
            var page = Files().List(folder, ct);
            return Task.FromResult(new Result(Choices: page.Items, Title: $"{FuiCommands.Get(action)!.Label} · {page.Folder}"));
        }, cancellable: true);
    }
}
