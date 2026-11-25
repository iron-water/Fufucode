namespace Fufucode.Fui;

internal sealed partial class TuiController
{
    private string _browseFilter = "";
    private void Commands() => OpenPicker("commands", "命令", FuiCommands.All.Select(FuiCommands.Choice).ToArray());

    private void OpenPicker(string id, string title, IReadOnlyList<FuiChoice> choices, string query = "",
        FuiPickerOptions? options = null)
    {
        if (id is "attach" or "preview" or "diff") { query = _browseFilter; _browseFilter = ""; }
        _pickerAction = id;
        _picker = new(title, choices, query, currentTreeSession: id == "tree" ? _active.Id : null, options);
        Dirty = true;
    }

    private void ClosePicker()
    {
        _picker = null; _pickerAction = null; _browseFilter = ""; _mentionStart = -1;
    }

    private FuiOverlay? Overlay()
    {
        if (_approvals.Count != 0) return null;
        if (_picker is not null) return _picker.View;
        var text = _composer.Text;
        if (_job is not null || !text.StartsWith('/') || text.StartsWith("//", StringComparison.Ordinal) ||
            text.StartsWith("/skill:", StringComparison.Ordinal) || text.Any(char.IsWhiteSpace) || FuiCommands.Get(text[1..]) is not null)
            return null;
        var matches = FuiCommands.All.Where(c => c.Id.StartsWith(text[1..], StringComparison.OrdinalIgnoreCase)).Select(FuiCommands.Choice).ToArray();
        return matches.Length == 0 ? null : new("命令 · Tab 补全", "", matches, 0);
    }

    private bool WorkbenchKey(ConsoleKeyInfo key, int pageSize)
    {
        var ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (_picker is not null)
        {
            if (key.Key == ConsoleKey.Escape || ctrl && key.Key == ConsoleKey.C) { ClosePicker(); return true; }
            if (key.Key == ConsoleKey.Enter && key.Modifiers == 0)
            {
                if (_picker.Current is not { } choice) return true;
                var id = _pickerAction!; var mention = _mentionStart;
                ClosePicker();
                if (id == "attach") _mentionStart = mention;
                Accept(id, choice);
            }
            else _picker.Edit(key, Math.Max(1, pageSize));
            return true;
        }
        if (_job is { } job)
        {
            if ((key.Key == ConsoleKey.Escape || ctrl && key.Key == ConsoleKey.C) && job.Cancellable)
            { job.Lifetime.Cancel(); ClosePicker(); return true; }
            if (ctrl && key.Key == ConsoleKey.C && _busy) { Stop(); return true; }
            if (job.Id == "send-files" || _mentionStart >= 0)
            {
                // The user can still edit a pending @ mention or file submission. An edit supersedes
                // the read that was prepared against the old text/caret.
                var revision = _composer.Revision;
                var cursorBefore = _composer.Cursor;
                _composer.Edit(key);
                if (_composer.Revision != revision || _composer.Cursor != cursorBefore)
                {
                    job.Lifetime.Cancel();
                    ClosePicker();
                }
                return true;
            }
        }
        if (ctrl && key.Key is (ConsoleKey.P or ConsoleKey.R or ConsoleKey.F))
        {
            if (_job is not null) return true;
            if (key.Key == ConsoleKey.P) Commands();
            else Run(FuiCommands.Get(key.Key == ConsoleKey.R ? "history" : "find")!, "");
            return true;
        }
        if (key.Key != ConsoleKey.Tab || key.Modifiers != 0 || _job is not null) return false;
        var text = _composer.Text;
        if (text.StartsWith('/') && !text.StartsWith("//", StringComparison.Ordinal) && !text.Any(char.IsWhiteSpace))
        {
            var matches = FuiCommands.All.Where(c => c.Id.StartsWith(text[1..], StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) Complete(matches[0]);
            else if (matches.Length != 0) OpenPicker("complete", "补全命令", matches.Select(FuiCommands.Choice).ToArray());
            return true;
        }
        int end = _composer.Cursor, start = end;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        if (start == end || text[start] != '@') return false;
        var path = text[(start + 1)..end].Replace('\\', '/');
        var slash = path.LastIndexOf('/');
        _mentionStart = start;
        Browse("attach", slash < 0 ? "." : path[..(slash + 1)], path[(slash + 1)..]);
        return true;
    }

    private void Complete(FuiCommand c) => _composer.Replace("/" + c.Id + (c.NeedsText ? " " : ""));

    private void Accept(string action, FuiChoice choice)
    {
        switch (action)
        {
            case "complete": Complete(FuiCommands.Get(choice.Value)!); return;
            case "commands":
                var c = FuiCommands.Get(choice.Value)!;
                if (!CanRun(c)) return;
                if (c.NeedsText)
                {
                    if (_composer.Text.Length != 0 && c.Id != "steer") { Error("请先发送或清空草稿，再输入命令参数。"); return; }
                    _composer.Replace("/" + c.Id + " " + _composer.Text);
                }
                else if (c.Id == "queue" && _busy && _composer.Text.Length != 0)
                    _composer.Replace("/queue " + _composer.Text); // Offer follow-up editing; choosing the palette item never sends a draft.
                else Run(c, "");
                return;
            case "find":
                if (int.TryParse(choice.Value, out var index) && (uint)index < (uint)_lines.Count)
                {
                    _scrollTarget = index;
                    if (_lines[index].Kind == "thinking") _showThinking = true;
                    if (_lines[index].Kind == "tool") _expandTools = true;
                }
                return;
            case "history": _composer.Replace(choice.Value); return;
            case "skills": _composer.Replace("/" + choice.Value.TrimStart('/') + " " + _composer.Text); return;
            case "capabilities": Info(choice.Label + (choice.Detail is null ? "" : "\n" + choice.Detail)); return;
            case "agents": ShowAgentDetail(choice); return;
            case "plan" or "todo": JumpToPlan(choice); return;
            case "links":
                if (!TuiHyperlinkPolicy.IsSafeUrl(choice.Value)) { Error("该链接目标不是可复制的 HTTP(S) URL。"); return; }
                OpenPicker("links-copy", "链接目标", [new(choice.Value, "复制 URL", choice.Value)],
                    options: new FuiPickerOptions(Detail: item => item.Detail ?? item.Value, PreserveOrder: true));
                return;
            case "links-copy":
                try
                {
                    _clipboard.Copy(choice.Value);
                    Info("已复制链接\n" + choice.Value);
                }
                catch (Exception error) { Error("复制链接失败：" + error.GetBaseException().Message); }
                return;
            default:
                if (FuiCommands.Get(action) is { } command && CanRun(command, choice.Value)) Run(command, choice.Value);
                return;
        }
    }
}
