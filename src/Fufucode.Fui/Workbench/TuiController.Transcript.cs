namespace Fufucode.Fui;

internal sealed partial class TuiController
{
    private const string OutlineFooter = "↑↓ 选择 · Enter 定位 · F5 刷新 · Esc 关闭";
    private const string TranscriptFooter = "Tab 过滤 · ↑↓ 选择 · Enter 定位 · Esc 关闭";
    private const string CopyFooter = "↑↓ 选择 · Enter 复制 · Esc 关闭";
    private FuiTranscriptFilter _transcriptFilter;
    private FuiCopyCatalog _copyCatalog = FuiCopyCatalog.Empty;
    private FuiTranscriptCatalog _transcriptCatalog = FuiTranscriptCatalog.Empty;
    private long _transcriptGeneration;
    private long _outlineGeneration = -1;
    private long _outlineRefreshedAt;

    private void AppendLine(string kind, string text, string? source = null)
    {
        _lines.Add(new(kind, text));
        _lineSources.Add(source);
        _transcriptGeneration++;
        Dirty = true;
    }

    private void OpenOutline()
    {
        var lines = _lines.ToArray();
        var sources = _lineSources.ToArray();
        Begin("outline", ct => Task.Run(() => new Result(
            Choices: FuiTranscriptWorkbench.OutlineChoices(FuiTranscriptWorkbench.Build(lines, sources))), ct), cancellable: true);
    }

    private void OpenCopy()
    {
        var lines = _lines.ToArray();
        var sources = _lineSources.ToArray();
        var session = _active.Id;
        Begin("copy", ct => Task.Run(() => new Result(
            Copy: FuiTranscriptWorkbench.BuildCopy(lines, sources, session)), ct), cancellable: true);
    }

    private void OpenTranscript()
    {
        var lines = _lines.ToArray();
        var sources = _lineSources.ToArray();
        Begin("transcript", ct => Task.Run(() =>
        {
            var catalog = FuiTranscriptWorkbench.Build(lines, sources);
            return new Result(Transcript: catalog);
        }, ct), cancellable: true);
    }

    private void OpenInfo()
    {
        if (_active is not IFuiSessionInspector inspector)
        {
            Error("当前会话没有可证明的会话详情接口。");
            return;
        }
        var drafts = _attachments.Count;
        Begin("info", async ct => new Result(Action: new(
            FuiTranscriptWorkbench.FormatInfo(await inspector.InspectAsync(ct), drafts))), cancellable: true);
    }

    private void Retry()
    {
        if (_active is not IFuiSessionInspector inspector)
        {
            Error("当前会话没有可证明的重试接口。");
            return;
        }
        Begin("retry", async ct =>
        {
            var text = await inspector.RetryLastUserAsync(ct);
            return new Result(Action: new($"已重发上一条用户消息：{FuiTranscriptWorkbench.MarkdownSummary(text)}"));
        });
    }

    private void OpenOutlinePicker(IReadOnlyList<FuiChoice> choices)
    {
        _outlineGeneration = -1;
        OpenPicker("outline", "会话大纲", choices, options: new(
            PreserveOrder: true, Footer: OutlineFooter, Kind: FuiOverlayKind.Outline));
    }

    private void OpenCopyPicker(FuiCopyCatalog catalog)
    {
        _copyCatalog = catalog;
        OpenPicker("copy", "复制", catalog.Choices, options: new(
            PreserveOrder: true, Footer: CopyFooter, Kind: FuiOverlayKind.Copy));
    }

    private void OpenTranscriptPicker(FuiTranscriptCatalog catalog)
    {
        _transcriptCatalog = catalog;
        OpenPicker("transcript", "对话 · " + FuiTranscriptWorkbench.FilterLabel(_transcriptFilter),
            catalog.Choices(_transcriptFilter), options: new(
                PreserveOrder: true, Footer: TranscriptFooter, Kind: FuiOverlayKind.Transcript));
    }

    private void CycleTranscriptFilter(bool backward)
    {
        if (_picker is not { } picker) return;
        var query = picker.View.Query;
        _transcriptFilter = FuiTranscriptWorkbench.NextFilter(_transcriptFilter, backward);
        _picker = new("对话 · " + FuiTranscriptWorkbench.FilterLabel(_transcriptFilter),
            _transcriptCatalog.Choices(_transcriptFilter), query, options: new(
                PreserveOrder: true, Footer: TranscriptFooter, Kind: FuiOverlayKind.Transcript));
        Dirty = true;
    }

    private void RefreshOutline(bool force)
    {
        if (_pickerAction != "outline" || _picker is not { } picker) return;
        var now = Environment.TickCount64;
        if (!force && (_transcriptGeneration == _outlineGeneration || now - _outlineRefreshedAt < 350)) return;
        var lines = _lines.ToArray();
        var sources = _lineSources.ToArray();
        picker.ReplaceChoices(FuiTranscriptWorkbench.OutlineChoices(FuiTranscriptWorkbench.Build(lines, sources)));
        _outlineGeneration = _transcriptGeneration;
        _outlineRefreshedAt = now;
        Dirty = true;
    }

    private void RefreshOpenTranscriptPanels()
    {
        if (_pickerAction == "outline") RefreshOutline(force: false);
    }

    private void LocateTranscriptChoice(FuiChoice choice)
    {
        var separator = choice.Value.IndexOf(':');
        if (separator < 0 || !int.TryParse(choice.Value.AsSpan(separator + 1), out var index) ||
            (uint)index >= (uint)_lines.Count)
        {
            Error("所选项已不在当前对话中，未跳转。");
            return;
        }
        _scrollTarget = index;
        if (_lines[index].Kind == "thinking") _showThinking = true;
        if (_lines[index].Kind == "tool") _expandTools = true;
        _status = "已定位 · " + TerminalText.SingleLine(choice.Label);
    }

    private void CopyChoice(FuiChoice choice)
    {
        if (!_copyCatalog.Targets.TryGetValue(choice.Value, out var target))
        {
            Error("复制目标已失效，请重新打开 /copy。");
            return;
        }
        var result = _clipboard.CopyText(target.Text);
        if (!result.Success)
        {
            Error($"复制失败：{result.Error ?? "未知原因"}");
            return;
        }
        Info($"已复制 {result.Characters} 字符 · {result.Bytes} 字节（UTF-8）");
    }
}
