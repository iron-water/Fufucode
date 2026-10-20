using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Fufucode.App.Infrastructure;

/// <summary>导出一条会话消息：发言者显示名与正文（支持 Markdown 子集）。</summary>
public sealed record SessionPdfMessage(string Speaker, string Text, bool IsUser = false);

/// <summary>
/// 会话 PDF 导出器：使用 SkiaSharp 直接生成 A4 PDF，不引入额外依赖，也不依赖 WebView2。
/// 排版目标是“可读、可存档”：标题、元信息、逐条消息、代码块与分页；不是完整 Markdown 渲染器。
/// 中文字体优先使用系统雅黑，缺失时回退 Skia 默认字体。
/// </summary>
public static class SessionPdfExporter
{
    public const float PageWidth = 595f;   // A4 @ 72dpi
    public const float PageHeight = 842f;
    private const float Margin = 48f;
    private const float ContentWidth = PageWidth - Margin * 2;
    private const float FooterReserve = 24f;

    public static void Write(
        System.IO.Stream output,
        string title,
        IReadOnlyList<(string Label, string Value)> metadata,
        IReadOnlyList<SessionPdfMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("输出流不可写", nameof(output));

        using var document = SKDocument.CreatePdf(output);
        using var bodyFace = ResolveTypeface(false);
        using var monoFace = ResolveTypeface(true);
        var layout = new Layout(document, bodyFace, monoFace, title, metadata);
        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Text)) continue;
            layout.DrawMessage(message);
        }
        layout.Finish();
    }

    private static SKTypeface ResolveTypeface(bool monospace)
    {
        var candidates = monospace
            ? new[] { "Cascadia Mono", "Consolas", "Microsoft YaHei UI", "Microsoft YaHei" }
            : new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" };
        foreach (var name in candidates)
        {
            var face = SKTypeface.FromFamilyName(name);
            if (face is not null) return face;
        }
        return SKTypeface.Default;
    }

    private sealed class Layout
    {
        private readonly SKDocument _document;
        private readonly SKFont _body, _bodyBold, _h1, _h2, _meta, _mono;
        private readonly SKPaint _text, _muted, _accent, _rule, _codeBackground;
        private readonly string _title;
        private readonly IReadOnlyList<(string Label, string Value)> _metadata;
        private SKCanvas _canvas = null!;
        private float _y;
        private int _page;
        private bool _firstPage = true;

        public Layout(SKDocument document, SKTypeface bodyFace, SKTypeface monoFace,
            string title, IReadOnlyList<(string Label, string Value)> metadata)
        {
            _document = document;
            _title = string.IsNullOrWhiteSpace(title) ? "Fufucode 会话" : title;
            _metadata = metadata;
            _body = new SKFont(bodyFace, 10.5f);
            _bodyBold = new SKFont(bodyFace, 10.5f);
            _h1 = new SKFont(bodyFace, 18f);
            _h2 = new SKFont(bodyFace, 13.5f);
            _meta = new SKFont(bodyFace, 9f);
            _mono = new SKFont(monoFace, 9.2f);
            _text = new SKPaint { Color = new SKColor(0x1F, 0x23, 0x28), IsAntialias = true };
            _muted = new SKPaint { Color = new SKColor(0x6B, 0x72, 0x80), IsAntialias = true };
            _accent = new SKPaint { Color = new SKColor(0x2F, 0x6F, 0xED), IsAntialias = true };
            _rule = new SKPaint { Color = new SKColor(0xD8, 0xDD, 0xE5), StrokeWidth = 0.7f, IsAntialias = true };
            _codeBackground = new SKPaint { Color = new SKColor(0xF3, 0xF4, 0xF6), IsAntialias = true };
            BeginPage();
        }

        public void DrawMessage(SessionPdfMessage message)
        {
            EnsureSpace(28f);
            _y += 10f;
            var label = string.IsNullOrWhiteSpace(message.Speaker) ? (message.IsUser ? "你" : "助手") : message.Speaker;
            _canvas.DrawText(label, Margin, _y, SKTextAlign.Left, _bodyBold, message.IsUser ? _accent : _muted);
            _y += 17f;
            DrawMarkdown(message.Text);
            _y += 6f;
        }

        public void Finish()
        {
            EndPage();
            _document.Close();
        }

        private void DrawMarkdown(string text)
        {
            var inCode = false;
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inCode = !inCode;
                    _y += 4f;
                    continue;
                }

                if (inCode)
                {
                    DrawCodeLine(line);
                    continue;
                }

                if (line.Length == 0)
                {
                    _y += 5f;
                    continue;
                }

                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    DrawWrapped(line[2..], _h1, _text, 22f, 22f);
                    continue;
                }
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    DrawWrapped(line[3..], _h2, _text, 18f, 18f);
                    continue;
                }
                if (line.StartsWith("### ", StringComparison.Ordinal))
                {
                    DrawWrapped(line[4..], _bodyBold, _text, 15f, 15f);
                    continue;
                }
                if (line.StartsWith("> ", StringComparison.Ordinal))
                {
                    DrawWrapped("│ " + line[2..], _body, _muted, 15f, 15f, indent: 10f);
                    continue;
                }
                if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                {
                    DrawWrapped("• " + line[2..], _body, _text, 15f, 15f, indent: 10f, hangingIndent: 12f);
                    continue;
                }

                DrawWrapped(line, _body, _text, 15f, 15f);
            }
        }

        private void DrawCodeLine(string line)
        {
            const float lineHeight = 13.5f;
            EnsureSpace(lineHeight + 2f);
            _canvas.DrawRect(new SKRect(Margin - 6f, _y - 9f, PageWidth - Margin + 6f, _y + 4f), _codeBackground);
            var display = line.Length == 0 ? " " : line;
            _canvas.DrawText(display, Margin, _y, SKTextAlign.Left, _mono, _text);
            _y += lineHeight;
        }

        private void DrawWrapped(string text, SKFont font, SKPaint paint, float lineHeight,
            float gapBefore, float indent = 0f, float hangingIndent = 0f)
        {
            _y += gapBefore;
            var available = ContentWidth - indent;
            var lines = Wrap(font, text, available);
            var first = true;
            foreach (var line in lines)
            {
                EnsureSpace(lineHeight);
                var x = Margin + indent + (first ? 0f : hangingIndent);
                _canvas.DrawText(line, x, _y, SKTextAlign.Left, font, paint);
                _y += lineHeight;
                first = false;
            }
        }

        private void EnsureSpace(float height)
        {
            if (_y + height <= PageHeight - Margin - FooterReserve) return;
            EndPage();
            BeginPage();
            _y = Margin;
        }

        private void BeginPage()
        {
            _canvas = _document.BeginPage(PageWidth, PageHeight);
            _page++;
            _y = Margin;
            if (!_firstPage) return;
            _firstPage = false;

            _canvas.DrawText(_title, Margin, _y + 8f, SKTextAlign.Left, _h1, _text);
            _y += 30f;
            foreach (var (label, value) in _metadata)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var line = label + "：" + value;
                _canvas.DrawText(line, Margin, _y, SKTextAlign.Left, _meta, _muted);
                _y += 12f;
            }
            _y += 6f;
            _canvas.DrawLine(Margin, _y, PageWidth - Margin, _y, _rule);
            _y += 18f;
        }

        private void EndPage()
        {
            if (_page == 0) return;
            var y = PageHeight - 28f;
            _canvas.DrawLine(Margin, y - 10f, PageWidth - Margin, y - 10f, _rule);
            _canvas.DrawText("Fufucode · 第 " + _page.ToString(CultureInfo.InvariantCulture) + " 页",
                Margin, y, SKTextAlign.Left, _meta, _muted);
            _document.EndPage();
            _canvas = null!;
        }

        private static List<string> Wrap(SKFont font, string text, float maxWidth)
        {
            var lines = new List<string>();
            var current = new StringBuilder();
            var width = 0f;
            var lastSpace = -1;

            foreach (var rune in text.EnumerateRunes())
            {
                var segment = rune.ToString();
                var segmentWidth = font.MeasureText(segment);
                if (width + segmentWidth > maxWidth && current.Length > 0)
                {
                    if (lastSpace > 0 && lastSpace < current.Length - 1)
                    {
                        var head = current.ToString(0, lastSpace).TrimEnd();
                        var tail = current.ToString(lastSpace + 1, current.Length - lastSpace - 1).TrimStart();
                        lines.Add(head);
                        current.Clear();
                        current.Append(tail);
                        width = font.MeasureText(tail);
                    }
                    else
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                        width = 0f;
                    }
                    lastSpace = -1;
                }

                if (rune.Value == ' ') lastSpace = current.Length;
                current.Append(segment);
                width += segmentWidth;
            }

            if (current.Length > 0) lines.Add(current.ToString().TrimEnd());
            if (lines.Count == 0) lines.Add(string.Empty);
            return lines;
        }
    }
}
