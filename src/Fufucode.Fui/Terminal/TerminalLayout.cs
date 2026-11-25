using System.Text;
using SkiaSharp;

namespace Fufucode.Fui;

internal enum Ink
{
    Plain, Header, User, Assistant, Tool, Quiet, Error, Status, Approval, Composer, PanelTitle, Selection,
    Heading, Code, CodeFence, Thinking, Info
}

internal readonly record struct ImageStrip(int Column, string Sixel);
internal readonly record struct ScreenRow(string Text, Ink Ink = Ink.Plain, IReadOnlyList<ImageStrip>? Images = null, int ImageIndent = 0);
internal sealed record TerminalFrame(int Width, ScreenRow[] Rows, int CursorRow, int CursorColumn, int ViewportRows, int MaxScroll,
    int PickerRows = 0, bool CanApprove = false, MouseRegions? Mouse = null);

internal sealed class TerminalLayout
{
    private readonly List<MessageBlock> _blocks = [];
    private int _columns;
    private ImageCells? _cell;
    private bool _cards;
    private int _total, _bodyRows;
    private AttachmentPreviewCache? _attachmentPreview;
    private readonly record struct AttachmentPreviewPlan(ProjectImagePreview Preview, int Column);
    private sealed record AttachmentPreviewCache(string Key, ScreenRow[] Rows);

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

        var attachments = view.Attachments ?? [];
        var attachmentHeaderRows = attachments.Count != 0 && columns >= 8 && height >= 4 ? 1 : 0;
        var previewPlans = new List<AttachmentPreviewPlan>();
        var attachmentPreviewRows = 0;
        string? previewReason = null;
        var imageCount = attachments.Count(file => file.MimeType is not null);
        if (imageCount != 0)
        {
            if (cell is null) previewReason = "无 Sixel 预览";
            else if (overlay is not null || view.Approval is not null) previewReason = "预览暂不可用";
            else if (height < 12) previewReason = "预览：窗口过矮";
            else
            {
                PlanAttachmentPreviews(attachments, columns - promptWidth, cell.Value, previewPlans, out attachmentPreviewRows);
                if (previewPlans.Count == 0)
                {
                    attachmentPreviewRows = 0;
                    previewReason = "预览：窗口过窄";
                }
                else if (attachmentPreviewRows > height - headerRows - statusRows - approvalRows -
                    attachmentHeaderRows - keepTranscript - 1)
                {
                    previewPlans.Clear();
                    attachmentPreviewRows = 0;
                    previewReason = "预览：窗口过矮";
                }
            }
        }

        var remaining = height - headerRows - statusRows - approvalRows - attachmentHeaderRows - attachmentPreviewRows;
        var draftRows = Math.Min(draftLines.Count, Math.Min(Math.Clamp(height / 3, 1, 6), Math.Max(0, remaining - keepTranscript)));
        var panelRows = overlay is null ? 0 : Math.Min(PickerPanel.DesiredRows(overlay), Math.Min(Math.Max(1, height / 2),
            Math.Max(0, remaining - draftRows - (height >= 4 ? 1 : 0))));
        var bodyRows = remaining - draftRows - panelRows;

        // Cards need enough room for a header, content, and a closing rail. Narrow or tiny terminals
        // keep the same wrapped text with a compact role rail so a resize never makes rows disappear.
        var cardLayout = columns >= 24 && bodyRows >= 4;
        var total = UpdateTranscript(view.Lines, columns, view.ExpandTools, view.ShowThinking, cell, cardLayout);
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
        var approvalRow = -1;
        var approvalAction = "";
        if (approvalRows > 0)
        {
            approvalRow = row;
            approvalAction = ApprovalAction(canApprove, columns);
            rows[row++] = new(approvalAction, Ink.Approval);
        }
        for (var i = 0; i < approvalRows - 1; i++)
            rows[row++] = new(ApprovalContent(approval[i].Text, columns), Ink.Approval);

        var pickerRows = 0;
        var pickerTop = -1;
        var pickerStart = 0;
        var pickerVisible = 0;
        var pickerColumns = 0;
        if (overlay is not null)
        {
            pickerTop = row;
            pickerRows = PickerPanel.Paint(overlay, rows.AsSpan(row, panelRows), columns);
            (pickerStart, pickerVisible) = PickerWindow(overlay, panelRows, columns);
            pickerColumns = PickerHitColumns(overlay, panelRows, columns);
            row += panelRows;
        }

        if (attachmentHeaderRows != 0)
            rows[row++] = new(AttachmentHeader(attachments, columns, previewReason), Ink.Composer);
        if (attachmentPreviewRows != 0)
        {
            var previews = AttachmentPreviewRows(attachments, previewPlans, promptWidth, columns, cell!.Value, attachmentPreviewRows);
            foreach (var preview in previews) rows[row++] = preview;
        }

        var start = Math.Clamp(caretLine - draftRows + 1, 0, Math.Max(0, draftLines.Count - draftRows));
        var cursorRow = draftRows == 0 ? -1 : row + caretLine - start;
        for (var i = 0; i < draftRows; i++)
        {
            var prefix = start + i == 0 ? prompt : new string(' ', promptWidth);
            rows[row++] = new(columns == 0 ? "" : prefix + draftLines[start + i].Text, Ink.Composer);
        }
        var (allowStart, allowEnd) = canApprove ? TextRange(approvalAction, "F2 允许") : (-1, -1);
        var (rejectStart, rejectEnd) = TextRange(approvalAction, "F3 拒绝");
        return new(width, rows, cursorRow, Math.Min(width - 1, promptWidth + caretCell), bodyRows, maxScroll, pickerRows, canApprove,
            new MouseRegions(pickerTop, pickerStart, pickerVisible, pickerColumns, approvalRow,
                allowStart, allowEnd, rejectStart, rejectEnd));
    }

    private static (int Start, int Visible) PickerWindow(FuiOverlay overlay, int panelRows, int columns)
    {
        var count = overlay.Items.Count;
        if (count == 0 || panelRows <= 1) return (0, 0);
        var selected = Math.Clamp(overlay.Selected, 0, count - 1);
        var split = columns >= 80 && panelRows >= 6;
        var visible = split ? Math.Max(1, Math.Min(count, panelRows - 1)) : VisibleItems(overlay, panelRows, selected);
        return (Math.Clamp(selected - visible / 2, 0, Math.Max(0, count - visible)), visible);
    }

    private static int VisibleItems(FuiOverlay overlay, int panelRows, int selected)
    {
        var detail = FuiAgentActivityRoster.IsHubTitle(overlay.Title)
            ? TerminalText.SingleLine(overlay.Items[selected].Detail ?? "")
            : TerminalText.SingleLine(overlay.Message ?? overlay.Items[selected].Detail ?? "");
        var hasDetail = detail.Length > 0 && panelRows >= 3;
        return Math.Max(1, Math.Min(overlay.Items.Count, panelRows - 1 - (hasDetail ? 1 : 0)));
    }

    private static int PickerHitColumns(FuiOverlay overlay, int panelRows, int columns)
    {
        if (columns >= 80 && panelRows >= 6 && overlay.Items.Count > 0)
        {
            var left = Math.Clamp(columns / 2, 30, 56);
            if (columns - left - 1 >= 18) return left;
        }
        return columns;
    }

    private static (int Start, int End) TextRange(string text, string value)
    {
        var index = text.IndexOf(value, StringComparison.Ordinal);
        if (index < 0) return (-1, -1);
        var start = TerminalText.Width(text.AsSpan(0, index));
        return (start, start + TerminalText.Width(value));
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

    private static string AttachmentHeader(IReadOnlyList<FileAttachment> attachments, int columns, string? reason)
    {
        if (columns <= 0) return "";
        var images = attachments.Count(file => file.MimeType is not null);
        var text = attachments.Count - images;
        var builder = new StringBuilder("│ 附件 ").Append(attachments.Count);
        if (images != 0) builder.Append(" · 图片 ").Append(images);
        if (text != 0) builder.Append(" · 文本 ").Append(text);
        if (reason is not null) builder.Append(" · ").Append(reason);
        var available = Math.Max(0, columns - TerminalText.Width(builder.ToString()));
        var nameLimit = Math.Max(1, (available - attachments.Count * 5) / Math.Max(1, attachments.Count));
        foreach (var file in attachments)
        {
            var icon = file.MimeType is null ? "▤" : "▣";
            var name = TerminalText.SingleLine(Path.GetFileName(file.Path));
            if (name.Length == 0) name = TerminalText.SingleLine(file.Path);
            builder.Append(" · ").Append(icon).Append(' ').Append(TerminalText.Clip(name, Math.Min(24, nameLimit), tail: true));
        }
        return TerminalText.Clip(builder.ToString(), columns);
    }

    private static void PlanAttachmentPreviews(IReadOnlyList<FileAttachment> attachments, int available,
        ImageCells cell, List<AttachmentPreviewPlan> plans, out int rows)
    {
        rows = 0;
        var x = 0;
        foreach (var file in attachments)
        {
            if (file.Preview is not { } preview || preview.Width <= 0 || preview.Height <= 0 ||
                preview.Pixels.Length != preview.Width * preview.Height) continue;
            var width = Math.Max(1, (preview.Width + cell.Width - 1) / cell.Width);
            rows = Math.Max(rows, Math.Max(1, (preview.Height + cell.Height - 1) / cell.Height));
            if (x != 0) x++;
            if (x + width > available) continue;
            plans.Add(new(preview, x));
            x += width;
        }
    }

    private ScreenRow[] AttachmentPreviewRows(IReadOnlyList<FileAttachment> attachments,
        IReadOnlyList<AttachmentPreviewPlan> plans, int promptIndent, int columns, ImageCells cell, int rowCount)
    {
        var key = string.Join('|', attachments.Select(file => $"{file.Path}:{Convert.ToHexString(file.Digest)}")) +
            $"\n{plans.Count}:{promptIndent}:{columns}:{cell.Width}:{cell.Height}:{cell.Foreground}:{cell.Background}";
        if (_attachmentPreview is { } cached && cached.Key == key && cached.Rows.Length == rowCount) return cached.Rows;

        var rows = new ScreenRow[rowCount];
        Array.Fill(rows, new(""));
        var strips = new List<ImageStrip>[rowCount];
        for (var i = 0; i < strips.Length; i++) strips[i] = [];
        foreach (var plan in plans)
        {
            var preview = plan.Preview;
            for (var row = 0; row < rowCount; row++)
            {
                var top = row * cell.Height;
                if (top >= preview.Height) break;
                var height = Math.Min(cell.Height, preview.Height - top);
                strips[row].Add(new(plan.Column, EncodePreview(preview.Pixels, preview.Width, top, height, cell)));
            }
        }
        for (var row = 0; row < rowCount; row++)
            if (strips[row].Count != 0) rows[row] = new("", Ink.Composer, strips[row], promptIndent);
        _attachmentPreview = new(key, rows);
        return rows;
    }

    // Real-color previews use a compact 6x6x6 palette. Transparent pixels stay unset so the
    // terminal background remains visible; partially transparent pixels blend into that background.
    private static string EncodePreview(SKColor[] pixels, int width, int top, int height, ImageCells cell)
    {
        var palette = new bool[216];
        var indexes = new byte[width * height];
        indexes.AsSpan().Fill(255);
        var start = top * width;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var pixel = pixels[start + y * width + x];
                if (pixel.Alpha == 0) continue;
                var red = Blend(pixel.Red, cell.Background >> 16 & 255, pixel.Alpha);
                var green = Blend(pixel.Green, cell.Background >> 8 & 255, pixel.Alpha);
                var blue = Blend(pixel.Blue, cell.Background & 255, pixel.Alpha);
                var r = (red * 5 + 127) / 255;
                var g = (green * 5 + 127) / 255;
                var b = (blue * 5 + 127) / 255;
                var index = (byte)(r * 36 + g * 6 + b);
                indexes[y * width + x] = index;
                palette[index] = true;
            }

        var bytes = new StringBuilder();
        bytes.Append("\x1bP0;1;0q\"1;1;").Append(width).Append(';').Append(height);
        for (var i = 0; i < palette.Length; i++)
        {
            if (!palette[i]) continue;
            var r = i / 36;
            var g = i / 6 % 6;
            var b = i % 6;
            bytes.Append('#').Append(i).Append(";2;").Append((r * 100 + 2) / 5).Append(';')
                .Append((g * 100 + 2) / 5).Append(';').Append((b * 100 + 2) / 5);
        }

        for (var y = 0; y < height; y += 6)
        {
            if (y != 0) bytes.Append('-');
            var band = Math.Min(6, height - y);
            var used = new bool[216];
            for (var dy = 0; dy < band; dy++)
                for (var x = 0; x < width; x++)
                {
                    var index = indexes[(y + dy) * width + x];
                    if (index != 255) used[index] = true;
                }
            for (var index = 0; index < used.Length; index++)
            {
                if (!used[index]) continue;
                var last = -1;
                for (var x = 0; x < width; x++)
                {
                    var bits = 0;
                    for (var dy = 0; dy < band; dy++)
                        if (indexes[(y + dy) * width + x] == index) bits |= 1 << dy;
                    if (bits != 0) last = x;
                }
                if (last < 0) continue;
                bytes.Append('$').Append('#').Append(index);
                for (var x = 0; x <= last;)
                {
                    var bits = 0;
                    for (var dy = 0; dy < band; dy++)
                        if (indexes[(y + dy) * width + x] == index) bits |= 1 << dy;
                    var count = 1;
                    while (x + count <= last)
                    {
                        var nextBits = 0;
                        for (var dy = 0; dy < band; dy++)
                            if (indexes[(y + dy) * width + x + count] == index) nextBits |= 1 << dy;
                        if (nextBits != bits) break;
                        count++;
                    }
                    var ch = (char)(63 + bits);
                    if (count > 3) bytes.Append('!').Append(count).Append(ch);
                    else bytes.Append(ch, count);
                    x += count;
                }
            }
        }
        return bytes.Append("\x1b\\").ToString();
    }

    private static int Blend(int foreground, int background, int alpha) =>
        (foreground * alpha + background * (255 - alpha) + 127) / 255;

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
        ImageCells? cell, bool cards)
    {
        if (columns != _columns || cell != _cell || cards != _cards)
        {
            _blocks.Clear();
            _columns = columns;
            _cell = cell;
            _cards = cards;
        }
        if (_blocks.Count > lines.Count) _blocks.RemoveRange(lines.Count, _blocks.Count - lines.Count);
        var row = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (_blocks.Count == i) _blocks.Add(new());
            var block = _blocks[i];
            block.Update(lines[i], columns, expandTools, showThinking, cards, cell);
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
        private FormulaRows? _formulas;
        private MarkdownLines? _markdown;
        private TuiLine? _line;
        private bool _plain, _hidden, _collapsed, _cards;
        private string _kind = "";
        private Ink _roleInk;
        private int _columns;

        internal int RowCount => _hidden ? 0 : _rows.Count;
        internal int First;

        internal ScreenRow Visible(int index, int columns) => columns == 0 ? new("") : _rows[index];

        internal void Update(TuiLine line, int columns, bool expandTools, bool showThinking, bool cards, ImageCells? cell)
        {
            var hidden = line.Kind == "thinking" && !showThinking;
            var collapsed = line.Kind == "tool" && !expandTools;
            if (_line is not null && _cards == cards && _hidden == hidden && _collapsed == collapsed &&
                (ReferenceEquals(line, _line) || (_line.Kind == line.Kind && ReferenceEquals(_line.Text, line.Text))))
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
                _formulas = null;
                _plain = false;
                return;
            }
            if (_kind == "tool")
            {
                Pieces.Clear();
                BuildTool(line.Text, columns, collapsed);
                return;
            }

            var card = cards && _kind is "user" or "assistant" or "error";
            var rail = cards && _kind == "thinking";
            var compactLabel = CompactLabel(_kind);
            var showCompactLabel = !card && !rail && TerminalText.Width(compactLabel) + 2 <= columns;
            var contentWidth = card ? Math.Max(1, columns - 4) :
                rail ? Math.Max(1, columns - 2) : Math.Max(1, columns - (showCompactLabel ? TerminalText.Width(compactLabel) : 0));

            if (_kind == "assistant" && cell is { } pixels && FormulaRows.MayContainMath(line.Text))
            {
                Pieces.Clear();
                _formulas ??= new();
                _formulas.Project(TerminalText.Clean(line.Text), contentWidth, pixels);
                _plain = false;
                AddRows(_formulas.Rows, card, rail, showCompactLabel, compactLabel);
                return;
            }

            if (_kind == "assistant")
            {
                Pieces.Clear();
                _formulas = null;
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

            _formulas = null;
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

        private void BuildTool(string text, int columns, bool collapsed)
        {
            var tool = ToolPresentation.Parse(text);
            var title = tool.Title(collapsed);
            if (!_cards)
            {
                _rows.Add(new(TerminalText.Clip(title, columns), Ink.Tool));
                if (!collapsed && tool.Body.Length > 0)
                {
                    var body = new List<TerminalText.Slice>();
                    TerminalText.Wrap(tool.Body, Math.Max(1, columns - 2), 0, body);
                    foreach (var piece in body) _rows.Add(new("  " + piece.Text, Ink.Tool));
                }
                return;
            }

            if (collapsed)
            {
                _rows.Add(new(CardTop(title, columns), Ink.Tool));
                return;
            }

            _rows.Add(new(CardTop(title, columns), Ink.Tool));
            if (tool.Body.Length > 0)
            {
                var body = new List<TerminalText.Slice>();
                TerminalText.Wrap(tool.Body, Math.Max(1, columns - 4), 0, body);
                foreach (var piece in body) _rows.Add(new("│ " + Pad(piece.Text, Math.Max(1, columns - 4)) + " │", Ink.Tool));
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
