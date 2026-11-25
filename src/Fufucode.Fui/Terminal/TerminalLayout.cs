namespace Fufucode.Fui;

internal enum Ink
{
    Plain, Header, User, Assistant, Tool, Quiet, Error, Status, Approval, Composer, PanelTitle, Selection,
    Heading, Code, CodeFence, Thinking, Info
}

internal readonly record struct ImageStrip(int Column, string Sixel);
internal readonly record struct ScreenRow(string Text, Ink Ink = Ink.Plain, IReadOnlyList<ImageStrip>? Images = null, int ImageIndent = 0);
internal sealed record TerminalFrame(int Width, ScreenRow[] Rows, int CursorRow, int CursorColumn, int ViewportRows, int MaxScroll,
    int PickerRows = 0, bool CanApprove = false);

internal sealed class TerminalLayout
{
    private readonly List<MessageBlock> _blocks = [];
    private int _columns;
    private ImageCells? _cell;
    private bool _cards;
    private string? _project;
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

        var prompt = ComposerPrompt(columns);
        var draft = TerminalText.Clean(view.Draft, view.Cursor, out var cursor);
        var promptWidth = TerminalText.Width(prompt);
        var draftWidth = Math.Max(1, columns - promptWidth);
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
        var approvalWidth = Math.Max(1, columns - (columns >= 24 ? 4 : 2));
        if (view.Approval is not null) TerminalText.Wrap(request, approvalWidth, 0, approval);
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

        // Cards need enough room for a header, content, and a closing rail. Narrow or tiny terminals
        // keep the same wrapped text with a compact role rail so a resize never makes rows disappear.
        var cardLayout = columns >= 24 && bodyRows >= 4;
        var total = UpdateTranscript(view.Lines, columns, view.ExpandTools, view.ShowThinking, cell, cardLayout,
            view.Project, view.ResolveImage);
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
            var shortcuts = view.Approval is not null || overlay is not null ? "" : view.Busy
                ? "Ctrl+P 命令 · F1 帮助 · Esc 停止"
                : "Ctrl+P 命令 · F1 帮助 · Enter 发送";
            rows[row++] = new(StatusLine(view.Status, scroll, columns, shortcuts), Ink.Status);
        }
        if (approvalRows > 0)
            rows[row++] = new(ApprovalAction(canApprove, columns), Ink.Approval);
        for (var i = 0; i < approvalRows - 1; i++)
            rows[row++] = new(ApprovalContent(approval[i].Text, columns), Ink.Approval);

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
            var prefix = start + i == 0 ? prompt : new string(' ', promptWidth);
            rows[row++] = new(columns == 0 ? "" : prefix + draftLines[start + i].Text, Ink.Composer);
        }
        return new(width, rows, cursorRow, Math.Min(width - 1, promptWidth + caretCell), bodyRows, maxScroll, pickerRows, canApprove);
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

    private static string StatusLine(string status, int scroll, int columns, string shortcuts)
    {
        if (columns <= 0) return "";
        var label = TerminalText.SingleLine(status);
        if (scroll > 0) label = $"↑ {scroll}  {label}";
        if (columns < 8) return TerminalText.Clip(label, columns);

        const string left = "├─ ";
        const string right = " ┤";
        var available = Math.Max(0, columns - TerminalText.Width(left) - TerminalText.Width(right));
        label = TerminalText.Clip(label, available);
        if (shortcuts.Length > 0 && TerminalText.Width(shortcuts) + 3 > available - TerminalText.Width(label))
            shortcuts = "";
        var used = TerminalText.Width(label) + (shortcuts.Length == 0 ? 0 : TerminalText.Width(shortcuts) + 1);
        var fill = Math.Max(0, available - used);
        var line = left + label + new string('─', fill);
        if (shortcuts.Length > 0) line += " " + shortcuts;
        line += right;
        return TerminalText.Clip(line, columns);
    }

    private static string ApprovalAction(bool canApprove, int columns)
    {
        var text = canApprove ? "! F2 允许 · F3 拒绝" : "! F3 拒绝 · 放大窗口查看请求";
        if (columns <= 0) return "";
        if (columns < 8) return TerminalText.Clip(text, columns);
        const string right = " ┤";
        var available = Math.Max(0, columns - TerminalText.Width(right));
        var shown = TerminalText.Clip(text, available);
        var fill = Math.Max(0, available - TerminalText.Width(shown));
        return TerminalText.Clip(shown + new string('─', fill) + right, columns);
    }

    private static string ApprovalContent(string text, int columns)
    {
        if (columns <= 0) return "";
        var rail = columns >= 8 ? "│ " : "  ";
        var right = columns >= 8 ? " │" : "";
        var inner = Math.Max(1, columns - TerminalText.Width(rail) - TerminalText.Width(right));
        var shown = TerminalText.Clip(TerminalText.Clean(text), inner);
        shown = Pad(shown, inner);
        return TerminalText.Clip(rail + shown + right, columns);
    }

    private static string ComposerPrompt(int columns)
    {
        if (columns >= 8) return "│ › ";
        return columns >= 4 ? "› " : "";
    }

    private int UpdateTranscript(IReadOnlyList<TuiLine> lines, int columns, bool expandTools, bool showThinking,
        ImageCells? cell, bool cards, string project, Func<string, ResolvedImage?>? resolveImage)
    {
        if (columns != _columns || cell != _cell || cards != _cards || !string.Equals(project, _project, StringComparison.Ordinal))
        {
            _blocks.Clear();
            _columns = columns;
            _cell = cell;
            _cards = cards;
            _project = project;
        }
        if (_blocks.Count > lines.Count) _blocks.RemoveRange(lines.Count, _blocks.Count - lines.Count);
        var row = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (_blocks.Count == i) _blocks.Add(new());
            var block = _blocks[i];
            block.Update(lines[i], columns, expandTools, showThinking, cards, cell, resolveImage);
            block.First = row;
            row += block.RowCount;
        }
        return row;
    }

    private static string Pad(string text, int width)
    {
        var clipped = TerminalText.Clip(text, width);
        return clipped + new string(' ', Math.Max(0, width - TerminalText.Width(clipped)));
    }

    private static string CardTop(string title, int columns)
    {
        if (columns <= 0) return "";
        if (columns == 1) return "╭";
        var shown = TerminalText.Clip(title, Math.Max(0, columns - 4));
        var line = "╭─ " + shown + " ";
        var fill = Math.Max(0, columns - TerminalText.Width(line) - 1);
        return TerminalText.Clip(line + new string('─', fill) + "╮", columns);
    }

    private static string CardBottom(int columns)
    {
        if (columns <= 0) return "";
        if (columns == 1) return "╰";
        return TerminalText.Clip("╰" + new string('─', Math.Max(0, columns - 2)) + "╯", columns);
    }

    private sealed class MessageBlock
    {
        private readonly List<TerminalText.Slice> Pieces = [];
        private readonly List<ScreenRow> _rows = [];
        private RichRows? _rich;
        private MarkdownLines? _markdown;
        private TuiLine? _line;
        private bool _plain, _hidden, _collapsed, _cards;
        private string _kind = "";
        private Ink _roleInk;
        private int _columns;

        internal int RowCount => _hidden ? 0 : _rows.Count;
        internal int First;

        internal ScreenRow Visible(int index, int columns) => columns == 0 ? new("") : _rows[index];

        internal void Update(TuiLine line, int columns, bool expandTools, bool showThinking, bool cards, ImageCells? cell,
            Func<string, ResolvedImage?>? resolveImage)
        {
            var hidden = line.Kind == "thinking" && !showThinking;
            var collapsed = line.Kind == "tool" && !expandTools;
            if (_line is not null && _cards == cards && _hidden == hidden && _collapsed == collapsed &&
                (ReferenceEquals(line, _line) || (_line.Kind == line.Kind && ReferenceEquals(_line.Text, line.Text) &&
                    ReferenceEquals(_line.Images, line.Images))))
                return;

            var old = _line;
            _line = line;
            _cards = cards;
            _columns = columns;
            if (_hidden != hidden || _collapsed != collapsed) _plain = false;
            (_hidden, _collapsed) = (hidden, collapsed);
            _rows.Clear();
            _kind = line.Kind;
            _roleInk = RoleInk(_kind);
            if (hidden)
            {
                Pieces.Clear();
                _markdown = null;
                _rich = null;
                _plain = false;
                return;
            }
            if (_kind == "tool")
            {
                Pieces.Clear();
                BuildTool(line.Text, line.Images, columns, collapsed, cell, resolveImage);
                return;
            }

            var card = cards && _kind is "user" or "assistant" or "error";
            var rail = cards && _kind == "thinking";
            var compactLabel = CompactLabel(_kind);
            var showCompactLabel = !card && !rail && TerminalText.Width(compactLabel) + 2 <= columns;
            var contentWidth = card ? Math.Max(1, columns - 4) :
                rail ? Math.Max(1, columns - 2) : Math.Max(1, columns - (showCompactLabel ? TerminalText.Width(compactLabel) : 0));

            if (_kind == "assistant" &&
                ((cell is not null && (RichRows.MayContainMath(line.Text) || MarkdownImages.MayContainImage(line.Text) || line.Images is { Count: > 0 })) ||
                 MarkdownImages.MayContainDataUri(line.Text)))
            {
                Pieces.Clear();
                _markdown = null;
                _rich ??= new();
                _rich.Project(line.Text, contentWidth, cell, allowMarkdown: true, allowMath: true, resolveImage, line.Images, Ink.Assistant);
                _plain = false;
                AddRows(_rich.Rows, card, rail, showCompactLabel, compactLabel);
                return;
            }

            if (_kind == "assistant")
            {
                Pieces.Clear();
                _rich = null;
                _markdown ??= new();
                _markdown.Project(line.Text, contentWidth);
                _plain = false;
                var markdownRows = new List<ScreenRow>(_markdown.Count);
                for (var i = 0; i < _markdown.Count; i++)
                {
                    var item = _markdown[i];
                    markdownRows.Add(new(item.Text, item.Ink == Ink.Plain ? Ink.Assistant : item.Ink));
                }
                AddRows(markdownRows, card, rail, showCompactLabel, compactLabel);
                return;
            }

            if ((cell is not null && (MarkdownImages.MayContainImage(line.Text) || line.Images is { Count: > 0 })) ||
                MarkdownImages.MayContainDataUri(line.Text))
            {
                Pieces.Clear();
                _markdown = null;
                _rich ??= new();
                _rich.Project(line.Text, contentWidth, cell, allowMarkdown: false, allowMath: false, resolveImage, line.Images, _roleInk);
                _plain = false;
                AddRows(_rich.Rows, card, rail, showCompactLabel, compactLabel);
                return;
            }

            _rich = null;
            _markdown = null;
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
                    AddPieces(card, rail, showCompactLabel, compactLabel);
                    return;
                }
            }
            var clean = TerminalText.Clean(line.Text);
            _plain = ReferenceEquals(clean, line.Text);
            Pieces.Clear();
            TerminalText.Wrap(clean, contentWidth, 0, Pieces);
            AddPieces(card, rail, showCompactLabel, compactLabel);
        }

        private void AddPieces(bool card, bool rail, bool compactLabel, string label)
        {
            var rows = new List<ScreenRow>(Pieces.Count);
            foreach (var piece in Pieces) rows.Add(new(piece.Text, _roleInk));
            AddRows(rows, card, rail, compactLabel, label);
        }

        private void AddRows(IReadOnlyList<ScreenRow> rows, bool card, bool rail, bool compactLabel, string label)
        {
            if (card)
            {
                _rows.Add(new(CardTop(RoleTitle(_kind), _columns), _roleInk));
                foreach (var row in rows)
                {
                    var inner = Math.Max(1, _columns - 4);
                    var ink = row.Ink == Ink.Plain ? _roleInk : row.Ink;
                    _rows.Add(row with
                    {
                        Text = "│ " + Pad(row.Text, inner) + " │",
                        Ink = ink,
                        ImageIndent = row.ImageIndent + 2
                    });
                }
                _rows.Add(new(CardBottom(_columns), _roleInk));
                return;
            }
            if (rail)
            {
                foreach (var row in rows)
                    _rows.Add(row with { Text = "┆ " + row.Text, Ink = Ink.Thinking, ImageIndent = row.ImageIndent + 2 });
                return;
            }
            var prefix = compactLabel ? label : "";
            var indent = new string(' ', TerminalText.Width(prefix));
            foreach (var row in rows)
            {
                _rows.Add(row with
                {
                    Text = prefix + row.Text,
                    Ink = row.Ink == Ink.Plain ? _roleInk : row.Ink,
                    ImageIndent = row.ImageIndent + TerminalText.Width(prefix)
                });
                prefix = indent;
            }
        }

        private void BuildTool(string text, IReadOnlyList<TuiImage>? images, int columns, bool collapsed,
            ImageCells? cell, Func<string, ResolvedImage?>? resolveImage)
        {
            var tool = ToolPresentation.Parse(text);
            var title = tool.Title(collapsed);
            if (!_cards)
            {
                _rows.Add(new(TerminalText.Clip(title, columns), Ink.Tool));
                if (!collapsed && (tool.Body.Length > 0 || images is { Count: > 0 }))
                {
                    var inner = Math.Max(1, columns - 2);
                    if ((cell is not null && (MarkdownImages.MayContainImage(tool.Body) || images is { Count: > 0 })) ||
                        MarkdownImages.MayContainDataUri(tool.Body))
                    {
                        _rich ??= new();
                        _rich.Project(tool.Body, inner, cell, allowMarkdown: false, allowMath: false, resolveImage, images, Ink.Tool);
                        foreach (var row in _rich.Rows)
                            _rows.Add(row with { Text = "  " + row.Text, Ink = row.Ink == Ink.Plain ? Ink.Tool : row.Ink, ImageIndent = row.ImageIndent + 2 });
                    }
                    else
                    {
                        var body = new List<TerminalText.Slice>();
                        TerminalText.Wrap(tool.Body, inner, 0, body);
                        foreach (var piece in body) _rows.Add(new("  " + piece.Text, Ink.Tool));
                    }
                }
                return;
            }

            if (collapsed)
            {
                _rows.Add(new(CardTop(title, columns), Ink.Tool));
                return;
            }

            _rows.Add(new(CardTop(title, columns), Ink.Tool));
            if (tool.Body.Length > 0 || images is { Count: > 0 })
            {
                var inner = Math.Max(1, columns - 4);
                if ((cell is not null && (MarkdownImages.MayContainImage(tool.Body) || images is { Count: > 0 })) ||
                    MarkdownImages.MayContainDataUri(tool.Body))
                {
                    _rich ??= new();
                    _rich.Project(tool.Body, inner, cell, allowMarkdown: false, allowMath: false, resolveImage, images, Ink.Tool);
                    foreach (var row in _rich.Rows)
                        _rows.Add(row with
                        {
                            Text = "│ " + Pad(row.Text, inner) + " │",
                            Ink = row.Ink == Ink.Plain ? Ink.Tool : row.Ink,
                            ImageIndent = row.ImageIndent + 2
                        });
                }
                else
                {
                    var body = new List<TerminalText.Slice>();
                    TerminalText.Wrap(tool.Body, inner, 0, body);
                    foreach (var piece in body) _rows.Add(new("│ " + Pad(piece.Text, inner) + " │", Ink.Tool));
                }
            }
            _rows.Add(new(CardBottom(columns), Ink.Tool));
        }

        private static Ink RoleInk(string kind) => kind switch
        {
            "user" => Ink.User,
            "assistant" => Ink.Assistant,
            "thinking" => Ink.Thinking,
            "tool" => Ink.Tool,
            "error" => Ink.Error,
            _ => Ink.Info
        };

        private static string RoleTitle(string kind) => kind switch
        {
            "user" => "你",
            "assistant" => "FUMO",
            "error" => "错误",
            _ => "信息"
        };

        private static string CompactLabel(string kind) => kind switch
        {
            "user" => "你 › ",
            "assistant" => "FUMO › ",
            "thinking" => "思考 › ",
            "error" => "错误 › ",
            _ => "· "
        };
    }

    private readonly record struct ToolPresentation(string Name, string Status, string Args, string Body)
    {
        internal string Title(bool collapsed)
        {
            var marker = collapsed ? "▸" : "▾";
            var title = $"工具 {marker} {(Name.Length == 0 ? "工具" : Name)}";
            if (Status.Length > 0) title += " · " + Status;
            if (Args.Length > 0) title += " · " + Args;
            return title;
        }

        internal static ToolPresentation Parse(string text)
        {
            var clean = TerminalText.Clean(text);
            var newline = clean.IndexOf('\n');
            var first = newline < 0 ? clean : clean[..newline];
            var body = newline < 0 ? "" : clean[(newline + 1)..];
            first = first.Trim();
            if (first.Length == 0) return new("", "", "", body);

            var split = first.IndexOf(' ');
            var name = split < 0 ? first : first[..split];
            var rest = split < 0 ? "" : first[(split + 1)..].Trim();
            var status = "";
            var args = rest;
            if (rest.StartsWith('['))
            {
                var close = rest.IndexOf(']');
                if (close > 1)
                {
                    status = rest[1..close].Trim();
                    args = rest[(close + 1)..].Trim();
                }
            }
            return new(name, status, args, body);
        }
    }
}
