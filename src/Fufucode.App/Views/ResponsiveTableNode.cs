using System;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Layout;
using LiveMarkdown.Avalonia;
using Markdig.Extensions.Tables;

namespace Fufucode.App.Views;

/// <summary>保留 LiveMarkdown 的单元格增量更新，只替换表格的布局与查看容器。</summary>
public sealed class ResponsiveTableNode : BlockNode<Table>
{
    private readonly MarkdownTableView _view = new();
    private readonly MarkdownRenderer.BlocksProxy _cells;

    public ResponsiveTableNode() => _cells = new(_view.Table.Children);

    public override Control Control => _view;

    protected override bool UpdateCore(DocumentNode documentNode, Table table,
        in ObservableStringBuilderChangedEventArgs change, CancellationToken cancellationToken)
    {
        if (table.ColumnDefinitions.Count == 0) return false;
        var rows = table.OfType<TableRow>().ToArray();
        var index = 0;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            var column = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index >= _cells.Count)
                    _cells.Add(CreateBlockNode(documentNode, cell, change, cancellationToken));
                else if (_cells[index].Update(documentNode, cell, change, cancellationToken) == false)
                    _cells[index] = CreateBlockNode(documentNode, cell, change, cancellationToken);

                var control = _cells[index++].Control;
                Grid.SetRow(control, rowIndex);
                Grid.SetColumn(control, column);
                Grid.SetColumnSpan(control, Math.Max(1, cell.ColumnSpan));
                control.Classes.Set("Header", row.IsHeader);
                control.Classes.Set("LastColumn", column + cell.ColumnSpan >= table.ColumnDefinitions.Count);
                control.Classes.Set("LastRow", rowIndex == rows.Length - 1);
                if (column < table.ColumnDefinitions.Count && control is Border { Child: { } child })
                    child.HorizontalAlignment = table.ColumnDefinitions[column].Alignment switch
                    {
                        TableColumnAlign.Left => HorizontalAlignment.Left,
                        TableColumnAlign.Center => HorizontalAlignment.Center,
                        TableColumnAlign.Right => HorizontalAlignment.Right,
                        _ => HorizontalAlignment.Stretch,
                    };
                column += Math.Max(1, cell.ColumnSpan);
            }
        }
        while (_cells.Count > index) _cells.RemoveAt(_cells.Count - 1);
        var source = documentNode.Owner.MarkdownBuilder?.ToString() ?? string.Empty;
        if (table.Span.Start >= 0 && table.Span.End >= table.Span.Start && table.Span.End < source.Length)
            _view.Markdown = source.Substring(table.Span.Start, table.Span.End - table.Span.Start + 1);
        _view.Table.InvalidateMeasure();
        return index > 0;
    }
}
