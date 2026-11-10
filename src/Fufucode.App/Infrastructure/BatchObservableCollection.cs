using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Fufucode.App.Infrastructure;

/// <summary>历史回放期间只发布一次 Reset，避免侧边刻度按每条消息重建整棵控件树。</summary>
internal sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    private int _depth;
    private bool _changed;
    internal void BeginUpdate() => _depth++;
    internal void EndUpdate()
    {
        if (_depth == 0 || --_depth != 0 || !_changed) return;
        _changed = false;
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        base.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_depth > 0) { _changed = true; return; }
        base.OnCollectionChanged(e);
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_depth == 0) base.OnPropertyChanged(e);
    }
}
