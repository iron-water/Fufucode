namespace Fufucode.Fui;

internal static class PickerPanel
{
    internal static int DesiredRows(FuiOverlay overlay)
    {
        var count = overlay.Items.Count;
        if (count == 0) return 2;
        var selected = overlay.Items[Math.Clamp(overlay.Selected, 0, count - 1)];
        return 1 + Math.Min(count, 9) + (string.IsNullOrWhiteSpace(overlay.Message ?? selected.Detail) ? 0 : 1);
    }

    internal static int Paint(FuiOverlay overlay, Span<ScreenRow> rows, int columns)
    {
        if (rows.Length == 0) return 0;
        var count = overlay.Items.Count;
        var selected = Math.Clamp(overlay.Selected, 0, Math.Max(0, count - 1));
        if (columns >= 80 && rows.Length >= 6 && count > 0)
            return PaintSplit(overlay, rows, columns, selected);
        string Fit(string s, bool tail = false) => TerminalText.Clip(s, columns, tail);
        var title = TerminalText.SingleLine(overlay.Title);
        var query = TerminalText.SingleLine(overlay.Query);
        var progress = $"{(count == 0 ? 0 : selected + 1)}/{count}";
        var available = Math.Max(0, columns - progress.Length - 2);
        var titleWidth = query.Length == 0 ? available : Math.Max(0, available / 2 - 2);
        var left = TerminalText.Clip(title, titleWidth);
        if (query.Length > 0) left += "  /" + TerminalText.Clip(query, available - TerminalText.Width(left) - 3, tail: true);
        if (rows.Length == 1)
        {
            rows[0] = new(Fit(count == 0 ? overlay.Message is null ? "没有匹配项" : TerminalText.SingleLine(overlay.Message) : "▸ " + Label(overlay.Items[selected], columns)), Ink.Selection);
            return Math.Min(1, count);
        }
        rows[0] = new(Fit(left + new string(' ', Math.Max(1, columns - TerminalText.Width(left) - progress.Length)) + progress), Ink.PanelTitle);
        if (count == 0)
        {
            rows[1] = new(Fit(TerminalText.SingleLine(overlay.Message ?? "没有匹配项")), Ink.Quiet);
            return 0;
        }

        var detail = TerminalText.SingleLine(overlay.Message ?? overlay.Items[selected].Detail ?? "");
        var hasDetail = detail.Length > 0 && rows.Length >= 3;
        var visible = Math.Max(1, Math.Min(count, rows.Length - 1 - (hasDetail ? 1 : 0)));
        var start = Math.Clamp(selected - visible / 2, 0, Math.Max(0, count - visible));
        for (var i = 0; i < visible; i++)
        {
            var index = start + i;
            var active = index == selected;
            rows[i + 1] = new(Fit((active ? "▸ " : "  ") + Label(overlay.Items[index], columns)), active ? Ink.Selection : Ink.Plain);
        }
        if (hasDetail) rows[visible + 1] = new(Fit("  " + detail), Ink.Quiet);
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
        var progress = $"{selected + 1}/{count}";
        var leftTitle = title + (query.Length == 0 ? "" : " /" + query);
        rows[0] = new(Pad(TerminalText.Clip(leftTitle, leftWidth - progress.Length - 1), leftWidth) +
            "│" + Pad(" " + progress + "  详情", rightWidth), Ink.PanelTitle);

        var detail = new List<TerminalText.Slice>();
        var selectedChoice = overlay.Items[selected];
        var detailText = TerminalText.Clean(string.Join('\n', new[]
        {
            Label(selectedChoice, leftWidth),
            selectedChoice.Value == Label(selectedChoice, leftWidth) ? "" : selectedChoice.Value,
            overlay.Message ?? selectedChoice.Detail ?? "",
            selectedChoice.TreePosition is { } position ? $"层级 {position.Depth}" : ""
        }.Where(value => value.Length != 0)));
        TerminalText.Wrap(detailText, rightWidth, 0, detail);

        var visible = Math.Max(1, Math.Min(count, rows.Length - 1));
        var start = Math.Clamp(selected - visible / 2, 0, Math.Max(0, count - visible));
        for (var i = 0; i < visible; i++)
        {
            var index = start + i;
            var active = index == selected;
            var left = (active ? "▸ " : "  ") + Label(overlay.Items[index], leftWidth);
            var right = i < detail.Count ? detail[i].Text : "";
            rows[i + 1] = new(Pad(TerminalText.Clip(left, leftWidth), leftWidth) + "│" +
                Pad(TerminalText.Clip(right, rightWidth), rightWidth), active ? Ink.Selection : Ink.Plain);
        }
        return visible;
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
