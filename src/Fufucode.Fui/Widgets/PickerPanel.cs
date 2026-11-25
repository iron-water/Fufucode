namespace Fufucode.Fui;

internal static class PickerPanel
{
    internal static int DesiredRows(FuiOverlay overlay)
    {
        var count = overlay.Items.Count;
        if (FuiAgentActivityRoster.IsHubTitle(overlay.Title))
            return 1 + Math.Min(count, 7) + 5;
        var footer = string.IsNullOrWhiteSpace(overlay.Footer) ? 0 : 1;
        if (count == 0) return 2 + footer;
        var selected = overlay.Items[Math.Clamp(overlay.Selected, 0, count - 1)];
        var rowLimit = overlay.Kind == FuiOverlayKind.Picker ? 9 : 12;
        var desired = 1 + Math.Min(count, rowLimit) +
            (string.IsNullOrWhiteSpace(overlay.Message ?? selected.Detail) ? 0 : 1) + footer;
        return overlay.Kind != FuiOverlayKind.Picker ? Math.Max(desired, 8) : desired;
    }

    internal static int Paint(FuiOverlay overlay, Span<ScreenRow> rows, int columns)
    {
        if (rows.Length == 0) return 0;
        var count = overlay.Items.Count;
        var selected = Math.Clamp(overlay.Selected, 0, Math.Max(0, count - 1));
        var footerRows = !string.IsNullOrWhiteSpace(overlay.Footer) && rows.Length >= 2 ? 1 : 0;
        if (footerRows != 0)
            rows[^1] = new(TerminalText.Clip("  " + TerminalText.SingleLine(overlay.Footer!), columns), Ink.Quiet);
        var content = footerRows == 0 ? rows : rows[..^footerRows];
        if (columns >= 80 && count > 0 &&
            (content.Length >= 6 || overlay.Kind != FuiOverlayKind.Picker && content.Length >= 3))
            return PaintSplit(overlay, rows, columns, selected);
        string Fit(string s, bool tail = false) => TerminalText.Clip(s, columns, tail);
        var title = TerminalText.SingleLine(overlay.Title);
        var query = TerminalText.SingleLine(overlay.Query);
        var progress = FuiAgentActivityRoster.IsHubTitle(overlay.Title) && count != 0
            ? AgentProgress(overlay, selected)
            : $"{(count == 0 ? 0 : selected + 1)}/{count}";
        var available = Math.Max(0, columns - progress.Length - 2);
        var titleWidth = query.Length == 0 ? available : Math.Max(0, available / 2 - 2);
        var left = TerminalText.Clip(title, titleWidth);
        if (query.Length > 0) left += "  /" + TerminalText.Clip(query, available - TerminalText.Width(left) - 3, tail: true);
        if (content.Length == 1)
        {
            content[0] = new(Fit(count == 0 ? overlay.Message is null ? "没有匹配项" : TerminalText.SingleLine(overlay.Message) : "▸ " + Label(overlay.Items[selected], columns)), Ink.Selection);
            return Math.Min(1, count);
        }
        content[0] = new(Fit(left + new string(' ', Math.Max(1, columns - TerminalText.Width(left) - progress.Length)) + progress), Ink.PanelTitle);
        if (count == 0)
        {
            content[1] = new(Fit(TerminalText.SingleLine(overlay.Message ?? "没有匹配项")), Ink.Quiet);
            return 0;
        }

        var detail = FuiAgentActivityRoster.IsHubTitle(overlay.Title)
            ? TerminalText.SingleLine(overlay.Items[selected].Detail ?? "")
            : TerminalText.SingleLine(overlay.Message ?? overlay.Items[selected].Detail ?? "");
        var hasDetail = detail.Length > 0 && content.Length >= 3;
        var visible = Math.Max(1, Math.Min(count, content.Length - 1 - (hasDetail ? 1 : 0)));
        var start = Math.Clamp(selected - visible / 2, 0, Math.Max(0, count - visible));
        for (var i = 0; i < visible; i++)
        {
            var index = start + i;
            var active = index == selected;
            content[i + 1] = new(Fit((active ? "▸ " : "  ") + Label(overlay.Items[index], columns)),
                active ? Ink.Selection : FuiAgentActivityRoster.IsHubTitle(overlay.Title)
                    ? FuiAgentActivityRoster.InkFor(overlay.Items[index])
                    : Ink.Plain);
        }
        if (hasDetail) content[visible + 1] = new(Fit("  " + detail), Ink.Quiet);
        return visible;
    }

    private static int PaintSplit(FuiOverlay overlay, Span<ScreenRow> rows, int columns, int selected)
    {
        var count = overlay.Items.Count;
        var leftWidth = Math.Clamp(columns / 2, 30, 56);
        var rightWidth = columns - leftWidth - 1;
        if (rightWidth < 18) return Paint(overlay, rows, columns);

        var title = TerminalText.SingleLine(overlay.Title);
        var query = TerminalText.SingleLine(overlay.Query);
        var agentHub = FuiAgentActivityRoster.IsHubTitle(overlay.Title);
        var progress = agentHub ? AgentProgress(overlay, selected) : $"{selected + 1}/{count}";
        var leftTitle = title + (query.Length == 0 ? "" : " /" + query);
        var footerRows = string.IsNullOrWhiteSpace(overlay.Footer) ? 0 : 1;
        var detailHead = overlay.Kind switch
        {
            FuiOverlayKind.Transcript => "对话 · 正文",
            FuiOverlayKind.Outline => "大纲 · 正文",
            FuiOverlayKind.Copy => "正文",
            _ => "详情"
        };
        rows[0] = new(Pad(TerminalText.Clip(leftTitle, leftWidth - progress.Length - 1), leftWidth) +
            "│" + Pad(" " + progress + (agentHub ? "  作业 · 最近事件" : "  " + detailHead), rightWidth), Ink.PanelTitle);

        var detail = new List<TerminalText.Slice>();
        var selectedChoice = overlay.Items[selected];
        var detailText = agentHub
            ? TerminalText.Clean(overlay.Message ?? selectedChoice.Detail ?? "尚无事件")
            : TerminalText.Clean(string.Join('\n', new[]
        {
            Label(selectedChoice, leftWidth),
            selectedChoice.Value == Label(selectedChoice, leftWidth) ? "" : selectedChoice.Value,
            overlay.Message ?? selectedChoice.Detail ?? "",
            selectedChoice.TreePosition is { } position ? $"层级 {position.Depth}" : ""
        }.Where(value => value.Length != 0)));
        TerminalText.Wrap(detailText, rightWidth, 0, detail);

        var bodyRows = rows.Length - 1 - footerRows;
        var visible = Math.Max(1, Math.Min(count, bodyRows));
        var start = Math.Clamp(selected - visible / 2, 0, Math.Max(0, count - visible));
        for (var i = 0; i < bodyRows; i++)
        {
            var hasItem = i < visible;
            var index = start + i;
            var active = hasItem && index == selected;
            var choice = hasItem ? overlay.Items[index] : null;
            var left = choice is null ? "" : (active ? "▸ " : "  ") + Label(choice, leftWidth);
            var right = i < detail.Count ? detail[i].Text : "";
            rows[i + 1] = new(Pad(TerminalText.Clip(left, leftWidth), leftWidth) + "│" +
                Pad(TerminalText.Clip(right, rightWidth), rightWidth),
                active ? Ink.Selection : choice is not null && agentHub
                    ? FuiAgentActivityRoster.InkFor(choice)
                    : Ink.Plain);
        }
        return visible;
    }

    private static string AgentProgress(FuiOverlay overlay, int selected)
    {
        var total = 0;
        var current = 0;
        for (var i = 0; i < overlay.Items.Count; i++)
        {
            if (FuiAgentActivityRoster.IsGroupChoice(overlay.Items[i])) continue;
            total++;
            if (i <= selected) current = total;
        }
        return $"{Math.Max(1, current)}/{total}";
    }

    private static string Label(FuiChoice choice, int columns)
    {
        var name = TerminalText.SingleLine(string.IsNullOrWhiteSpace(choice.Label) ? choice.Value : choice.Label);
        if (choice.TreePosition is not { } position || columns < 6) return name;
        var indentBudget = Math.Min(16, (columns - 2) / 3);
        var indent = position.Depth <= indentBudget / 2 ? new string(' ', position.Depth * 2) :
            TerminalText.Clip($"…{position.Depth} ", indentBudget);
        var marker = position.Expanded switch { true => "▾ ", false => "▸ ", null => "· " };
        return indent + marker + name;
    }

    private static string Pad(string text, int width)
    {
        var clipped = TerminalText.Clip(text, width);
        return clipped + new string(' ', Math.Max(0, width - TerminalText.Width(clipped)));
    }
}
