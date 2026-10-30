using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Fufucode.App.Features.FilePreview;

public sealed record CsvPreviewData(IReadOnlyList<string> Headers, IReadOnlyList<FilePreviewTableRow> Rows,
    bool IsTruncated);

/// <summary>有界 CSV/TSV 解析与虚拟化行视图。首条记录作表头，列宽固定以保持行间对齐。</summary>
public sealed class CsvPreviewControl : UserControl
{
    public const int MaximumRows = 10000;
    public const int MaximumColumns = 32;
    public const int MaximumCellCharacters = 8192;
    public const int MaximumDisplayedCellCharacters = 1024;

    public CsvPreviewControl(FilePreviewResult result)
    {
        var headers = result.Headers ?? [];
        var rows = result.Rows ?? [];
        DisplayedRowCount = Math.Min(rows.Count, MaximumRows);
        DisplayedColumnCount = Math.Min(MaximumColumns,
            Math.Max(headers.Count, rows.Take(DisplayedRowCount).Select(row => row.Cells.Count).DefaultIfEmpty().Max()));
        var list = new ListBox
        {
            ItemsSource = Enumerable.Range(0, DisplayedRowCount + 1).ToArray(),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<int>((index, _) => BuildRow(index)),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
        };
        list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters = { new Setter(PaddingProperty, new Thickness(0)), new Setter(MinHeightProperty, 0d) },
        });
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var limited = result.IsTruncated || rows.Count > MaximumRows || headers.Count > MaximumColumns ||
            rows.Take(DisplayedRowCount).Any(row => row.Cells.Count > MaximumColumns);
        layout.Children.Add(new TextBlock
        {
            Text = $"{DisplayedRowCount:N0} 行 · {DisplayedColumnCount} 列（首条记录为表头）" +
                (limited ? $" · 已限量，最多 {MaximumRows:N0} 行 / {MaximumColumns} 列 / 单元格 {MaximumCellCharacters:N0} 字符" : string.Empty) +
                $" · 单元格显示前 {MaximumDisplayedCellCharacters:N0} 字符，悬停查看已载入内容",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 6),
        });
        Grid.SetRow(list, 1);
        layout.Children.Add(list);
        Content = layout;

        Control BuildRow(int index)
        {
            var header = index == 0;
            var cells = header ? headers : rows[index - 1].Cells;
            var grid = new Grid { Width = DisplayedColumnCount * 220, MinHeight = 30 };
            for (var column = 0; column < DisplayedColumnCount; column++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(220)));
                var value = column < cells.Count ? cells[column] : header ? $"列 {column + 1}" : string.Empty;
                if (header && string.IsNullOrWhiteSpace(value)) value = $"列 {column + 1}";
                var shown = value.Length > MaximumDisplayedCellCharacters ? value[..MaximumDisplayedCellCharacters] + "…" : value;
                var block = new TextBlock
                {
                    Text = shown.Replace("\r\n", " ↵ ").Replace('\n', '↵').Replace('\r', '↵'),
                    Padding = new Thickness(8, 5),
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = header ? FontWeight.Bold : FontWeight.Normal,
                };
                Avalonia.Automation.AutomationProperties.SetHelpText(block, value);
                Grid.SetColumn(block, column);
                grid.Children.Add(block);
            }
            return grid;
        }
    }

    public int DisplayedRowCount { get; }
    public int DisplayedColumnCount { get; }

    /// <summary>保留引号内分隔符/换行，两个双引号解码为一个；格式错误明确抛出 FormatException。</summary>
    public static CsvPreviewData Parse(string text, char delimiter, CancellationToken cancellationToken = default)
    {
        if (delimiter is not (',' or '\t')) throw new ArgumentOutOfRangeException(nameof(delimiter));
        cancellationToken.ThrowIfCancellationRequested();
        var records = new List<IReadOnlyList<string>>();
        var cells = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        var closedQuote = false;
        var started = false;
        var fieldStarted = false;
        var truncated = false;
        var column = 0;
        var widest = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"') { Append('"'); index++; }
                    else { quoted = false; closedQuote = true; }
                }
                else Append(character);
                continue;
            }
            if (character == delimiter)
            {
                EndField();
                started = true;
                continue;
            }
            if (character is '\r' or '\n')
            {
                EndField();
                records.Add(cells.ToArray());
                cells.Clear(); column = 0; started = false;
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                if (records.Count == MaximumRows + 1)
                {
                    truncated |= index + 1 < text.Length;
                    break;
                }
                continue;
            }
            if (closedQuote) throw new FormatException($"第 {records.Count + 1} 条记录：结束引号后存在非分隔字符。");
            if (character == '"')
            {
                if (fieldStarted) throw new FormatException($"第 {records.Count + 1} 条记录：未加引号的字段中存在双引号。");
                quoted = true; fieldStarted = true; started = true;
                continue;
            }
            Append(character);
            started = true;
            fieldStarted = true;
        }
        if (quoted) throw new FormatException("CSV 引号未闭合。");
        if (started || cells.Count > 0 || closedQuote)
        {
            EndField();
            if (records.Count < MaximumRows + 1) records.Add(cells.ToArray());
            else truncated = true;
        }
        if (records.Count == 0) return new([], [], truncated);
        var headers = records[0].ToList();
        while (headers.Count < widest) headers.Add($"列 {headers.Count + 1}");
        return new(headers, records.Skip(1).Select(record => new FilePreviewTableRow(record)).ToArray(), truncated);

        void Append(char character)
        {
            if (column >= MaximumColumns) { truncated = true; return; }
            if (value.Length < MaximumCellCharacters) value.Append(character);
            else truncated = true;
        }

        void EndField()
        {
            if (column < MaximumColumns) cells.Add(value.ToString());
            else truncated = true;
            value.Clear(); column++;
            widest = Math.Max(widest, Math.Min(column, MaximumColumns));
            fieldStarted = false; closedQuote = false;
        }
    }
}
