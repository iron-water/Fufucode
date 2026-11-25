namespace Fufucode.Fui;

internal sealed partial class TuiController
{
    private FuiCompletionPopup? _completion;
    private string? _completionProject;
    private IReadOnlyList<string>? _completionPaths;
    private long _pendingCompletionRefresh;

    private bool CompletionKey(ConsoleKeyInfo key)
    {
        if (_completion is null) return false;
        if (key.Key == ConsoleKey.Escape)
        {
            CloseCompletion();
            return true;
        }
        if (key.Key == ConsoleKey.UpArrow && key.Modifiers == 0) { _completion.Move(-1); return true; }
        if (key.Key == ConsoleKey.DownArrow && key.Modifiers == 0) { _completion.Move(1); return true; }
        if (key.Key == ConsoleKey.PageUp && key.Modifiers == 0) { _completion.Move(-5); return true; }
        if (key.Key == ConsoleKey.PageDown && key.Modifiers == 0) { _completion.Move(5); return true; }
        if (key.Key is ConsoleKey.Enter or ConsoleKey.Tab && key.Modifiers == 0)
        {
            AcceptCompletion();
            return true;
        }
        if (key.Key == ConsoleKey.Spacebar && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            OpenWordCompletion(announceIfEmpty: true);
            return true;
        }
        if (key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.Home or ConsoleKey.End)
            CloseCompletion();
        return false;
    }

    private void OpenWordCompletion(bool announceIfEmpty)
    {
        if (_picker is not null || _approvals.Count != 0 || _job is not null) return;
        var token = WordToken();
        var choices = WordChoices(token);
        CloseCompletion();
        if (choices.Count == 0)
        {
            if (announceIfEmpty) _status = "没有本地词补全候选";
            return;
        }
        _completion = new(FuiCompletionKind.Word, "词补全 · 本地", token.Start, token.End, token.Query, choices);
        Dirty = true;
    }

    private void RefreshCompletionAfterEdit()
    {
        if (_completion is { Kind: FuiCompletionKind.Word } word)
        {
            var token = WordToken();
            if (token.Start != word.Start || token.Query.Length == 0)
            {
                CloseCompletion();
                if (_settings.EmojiAutocomplete == "auto") RefreshEmojiCompletion();
                return;
            }
            var choices = WordChoices(token);
            if (choices.Count == 0)
            {
                CloseCompletion();
                return;
            }
            _completion = new(FuiCompletionKind.Word, "词补全 · 本地", token.Start, token.End, token.Query, choices);
            return;
        }

        if (_completion is { Kind: FuiCompletionKind.Emoji })
            RefreshEmojiCompletion();
        else if (_settings.EmojiAutocomplete == "auto")
            RefreshEmojiCompletion();
    }

    private void RefreshEmojiCompletion()
    {
        if (_settings.EmojiAutocomplete != "auto" ||
            !EmojiShortcodes.TryTrigger(_composer.Text, _composer.Cursor, out var start, out var end, out var query))
        {
            if (_completion is { Kind: FuiCompletionKind.Emoji }) CloseCompletion();
            return;
        }
        var choices = FuiCompletionCatalog.EmojiChoices(query);
        if (choices.Count == 0)
        {
            if (_completion is { Kind: FuiCompletionKind.Emoji }) CloseCompletion();
            return;
        }
        _completion = new(FuiCompletionKind.Emoji, "Emoji 短码 · 本地", start, end, query, choices);
        Dirty = true;
    }

    private CompletionToken WordToken() =>
        FuiCompletionCatalog.TryWordToken(_composer.Text, _composer.Cursor, out var token) ? token : default;

    private IReadOnlyList<FuiChoice> WordChoices(CompletionToken token)
    {
        if (_completionPaths is null || !string.Equals(_completionProject, _active.Project, StringComparison.Ordinal))
        {
            _completionProject = _active.Project;
            try { _completionPaths = Files().CompletionPaths(CancellationToken.None); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _completionPaths = [];
            }
        }
        return FuiCompletionCatalog.WordChoices(token, _lines.Select(line => line.Text),
            _composer.History, _completionPaths);
    }

    private void AcceptCompletion()
    {
        if (_completion?.Current is not { } choice) { CloseCompletion(); return; }
        var completion = _completion;
        CloseCompletion();
        PrepareComposerMutation();
        _composer.ReplaceRange(completion.Start, completion.End, choice.Value);
        _status = completion.Kind == FuiCompletionKind.Emoji
            ? $"已补全 {choice.Value}"
            : $"已补全 {TerminalText.Clip(choice.Value, 36, tail: true)}";
        Dirty = true;
    }

    private void CloseCompletion()
    {
        _pendingCompletionRefresh = 0;
        if (_completion is null) return;
        _completion = null;
        Dirty = true;
    }

    private void ScheduleCompletionRefresh()
    {
        if (_settings.EmojiAutocomplete == "auto") _pendingCompletionRefresh = Environment.TickCount64 + 50;
    }

    private void RefreshPendingCompletion()
    {
        if (_pendingCompletionRefresh == 0 || Environment.TickCount64 < _pendingCompletionRefresh) return;
        _pendingCompletionRefresh = 0;
        if (_completion is null && _settings.EmojiAutocomplete == "auto") RefreshEmojiCompletion();
    }

    private void PrepareComposerMutation()
    {
        if (_job is not { } job || (job.Id != "send-files" && _mentionStart < 0)) return;
        job.Lifetime.Cancel();
        ClosePicker();
    }

    private FuiOverlay? CompletionOverlay() => _completion?.View;
}
