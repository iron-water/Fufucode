using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 类似 Magpie / Claude 上下文窗口的矩形树图（Treemap）：
/// 按 Token 占比将矩形区域自适应划分为多层色块，展示系统提示词、工具定义、记忆、文件、工具结果与会话内容。
/// </summary>
internal sealed class PerfTreemapControl : Control
{
    private static readonly Typeface BoldFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface RegularFace = new(FontFamily.Default);

    private readonly List<(Rect Rect, PerfContextItem? Item, string? CategoryKey)> _hits = [];
    private IReadOnlyList<PerfContextCategory> _categories = [];
    private long _totalTokens;

    public static readonly StyledProperty<IBrush?> MutedBrushProperty =
        AvaloniaProperty.Register<PerfTreemapControl, IBrush?>(nameof(MutedBrush));
    public static readonly StyledProperty<IBrush?> CardBackgroundBrushProperty =
        AvaloniaProperty.Register<PerfTreemapControl, IBrush?>(nameof(CardBackgroundBrush));

    public PerfTreemapControl()
    {
        ClipToBounds = true;
        Height = 220;
        this.Bind(MutedBrushProperty, new DynamicResourceExtension("AppTextDim"));
        this.Bind(CardBackgroundBrushProperty, new DynamicResourceExtension("AppCardBackground"));
        PointerPressed += OnPointerPressed;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public IBrush? MutedBrush { get => GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public IBrush? CardBackgroundBrush { get => GetValue(CardBackgroundBrushProperty); set => SetValue(CardBackgroundBrushProperty, value); }

    public event Action<string?>? CategoryClicked;

    public void SetData(IReadOnlyList<PerfContextCategory> categories, long totalTokens)
    {
        _categories = categories;
        _totalTokens = totalTokens;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _hits.Clear();
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 20 || height <= 20) return;

        var fullRect = new Rect(0, 0, width, height);

        if (_categories.Count == 0 || _totalTokens <= 0)
        {
            // 空状态：虚线框 + 提示
            var borderPen = new Pen(MutedBrush ?? Brushes.Gray, 1, new DashStyle([4, 4], 0));
            context.DrawRectangle(CardBackgroundBrush, borderPen, fullRect, 10, 10);
            var emptyText = new FormattedText("暂无上下文数据 · 开始对话后生成上下文构成分布",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, RegularFace, 11,
                MutedBrush ?? Brushes.Gray);
            context.DrawText(emptyText, new Point(
                Math.Max(10, (width - emptyText.Width) / 2),
                Math.Max(10, (height - emptyText.Height) / 2)));
            return;
        }

        // 递归拆分出每个类别的区域
        var catItems = _categories.Where(c => c.TotalTokens > 0)
            .Select(c => (Data: (object)c, Weight: (double)c.TotalTokens))
            .ToList();

        SplitRect(fullRect, catItems, (catObj, catRect) =>
        {
            var cat = (PerfContextCategory)catObj;
            DrawCategory(context, cat, catRect);
        });
    }

    private void DrawCategory(DrawingContext context, PerfContextCategory category, Rect rect)
    {
        var inset = rect.Deflate(2);
        if (inset.Width <= 6 || inset.Height <= 6) return;

        // 如果该类别有多个子项且区域足够大，对子项再做一次细分
        var topItems = category.Items.Where(i => i.Tokens > 0).Take(6).ToList();
        if (topItems.Count > 1 && inset.Width >= 70 && inset.Height >= 46)
        {
            var itemWeights = topItems.Select(i => (Data: (object)i, Weight: (double)i.Tokens)).ToList();
            SplitRect(inset, itemWeights, (itemObj, itemRect) =>
            {
                var item = (PerfContextItem)itemObj;
                DrawTile(context, item.Name, $"{PerfPalette.FormatTokens(item.Tokens)} · {item.CategoryName}",
                    item.Color, itemRect, item, category.Key);
            });
        }
        else
        {
            // 单个类别块展示
            var sub = category.Items.Count > 0 ? category.Items[0].Name : category.Name;
            var detail = $"{PerfPalette.FormatTokens(category.TotalTokens)} · {category.Name}";
            DrawTile(context, sub, detail, category.Color, inset,
                category.Items.Count > 0 ? category.Items[0] : null, category.Key);
        }
    }

    private void DrawTile(DrawingContext context, string title, string subtitle, Color color,
        Rect rect, PerfContextItem? item, string categoryKey)
    {
        var tile = rect.Deflate(1.5);
        if (tile.Width <= 4 || tile.Height <= 4) return;

        // 瓷砖背景：深色底 + 类别色半透明柔光
        var fillBrush = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B));
        var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(85, color.R, color.G, color.B)), 1);

        context.DrawRectangle(fillBrush, borderPen, tile, 6, 6);
        _hits.Add((tile, item, categoryKey));

        // 仅在空间充足时绘制两行文字
        if (tile.Width >= 48 && tile.Height >= 32)
        {
            using (context.PushClip(tile.Deflate(4)))
            {
                var titleText = new FormattedText(title, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, BoldFace, 11, Brushes.White);
                context.DrawText(titleText, new Point(tile.X + 6, tile.Y + 5));

                var subBrush = new SolidColorBrush(Color.FromArgb(220, color.R, color.G, color.B));
                var subText = new FormattedText(subtitle, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, RegularFace, 9.5, subBrush);
                context.DrawText(subText, new Point(tile.X + 6, tile.Y + 21));
            }
        }
        else if (tile.Width >= 30 && tile.Height >= 16)
        {
            using (context.PushClip(tile.Deflate(2)))
            {
                var compactText = new FormattedText(title, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, RegularFace, 9, Brushes.White);
                context.DrawText(compactText, new Point(tile.X + 3, tile.Y + (tile.Height - compactText.Height) / 2));
            }
        }
    }

    /// <summary>
    /// 二分切分 Treemap 布局算法：保持长宽比接近 1，无外部依赖。
    /// </summary>
    private static void SplitRect(Rect bounds, List<(object Data, double Weight)> items, Action<object, Rect> emit)
    {
        if (items.Count == 0) return;
        if (items.Count == 1)
        {
            emit(items[0].Data, bounds);
            return;
        }

        var totalWeight = items.Sum(x => x.Weight);
        if (totalWeight <= 0) return;

        var half = totalWeight / 2.0;
        var acc = 0.0;
        var splitIndex = 1;
        for (var i = 0; i < items.Count - 1; i++)
        {
            acc += items[i].Weight;
            if (acc >= half)
            {
                var diffPrev = Math.Abs((acc - items[i].Weight) - half);
                var diffCurr = Math.Abs(acc - half);
                splitIndex = diffCurr < diffPrev ? i + 1 : Math.Max(1, i);
                break;
            }
        }

        var leftItems = items.Take(splitIndex).ToList();
        var rightItems = items.Skip(splitIndex).ToList();
        var leftWeight = leftItems.Sum(x => x.Weight);

        Rect leftRect, rightRect;
        if (bounds.Width >= bounds.Height)
        {
            var leftWidth = bounds.Width * (leftWeight / totalWeight);
            leftRect = new Rect(bounds.X, bounds.Y, leftWidth, bounds.Height);
            rightRect = new Rect(bounds.X + leftWidth, bounds.Y, bounds.Width - leftWidth, bounds.Height);
        }
        else
        {
            var topHeight = bounds.Height * (leftWeight / totalWeight);
            leftRect = new Rect(bounds.X, bounds.Y, bounds.Width, topHeight);
            rightRect = new Rect(bounds.X, bounds.Y + topHeight, bounds.Width, bounds.Height - topHeight);
        }

        SplitRect(leftRect, leftItems, emit);
        SplitRect(rightRect, rightItems, emit);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            if (_hits[i].Rect.Contains(pos))
            {
                CategoryClicked?.Invoke(_hits[i].CategoryKey);
                return;
            }
        }
        CategoryClicked?.Invoke(null);
    }
}
