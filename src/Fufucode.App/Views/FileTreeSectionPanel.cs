using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

/// <summary>文件夹展开只平滑露出内容，不移动、缩放整张目录卡。</summary>
public sealed class FileTreeSectionPanel : ItemsControl
{
    protected override Type StyleKeyOverride => typeof(ItemsControl);
    private INotifyCollectionChanged? _rows;
    private IDisposable? _reveal;
    private double _shownHeight;

    public FileTreeSectionPanel()
    {
        ClipToBounds = true;
        DataContextChanged += (_, _) => ObserveRows();
    }

    private void ObserveRows()
    {
        if (_rows is not null) _rows.CollectionChanged -= OnRowsChanged;
        _rows = (DataContext as FileTreeSectionViewModel)?.Rows;
        if (_rows is not null) _rows.CollectionChanged += OnRowsChanged;
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (VisualRoot is null || !IsEffectivelyVisible || Motion.Reduced) return;
        var target = (DataContext as FileTreeSectionViewModel)?.Rows.Count * 32d ?? 0;
        _reveal?.Dispose();
        var from = _shownHeight > 0 ? _shownHeight : Bounds.Height;
        if (from <= 0 || target <= 0) return;
        // 大目录也只过渡最后半行，避免展开几百像素的整卡滑动。
        from = Math.Clamp(from, Math.Max(0, target - 16), target + 16);
        _shownHeight = from;
        Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(1, Bounds.Width), from));
        _reveal = Motion.AnimateValue(from, target, Motion.Fast, height =>
        {
            _shownHeight = height;
            Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(1, Bounds.Width), height));
        }, () => { Clip = null; _shownHeight = 0; });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveRows();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _reveal?.Dispose();
        _reveal = null;
        Clip = null;
        _shownHeight = 0;
        if (_rows is not null) _rows.CollectionChanged -= OnRowsChanged;
        _rows = null;
        base.OnDetachedFromVisualTree(e);
    }
}
