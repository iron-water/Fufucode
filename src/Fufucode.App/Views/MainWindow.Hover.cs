using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Fufucode.App;

public partial class MainWindow
{
    // 行悬停的兜底实现：不依赖 :pointerover（子控件命中、弹层、缓存等各种情况都可能不触发），
    // 由窗口级 PointerMoved 找到最近的行容器，直接加 pointerHover 类。
    private Control? _hoverRow;

    private static readonly string[] RowClasses =
    [
        "settingsRow", "overviewSession", "dockTabRow", "navFill", "changeFileRow", "overviewProject", "modelChoiceRow",
    ];

    private void InitializeRowHover()
    {
        AddHandler(PointerMovedEvent, OnRowPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerExited += (_, _) => ClearHoverRow();
    }

    private void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        var row = FindRow(e.Source as Visual);
        if (ReferenceEquals(row, _hoverRow)) return;
        ClearHoverRow();
        _hoverRow = row;
        row?.Classes.Add("pointerHover");
    }

    private void ClearHoverRow()
    {
        _hoverRow?.Classes.Remove("pointerHover");
        _hoverRow = null;
    }

    private static Control? FindRow(Visual? source)
    {
        for (var cursor = source; cursor is not null; cursor = cursor.GetVisualParent())
        {
            if (cursor is Control control)
                foreach (var name in RowClasses)
                    if (control.Classes.Contains(name)) return control;
        }
        return null;
    }
}
