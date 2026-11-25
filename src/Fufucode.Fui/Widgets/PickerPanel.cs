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
        string Label(FuiChoice c)
        {
            var name = TerminalText.SingleLine(string.IsNullOrWhiteSpace(c.Label) ? c.Value : c.Label);
            if (c.TreePosition is not { } position || columns < 6) return name;
            var indentBudget = Math.Min(16, (columns - 2) / 3);
            var indent = position.Depth <= indentBudget / 2 ? new string(' ', position.Depth * 2) :
                TerminalText.Clip($"…{position.Depth} ", indentBudget);
            var marker = position.Expanded switch { true => "▾ ", false => "▸ ", null => "· " };
            return indent + marker + name;
        }
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
            rows[0] = new(Fit(count == 0 ? overlay.Message is null ? "没有匹配项" : TerminalText.SingleLine(overlay.Message) : "▸ " + Label(overlay.Items[selected])), Ink.Selection);
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
            rows[i + 1] = new(Fit((active ? "▸ " : "  ") + Label(overlay.Items[index])), active ? Ink.Selection : Ink.Plain);
        }
        if (hasDetail) rows[visible + 1] = new(Fit("  " + detail), Ink.Quiet);
        return visible;
    }
}
