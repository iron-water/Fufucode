namespace Fufucode.Fui;

internal enum Ink { Plain, Header, User, Tool, Quiet, Error, Status, Approval, Composer, PanelTitle, Selection, Heading, Code, CodeFence }
internal readonly record struct ImageStrip(int Column, string Sixel);
internal readonly record struct ScreenRow(string Text, Ink Ink = Ink.Plain, IReadOnlyList<ImageStrip>? Images = null, int ImageIndent = 0);
internal sealed record TerminalFrame(int Width, ScreenRow[] Rows, int CursorRow, int CursorColumn, int ViewportRows, int MaxScroll,
    int PickerRows = 0, bool CanApprove = false);

internal sealed class TerminalLayout
{
    private readonly List<MessageBlock> _blocks = [];
    private int _columns;
    private ImageCells? _cell;
    private int _total, _bodyRows;

    internal int ScrollToLine(int transcriptIndex)
    {
        if (_blocks.Count == 0) return 0;
        var index = Math.Clamp(transcriptIndex, 0, _blocks.Count - 1);
        return Math.Clamp(_total - _bodyRows - _blocks[index].First, 0, Math.Max(0, _total - _bodyRows));
    }

    internal TerminalFrame Arrange(TuiView view, int width, int height, ImageCells? cell = null)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var columns = width - 1; // leave the terminal's wrap-pending cell unused, even on the bottom line
        var rows = new ScreenRow[height];
        Array.Fill(rows, new(""));
        var draft = TerminalText.Clean(view.Draft, view.Cursor, out var cursor);
        var prompt = columns >= 4 ? "› " : "";
        var draftWidth = Math.Max(1, columns - prompt.Length);
        var draftLines = new List<TerminalText.Slice>();
        TerminalText.Wrap(draft, draftWidth, 0, draftLines);
        var caretLine = draftLines.FindLastIndex(line => line.Start <= cursor);
        var caretStart = draftLines[caretLine].Start;
        var caretCell = Math.Min(draftWidth, TerminalText.Width(draft.AsSpan(caretStart, cursor - caretStart)));

        var overlay = view.Approval is null ? view.Overlay : null;
        var headerRows = height >= (overlay is null ? 4 : 6) ? 1 : 0;
        var statusRows = height >= (overlay is null ? 5 : 7) ? 1 : 0;
        var keepTranscript = height >= 3 ? 1 : 0;
        var approval = new List<TerminalText.Slice>();
        var request = view.Approval is null ? "" : TerminalText.Clean(view.Approval).Trim();
        if (view.Approval is not null) TerminalText.Wrap(request, Math.Max(1, columns - 2), 0, approval);
        var approvalRoom = Math.Max(1, height - headerRows - statusRows - keepTranscript - 1);
        var approvalRows = view.Approval is null ? 0 : Math.Min(1 + approval.Count, Math.Min(Math.Max(3, height / 3), approvalRoom));
        const string allowKeys = "! F2 允许 · F3 拒绝";
        // Native confirms put the tool name on the first line and its arguments on the following lines.
        var detailsStart = request.IndexOf('\n') + 1;
        var canApprove = approvalRows >= 2 && columns >= TerminalText.Width(allowKeys) && approval.Take(approvalRows - 1)
            .Any(line => line.Start >= detailsStart && !string.IsNullOrWhiteSpace(line.Text));
        var remaining = height - headerRows - statusRows - approvalRows;
        var draftRows = Math.Min(draftLines.Count, Math.Min(Math.Clamp(height / 3, 1, 6), Math.Max(0, remaining - keepTranscript)));
        var panelRows = overlay is null ? 0 : Math.Min(PickerPanel.DesiredRows(overlay), Math.Min(Math.Max(1, height / 2),
            Math.Max(0, remaining - draftRows - (height >= 4 ? 1 : 0))));
        var bodyRows = remaining - draftRows - panelRows;

        var total = UpdateTranscript(view.Lines, columns, view.ExpandTools, view.ShowThinking, cell);
        (_total, _bodyRows) = (total, bodyRows);
        var maxScroll = Math.Max(0, total - bodyRows);
        var scroll = Math.Clamp(view.Scroll, 0, maxScroll);
        var end = Math.Max(0, total - scroll);
        var first = Math.Max(0, end - bodyRows);
        if (headerRows > 0) rows[0] = new(Header(view, columns), Ink.Header);
        var row = headerRows;
        // Only materialize visible rows. Finished messages retain their wrapped content across frames.
        foreach (var block in _blocks)
        {
            if (block.First >= end) break;
            var from = Math.Max(0, first - block.First);
            var to = Math.Min(block.RowCount, end - block.First);
            for (var i = from; i < to; i++) rows[row++] = block.Visible(i, columns);
        }

        row = headerRows + bodyRows;
        if (statusRows > 0)
        {
            var label = TerminalText.SingleLine(view.Status);
            if (scroll > 0) label = $"↑ {scroll}  {label}";
            label = TerminalText.Clip("─ " + label + " ", columns);
            var room = columns - TerminalText.Width(label);
            var shortcuts = view.Approval is not null || overlay is not null ? "" : view.Busy ? " Ctrl+P 命令 · F1 帮助 · Esc 停止" : " Ctrl+P 命令 · F1 帮助 · Enter 发送";
            if (room < TerminalText.Width(shortcuts) + 3) shortcuts = "";
            rows[row++] = new(label + new string('─', Math.Max(0, room - TerminalText.Width(shortcuts))) + shortcuts, Ink.Status);
        }
        if (approvalRows > 0)
            rows[row++] = new(TerminalText.Clip(canApprove ? allowKeys : "! F3 拒绝 · 放大窗口查看请求", columns), Ink.Approval);
        for (var i = 0; i < approvalRows - 1; i++)
        {
            var text = "  " + approval[i].Text;
            if (i + 2 == approvalRows && approval.Count > i + 1) text = TerminalText.Clip(text, columns - 1) + "…";
            rows[row++] = new(TerminalText.Clip(text, columns), Ink.Approval);
        }

        var pickerRows = 0;
        if (overlay is not null)
        {
            pickerRows = PickerPanel.Paint(overlay, rows.AsSpan(row, panelRows), columns);
            row += panelRows;
        }

        var start = Math.Clamp(caretLine - draftRows + 1, 0, Math.Max(0, draftLines.Count - draftRows));
        var cursorRow = draftRows == 0 ? -1 : row + caretLine - start;
        for (var i = 0; i < draftRows; i++)
        {
            var prefix = start + i == 0 ? prompt : new string(' ', prompt.Length);
            rows[row++] = new(columns == 0 ? "" : prefix + draftLines[start + i].Text, Ink.Composer);
        }
        return new(width, rows, cursorRow, Math.Min(width - 1, prompt.Length + caretCell), bodyRows, maxScroll, pickerRows, canApprove);
    }

    private static string Header(TuiView view, int columns)
    {
        var project = TerminalText.SingleLine(view.Project);
        var model = TerminalText.SingleLine(view.Model);
        var session = TerminalText.SingleLine(view.Session);
        var brand = columns >= 18 ? "FUI  " : "";
        var available = Math.Max(0, columns - brand.Length - 3);
        var sessionLimit = columns >= 60 && session.Length > 0 ? Math.Min(14, available / 5) : 0;
        if (sessionLimit > 0) available -= sessionLimit + 3;
        var projectLimit = Math.Min(TerminalText.Width(project), Math.Max(available / 2, available - TerminalText.Width(model)));
        var path = TerminalText.Clip(project, projectLimit, tail: true);
        var right = TerminalText.Clip(model, Math.Max(0, available - TerminalText.Width(path)));
        var title = brand + path + " · " + right;
        if (sessionLimit > 0) title += " · " + TerminalText.Clip(session, sessionLimit);
        return TerminalText.Clip(title, columns);
    }

    private int UpdateTranscript(IReadOnlyList<TuiLine> lines, int columns, bool expandTools, bool showThinking, ImageCells? cell)
    {
        if (columns != _columns || cell != _cell) { _blocks.Clear(); _columns = columns; _cell = cell; }
        if (_blocks.Count > lines.Count) _blocks.RemoveRange(lines.Count, _blocks.Count - lines.Count);
        var row = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (_blocks.Count == i) _blocks.Add(new());
            var block = _blocks[i];
            block.Update(lines[i], columns, expandTools, showThinking, cell);
            block.First = row;
            row += block.RowCount;
        }
        return row;
    }

    private sealed class MessageBlock
    {
        private readonly List<TerminalText.Slice> Pieces = [];
        private FormulaRows? _formulas;
        internal int RowCount => _hidden ? 0 : _formulas?.Rows.Count ?? (_markdown?.Count ?? Pieces.Count);
        internal int First;
        private TuiLine? _line;
        private bool _plain;
        private bool _hidden, _collapsed;
        private MarkdownLines? _markdown;
        private string _label = "", _indent = "";
        private Ink _ink;

        internal ScreenRow Visible(int index, int columns)
        {
            if (columns == 0) return new("");
            var prefix = index == 0 ? _label : _indent;
            if (_formulas is null && _markdown is { } markdown)
            {
                var markdownRow = markdown[index];
                return new(prefix + markdownRow.Text, markdownRow.Ink);
            }
            if (_formulas is null) return new(prefix + Pieces[index].Text, _ink);
            var row = _formulas.Rows[index];
            return row with { Text = prefix + row.Text, ImageIndent = _indent.Length };
        }

        internal void Update(TuiLine line, int columns, bool expandTools, bool showThinking, ImageCells? cell)
        {
            var hidden = line.Kind == "thinking" && !showThinking;
            var collapsed = line.Kind == "tool" && !expandTools;
            if (hidden == _hidden && collapsed == _collapsed && (ReferenceEquals(line, _line) ||
                (_line is not null && _line.Kind == line.Kind && ReferenceEquals(_line.Text, line.Text)))) return;
            var old = _line;
            _line = line;
            if (_hidden != hidden || _collapsed != collapsed) _plain = false;
            (_hidden, _collapsed) = (hidden, collapsed);
            if (hidden) { Pieces.Clear(); return; }
            var (label, ink) = line.Kind switch
            {
                "user" => ("你 › ", Ink.User), "assistant" => ("FUMO › ", Ink.Plain),
                "tool" => (collapsed ? "工具 ▸ " : "工具 ▾ ", Ink.Tool), "thinking" => ("思考 › ", Ink.Quiet),
                "error" => ("错误 › ", Ink.Error), _ => ("· ", Ink.Quiet)
            };
            _ink = ink;
            _label = TerminalText.Width(label) + 2 <= columns ? label : "";
            _indent = new(' ', TerminalText.Width(_label));
            var contentWidth = Math.Max(1, columns - _indent.Length);
            if (cell is { } pixels && line.Kind == "assistant" && FormulaRows.MayContainMath(line.Text))
            {
                _formulas ??= new();
                _formulas.Project(TerminalText.Clean(line.Text), contentWidth, pixels);
                _plain = false;
                return;
            }
            _formulas = null;
            if (line.Kind == "assistant")
            {
                _markdown ??= new();
                _markdown.Project(line.Text, contentWidth);
                _plain = false;
                return;
            }
            _markdown = null;
            if (collapsed)
            {
                var preview = TerminalText.Clean(line.Text);
                var newline = preview.IndexOf('\n');
                if (newline >= 0)
                {
                    var visible = TerminalText.IndexAtColumn(preview.AsSpan(0, newline), Math.Max(0, contentWidth - 2));
                    preview = preview[..visible] + " …";
                }
                Pieces.Clear();
                Pieces.Add(new(0, TerminalText.Clip(preview, contentWidth)));
                _plain = false;
                return;
            }
            // A token may complete the previous grapheme. Reflow the final two rows, not the whole answer.
            // A control sequence split across chunks instead needs the full sanitizer state; never emit its suffix raw.
            if (_plain && old is not null && old.Kind == line.Kind && line.Text.StartsWith(old.Text, StringComparison.Ordinal))
            {
                var suffix = line.Text[old.Text.Length..];
                if (ReferenceEquals(TerminalText.Clean(suffix), suffix))
                {
                    var keep = Math.Max(0, Pieces.Count - 2);
                    var start = Pieces[keep].Start;
                    Pieces.RemoveRange(keep, Pieces.Count - keep);
                    TerminalText.Wrap(line.Text, contentWidth, start, Pieces);
                    return;
                }
            }
            var clean = TerminalText.Clean(line.Text);
            _plain = ReferenceEquals(clean, line.Text);
            Pieces.Clear();
            TerminalText.Wrap(clean, contentWidth, 0, Pieces);
        }
    }
}
