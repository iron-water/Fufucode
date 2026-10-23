using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

/// <summary>
/// 一行 diff 的富文本：语法着色的片段 + 词级强调。
///
/// 用 Inlines 而不是每段一个 TextBlock：段数可能上百，
/// 而且没有颜色的片段必须真正"不设前景色"才能继承正文色——
/// 把 Foreground 绑定成 null 会画成透明，文字看起来就像丢了。
/// </summary>
public sealed class DiffLineText : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<DiffRun>?> RunsProperty =
        AvaloniaProperty.Register<DiffLineText, IReadOnlyList<DiffRun>?>(nameof(Runs));

    /// <summary>词级强调的底色按行类型取：新增行用绿底、删除行用红底。</summary>
    public static readonly StyledProperty<bool> IsAddedRowProperty =
        AvaloniaProperty.Register<DiffLineText, bool>(nameof(IsAddedRow));

    public IReadOnlyList<DiffRun>? Runs
    {
        get => GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    public bool IsAddedRow
    {
        get => GetValue(IsAddedRowProperty);
        set => SetValue(IsAddedRowProperty, value);
    }

    /// <summary>是否折行；折行会关闭横向滚动，长行在窄栏里也能完整读到。</summary>
    public static readonly StyledProperty<bool> WrapProperty =
        AvaloniaProperty.Register<DiffLineText, bool>(nameof(Wrap));

    public bool Wrap
    {
        get => GetValue(WrapProperty);
        set => SetValue(WrapProperty, value);
    }

    public DiffLineText()
    {
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WrapProperty)
            TextWrapping = Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (change.Property == RunsProperty || change.Property == IsAddedRowProperty) Rebuild();
    }

    private void Rebuild()
    {
        Inlines ??= new InlineCollection();
        Inlines.Clear();
        if (Runs is null) return;
        var emphasis = EmphasisBrush();
        foreach (var run in Runs)
        {
            var inline = new Run(run.Text);
            if (run.Foreground is { } foreground) inline.Foreground = foreground;
            if (run.Emphasis && emphasis is not null) inline.Background = emphasis;
            Inlines.Add(inline);
        }
    }

    /// <summary>强调底色取主题令牌；取不到就让强调退化为纯色，不阻断整行渲染。</summary>
    private IBrush? EmphasisBrush()
    {
        var key = IsAddedRow ? "AppDiffAddedStrong" : "AppDiffRemovedStrong";
        return this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
    }
}
