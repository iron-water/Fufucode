using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Fufucode.App.Features.FilePreview;

/// <summary>只读源码视图：行号、可切换换行，以及不重建内容的行级查找状态。</summary>
public sealed class TextPreviewControl : UserControl
{
    private readonly ScrollViewer _scrollViewer;
    private readonly Grid _rows;
    private readonly IReadOnlyList<string> _lines;
    private readonly IReadOnlyList<TextBlock> _codeBlocks;
    private string _query = string.Empty;
    private int _currentMatch = -1;

    public TextPreviewControl(string text)
    {
        _lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        _rows = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(10) };
        var codeBlocks = new List<TextBlock>(_lines.Count);
        for (var index = 0; index < _lines.Count; index++)
        {
            _rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var number = new TextBlock
            {
                Text = (index + 1).ToString(),
                Classes = { "mono" },
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 12, 0),
            };
            var code = new TextBlock
            {
                Text = _lines[index],
                FontFamily = new FontFamily("Cascadia Mono,Consolas"),
                TextWrapping = TextWrapping.NoWrap,
            };
            Grid.SetRow(number, index); Grid.SetColumn(number, 0);
            Grid.SetRow(code, index); Grid.SetColumn(code, 1);
            _rows.Children.Add(number); _rows.Children.Add(code);
            codeBlocks.Add(code);
        }
        _codeBlocks = codeBlocks;
        _scrollViewer = new ScrollViewer
        {
            Content = _rows,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Content = _scrollViewer;
    }

    public bool IsWordWrapEnabled { get; private set; }
    public string FindStatus { get; private set; } = string.Empty;
    public int MatchCount { get; private set; }
    public int CurrentMatch => _currentMatch;

    public void SetWordWrap(bool enabled)
    {
        IsWordWrapEnabled = enabled;
        foreach (var block in _codeBlocks)
            block.TextWrapping = enabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _scrollViewer.HorizontalScrollBarVisibility = enabled
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
    }

    public string Find(string? query, bool forward = true)
    {
        _query = query?.Trim() ?? string.Empty;
        if (_query.Length == 0)
        {
            MatchCount = 0;
            _currentMatch = -1;
            FindStatus = string.Empty;
            return FindStatus;
        }

        var matches = _lines
            .Select((line, index) => (line, index))
            .Where(item => item.line.Contains(_query, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();
        MatchCount = matches.Length;
        if (matches.Length == 0)
        {
            _currentMatch = -1;
            FindStatus = "未找到";
            return FindStatus;
        }

        var position = Array.IndexOf(matches, _currentMatch);
        position = forward
            ? (position + 1) % matches.Length
            : (position <= 0 ? matches.Length - 1 : position - 1);
        _currentMatch = matches[position];
        FindStatus = $"{position + 1}/{matches.Length} · 第 {_currentMatch + 1} 行";
        var row = _codeBlocks[_currentMatch];
        Dispatcher.UIThread.Post(() => _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, Math.Max(0, row.Bounds.Top)), DispatcherPriority.Background);
        return FindStatus;
    }
}
