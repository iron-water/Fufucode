using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// 严格对齐 yetone/magpie context.js 的上下文可视化控件：
/// 1. 支持 Map（矩形树图）与 Grid（400 格华夫网格）双视图无缝切换。
/// 2. Map 视图采用 Magpie 的 share^0.35 温和幂律曲线，避免低占用时树图塌陷为细线。
/// 3. Grid 视图绘制 400 个 1/400 窗口单元格，支持开场波浪级联弹入（CubicEaseOut 动效）。
/// 4. 支持类别/条目悬停对焦（未对焦区域平滑降至 0.16 透明度，对焦区域放大 1.12 倍并高亮）。
/// 5. 悬停浮动跟随 ToolTip 卡片。
/// </summary>
internal sealed class PerfTreemapControl : Control
{
    private static readonly Typeface BoldFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface RegularFace = new(FontFamily.Default);
    private const int GridCells = 400;

    private readonly List<(Rect Rect, PerfContextItem? Item, string? CategoryKey)> _hits = [];
    private IReadOnlyList<PerfContextCategory> _categories = [];
    private long _totalTokens;
    private long _windowCapacity = 200_000;

    // 视图模式：map（树图）或 grid（400格网格）
    private string _lookMode = "map";

    // 动画状态
    private double _animProgress = 1.0;
    private long _animStartTicks;
    private DispatcherTimer? _animTimer;
    private int _hoveredIndex = -1;
    private Point _mousePos;

    // 对焦过滤（由外部列表悬停或内部图例设置）
    private string? _focusCategory;
    private PerfContextItem? _focusItem;

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
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public string LookMode
    {
        get => _lookMode;
        set
        {
            if (_lookMode == value) return;
            _lookMode = value;
            StartBloomAnimation();
        }
    }

    public IBrush? MutedBrush { get => GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public IBrush? CardBackgroundBrush { get => GetValue(CardBackgroundBrushProperty); set => SetValue(CardBackgroundBrushProperty, value); }

    public event Action<string?>? CategoryClicked;

    public void SetFocus(string? categoryKey, PerfContextItem? item)
    {
        _focusCategory = categoryKey;
        _focusItem = item;
        InvalidateVisual();
    }

    public void SetData(IReadOnlyList<PerfContextCategory> categories, long totalTokens, long? windowCapacity = null)
    {
        _categories = categories;
        _totalTokens = totalTokens;
        _windowCapacity = windowCapacity is > 0 ? windowCapacity.Value : Math.Max(200_000, totalTokens);

        StartBloomAnimation();
    }

    private void StartBloomAnimation()
    {
        _animTimer?.Stop();
        _animProgress = 0.0;
        _animStartTicks = Stopwatch.GetTimestamp();

        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _animTimer.Tick += (_, _) =>
        {
            var elapsedMs = (Stopwatch.GetTimestamp() - _animStartTicks) * 1000.0 / Stopwatch.Frequency;
            var t = Math.Clamp(elapsedMs / 380.0, 0, 1);
            // CubicEaseOut: 1 - (1 - t)^3
            _animProgress = 1.0 - Math.Pow(1.0 - t, 3);
            InvalidateVisual();

            if (t >= 1.0)
            {
                _animProgress = 1.0;
                _animTimer.Stop();
                _animTimer = null;
            }
        };
        _animTimer.Start();
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
            var emptyText = new FormattedText("暂无上下文数据 · 开始会话后生成上下文窗口构成分布",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, RegularFace, 11,
                MutedBrush ?? Brushes.Gray);
            context.DrawText(emptyText, new Point(
                Math.Max(10, (width - emptyText.Width) / 2),
                Math.Max(10, (height - emptyText.Height) / 2)));
            return;
        }

        if (_lookMode == "grid")
        {
            RenderWaffleGrid(context, width, height);
        }
        else
        {
            RenderTreemapMap(context, width, height);
        }

        // 鼠标悬停 ToolTip 浮层
        if (_hoveredIndex >= 0 && _hoveredIndex < _hits.Count)
        {
            DrawHoverTooltip(context, _hits[_hoveredIndex]);
        }
    }

    // ──────────────────────────── 1. Magpie 树图算法 (Map 模式) ────────────────────────────

    private void RenderTreemapMap(DrawingContext context, double width, double height)
    {
        var scale = Math.Max(_windowCapacity, _totalTokens);
        var across = width >= height;
        var longSide = across ? width : height;

        // 对齐 yetone/magpie: share^0.35 温和幂律曲线，使 8% 的占用在画面上仍占 ~41% 面积以供清晰阅读
        double usedLen = longSide;
        if (_totalTokens < scale)
        {
            var ratio = (double)_totalTokens / scale;
            usedLen = Math.Min(longSide, Math.Max(longSide * Math.Pow(ratio, 0.35), Math.Min(longSide, 48)));
        }

        var usedRect = across ? new Rect(0, 0, usedLen, height) : new Rect(0, 0, width, usedLen);

        // 递归切分已有 Token 的类别
        var catItems = _categories.Where(c => c.TotalTokens > 0)
            .Select(c => (Data: (object)c, Weight: (double)c.TotalTokens))
            .ToList();

        SplitRect(usedRect, catItems, (catObj, catRect) =>
        {
            var cat = (PerfContextCategory)catObj;
            DrawCategoryTiles(context, cat, catRect);
        });

        // 剩余未用空间（Free Space）：带虚线描边与淡淡底色
        if (_totalTokens < scale)
        {
            var freeRect = across
                ? new Rect(usedLen, 0, width - usedLen, height)
                : new Rect(0, usedLen, width, height - usedLen);
            DrawFreeTile(context, freeRect.Deflate(2), scale - _totalTokens);
        }
    }

    private void DrawFreeTile(DrawingContext context, Rect rect, long freeTokens)
    {
        if (rect.Width <= 4 || rect.Height <= 4) return;
        var brush = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(50, 148, 163, 184)), 1, new DashStyle([4, 4], 0));

        context.DrawRectangle(brush, pen, rect, 6, 6);
        _hits.Add((rect, null, "free"));

        if (rect.Width >= 50 && rect.Height >= 28)
        {
            var title = new FormattedText("剩余空间", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, RegularFace, 10, MutedBrush ?? Brushes.Gray);
            var sub = new FormattedText(PerfPalette.FormatTokens(freeTokens), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, RegularFace, 9, MutedBrush ?? Brushes.Gray);
            context.DrawText(title, new Point(rect.X + 8, rect.Y + 6));
            context.DrawText(sub, new Point(rect.X + 8, rect.Y + 20));
        }
    }

    private void DrawCategoryTiles(DrawingContext context, PerfContextCategory category, Rect rect)
    {
        var inset = rect.Deflate(2);
        if (inset.Width <= 6 || inset.Height <= 6) return;

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

        var currentIndex = _hits.Count;
        var isHovered = _hoveredIndex == currentIndex;

        // 对焦判定：当外部指定了 FocusCategory 或 FocusItem 时，未对焦者变暗至 0.16
        var isFocused = (_focusCategory is null && _focusItem is null)
            || (_focusCategory == categoryKey)
            || (_focusItem is not null && item is not null && ReferenceEquals(_focusItem, item));

        var opacity = isFocused ? _animProgress : 0.16 * _animProgress;

        // 动效插值：从中心展开绽放 (scale: 0.88 -> 1.0)
        var scale = 0.88 + 0.12 * _animProgress;
        var cx = tile.Center.X;
        var cy = tile.Center.Y;
        var animWidth = tile.Width * scale;
        var animHeight = tile.Height * scale;
        var drawTile = new Rect(cx - animWidth / 2, cy - animHeight / 2, animWidth, animHeight);

        var fillAlpha = (byte)(isHovered ? 75 : 38);
        var strokeAlpha = (byte)(isHovered ? 240 : 85);
        var strokeWidth = isHovered ? 1.8 : 1.0;

        var fillBrush = new SolidColorBrush(Color.FromArgb(fillAlpha, color.R, color.G, color.B));
        var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(strokeAlpha, color.R, color.G, color.B)), strokeWidth);

        using (context.PushOpacity(opacity))
        {
            context.DrawRectangle(fillBrush, borderPen, isHovered ? drawTile.Inflate(1) : drawTile, 6, 6);
            _hits.Add((tile, item, categoryKey));

            if (tile.Width >= 48 && tile.Height >= 32)
            {
                using (context.PushClip(drawTile.Deflate(4)))
                {
                    var titleText = new FormattedText(title, CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, BoldFace, 11, Brushes.White);
                    context.DrawText(titleText, new Point(drawTile.X + 6, drawTile.Y + 5));

                    var subColor = isHovered ? Color.FromArgb(255, color.R, color.G, color.B) : Color.FromArgb(210, color.R, color.G, color.B);
                    var subBrush = new SolidColorBrush(subColor);
                    var subText = new FormattedText(subtitle, CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, RegularFace, 9.5, subBrush);
                    context.DrawText(subText, new Point(drawTile.X + 6, drawTile.Y + 21));
                }
            }
            else if (tile.Width >= 30 && tile.Height >= 16)
            {
                using (context.PushClip(drawTile.Deflate(2)))
                {
                    var compactText = new FormattedText(title, CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, RegularFace, 9, Brushes.White);
                    context.DrawText(compactText, new Point(drawTile.X + 3, drawTile.Y + (drawTile.Height - compactText.Height) / 2));
                }
            }
        }
    }

    // ──────────────────────────── 2. Magpie 400格华夫网格 (Grid 模式) ────────────────────────────

    private void RenderWaffleGrid(DrawingContext context, double width, double height)
    {
        var cols = width <= 520 ? 25 : 50;
        var rows = GridCells / cols; // 8 or 16 rows

        var gap = 2.0;
        var cellW = (width - gap * (cols - 1)) / cols;
        var cellH = (height - gap * (rows - 1)) / rows;
        var cellSize = Math.Min(cellW, cellH);

        // 重新居中网格
        var gridTotalW = cols * cellSize + (cols - 1) * gap;
        var gridTotalH = rows * cellSize + (rows - 1) * gap;
        var startX = Math.Max(0, (width - gridTotalW) / 2);
        var startY = Math.Max(0, (height - gridTotalH) / 2);

        // 计算 400 个单元格分配
        var cells = ComputeWaffleCells();

        for (var i = 0; i < GridCells; i++)
        {
            var col = i % cols;
            var row = i / cols;

            var x = startX + col * (cellSize + gap);
            var y = startY + row * (cellSize + gap);
            var rect = new Rect(x, y, cellSize, cellSize);

            var (kind, item) = cells[i];
            var isFree = string.IsNullOrEmpty(kind) || kind == "free";
            var color = isFree ? PerfPalette.CtxFree : PerfPalette.ContextCategoryColor(kind ?? "other");

            var currentIndex = _hits.Count;
            var isHovered = _hoveredIndex == currentIndex;

            // 对焦判断
            var isFocused = (_focusCategory is null && _focusItem is null)
                || (_focusCategory == kind)
                || (_focusItem is not null && item is not null && ReferenceEquals(_focusItem, item));

            // 波浪级联动效：按列级联展开
            var colProgress = Math.Clamp((_animProgress * 1.5) - (col / (double)cols * 0.5), 0, 1);
            var cellOpacity = (isFocused ? 1.0 : 0.16) * colProgress;
            var cellScale = isHovered ? 1.18 : (0.4 + 0.6 * colProgress);

            var cx = rect.Center.X;
            var cy = rect.Center.Y;
            var dw = rect.Width * cellScale;
            var dh = rect.Height * cellScale;
            var drawRect = new Rect(cx - dw / 2, cy - dh / 2, dw, dh);

            using (context.PushOpacity(cellOpacity))
            {
                if (isFree)
                {
                    var freeBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
                    var freePen = new Pen(new SolidColorBrush(Color.FromArgb(40, 148, 163, 184)), 0.8);
                    context.DrawRectangle(freeBrush, freePen, drawRect, 2, 2);
                }
                else
                {
                    var brush = new SolidColorBrush(Color.FromArgb(isHovered ? (byte)255 : (byte)210, color.R, color.G, color.B));
                    context.DrawRectangle(brush, null, drawRect, 2, 2);
                }
            }

            _hits.Add((rect.Inflate(1), item, kind));
        }
    }

    private List<(string? Kind, PerfContextItem? Item)> ComputeWaffleCells()
    {
        var scale = Math.Max(_windowCapacity, _totalTokens);
        var usedCells = Math.Min(GridCells, (int)Math.Round((double)_totalTokens / scale * GridCells));

        var parts = _categories.Where(c => c.TotalTokens > 0).ToList();
        var sum = parts.Sum(c => c.TotalTokens);
        if (sum <= 0)
        {
            return Enumerable.Repeat<(string? Kind, PerfContextItem? Item)>((null, null), GridCells).ToList();
        }

        // 最大余数算法（Hamilton-Hare Method）精准分配 400 个格子
        var wants = parts.Select(p => ((double)p.TotalTokens / sum) * usedCells).ToList();
        var gots = wants.Select(w => Math.Max(1, (int)Math.Floor(w))).ToList();
        var left = usedCells - gots.Sum();

        var remainders = wants.Select((w, i) => (Rem: w - Math.Floor(w), Index: i)).OrderByDescending(x => x.Rem).ToList();
        for (var j = 0; left > 0 && j < remainders.Count; j++, left--)
            gots[remainders[j].Index]++;

        for (var j = gots.Count - 1; left < 0 && j >= 0; j--)
            while (left < 0 && gots[j] > 1) { gots[j]--; left++; }

        var result = new List<(string? Kind, PerfContextItem? Item)>(GridCells);
        for (var pi = 0; pi < parts.Count; pi++)
        {
            var p = parts[pi];
            var count = gots[pi];
            var items = p.Items.Where(i => i.Tokens > 0).OrderByDescending(i => i.Tokens).ToList();
            var isum = items.Sum(i => i.Tokens);

            var at = 0;
            var acc = 0L;
            for (var c = 0; c < count; c++)
            {
                var mid = ((c + 0.5) / count) * isum;
                while (at < items.Count - 1 && acc + items[at].Tokens < mid)
                    acc += items[at++].Tokens;

                result.Add((p.Key, items.Count > 0 ? items[at] : null));
            }
        }

        while (result.Count < GridCells)
            result.Add(("free", null));

        return result;
    }

    // ──────────────────────────── 3. 悬停浮层与交互 ────────────────────────────

    private void DrawHoverTooltip(DrawingContext context, (Rect Rect, PerfContextItem? Item, string? CategoryKey) hit)
    {
        var item = hit.Item;
        var isFree = hit.CategoryKey == "free";
        var title = isFree ? "剩余未用空间" : (item?.Name ?? hit.CategoryKey ?? "未知");
        var tokens = isFree ? Math.Max(0, _windowCapacity - _totalTokens) : (item?.Tokens ?? 0);
        var percent = _windowCapacity > 0 ? (double)tokens / _windowCapacity : 0;
        var info = $"{PerfPalette.FormatTokens(tokens)} token · {PerfPalette.FormatPercent(percent)}";

        var formattedTitle = new FormattedText(title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, BoldFace, 11, Brushes.White);
        var formattedInfo = new FormattedText(info, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, RegularFace, 10, new SolidColorBrush(Color.Parse("#94A3B8")));

        var padW = 20;
        var tipW = Math.Max(formattedTitle.Width, formattedInfo.Width) + padW;
        var tipH = 38;

        var tipX = Math.Clamp(_mousePos.X + 12, 6, Math.Max(6, Bounds.Width - tipW - 8));
        var tipY = _mousePos.Y + 16 + tipH > Bounds.Height ? Math.Max(6, _mousePos.Y - tipH - 8) : _mousePos.Y + 16;
        var tipRect = new Rect(tipX, tipY, tipW, tipH);

        var bgBrush = new SolidColorBrush(Color.FromArgb(235, 15, 23, 42));
        var strokePen = new Pen(new SolidColorBrush(Color.FromArgb(120, 148, 163, 184)), 1);
        context.DrawRectangle(bgBrush, strokePen, tipRect, 6, 6);

        context.DrawText(formattedTitle, new Point(tipX + 10, tipY + 5));
        context.DrawText(formattedInfo, new Point(tipX + 10, tipY + 20));
    }

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

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _mousePos = e.GetPosition(this);
        var oldHover = _hoveredIndex;
        _hoveredIndex = -1;
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            if (_hits[i].Rect.Contains(_mousePos))
            {
                _hoveredIndex = i;
                break;
            }
        }
        if (oldHover != _hoveredIndex) InvalidateVisual();
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_hoveredIndex != -1)
        {
            _hoveredIndex = -1;
            InvalidateVisual();
        }
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
