using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.Core.Automation;

namespace Fufucode.App.Features.Automation;

public partial class AutomationControlPageView : UserControl
{
    private AutomationControlViewModel? _model;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    public AutomationControlPageView()
    {
        InitializeComponent(); _timer.Tick += async (_, _) => { if (_model is { } model) await model.RefreshAsync(); };
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }
    /// <summary>
    /// v4 响应式：宽于 1000DIP 时主操作靠右同排；窄于此值时整行下移，标题与操作都不被裁切。
    /// 800×640 只有一列，详情改为底部区域，不横向溢出到整个应用。
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        if (HeaderActions is null || WorkbenchHeader is null) return;
        var narrow = Bounds.Width < 1000;
        WorkbenchHeader.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,Auto");
        WorkbenchHeader.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
        Grid.SetColumn(HeaderActions, narrow ? 0 : 1);
        Grid.SetRow(HeaderActions, narrow ? 1 : 0);
        HeaderActions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        HeaderActions.Margin = narrow ? new Thickness(0, 6, 0, 0) : default;
    }
    public void Attach(AutomationControlViewModel model)
    {
        if (ReferenceEquals(_model, model)) return;
        Detach(); _model = model; DataContext = model; model.GraphChanged += RenderGraph;
        RenderGraph(); RenderDetailPanels(); ApplyResponsiveLayout(); if (this.IsAttachedToVisualTree()) _timer.Start();
    }
    public void Detach()
    {
        _timer.Stop();
        if (_model is { } model) { model.GraphChanged -= RenderGraph; model.Dispose(); }
        _model = null; DataContext = null;
    }
    /// <summary>线程/成果选择在视图模型里查得到就同步选中，查不到就明确说明，不伪造选中。</summary>
    private void RenderDetailPanels()
    {
        if (_model is not { } model || !model.Connected) return;
        model.RefreshSelectionPanels();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); if (_model is not null) _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _timer.Stop(); base.OnDetachedFromVisualTree(e); }
    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (_model is not { IsBusy: false } model || TopLevel.GetTopLevel(this) is not { } top) return;
        var context = model.ProjectContext;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = "连接已有工作台数据库", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("工作台数据库") { Patterns = ["*.db", "*.sqlite", "*.sqlite3"] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path && ReferenceEquals(_model, model))
            {
                if (context == model.ProjectContext) await model.ConnectAsync(path);
                else model.StatusText = "项目已切换，请重新选择工作台。";
            }
        }
        catch (Exception error) { if (ReferenceEquals(_model, model)) model.StatusText = "选择失败：" + error.Message; }
    }
    internal IReadOnlyDictionary<string, Point> TaskPorts => _taskPorts;
    internal IReadOnlyDictionary<string, Point> PartPorts => _partPorts;
    private readonly Dictionary<string, Point> _taskPorts = new();
    private readonly Dictionary<string, Point> _partPorts = new();
    /// <summary>
    /// v4 依赖图：以真实线程节点为单元，节点内保留任务与成果端口。
    /// 只画读到的对象；没有快照时明确说明，不画装饰性占位。
    /// </summary>
    private void RenderGraph()
    {
        GraphCanvas.Children.Clear(); _taskPorts.Clear(); _partPorts.Clear();
        var model = _model;
        var threads = model?.Snapshot?.Graph?.Threads.ToArray() ?? [];
        if (threads.Length == 0)
        {
            GraphCanvas.Width = 560; GraphCanvas.Height = 48;
            GraphCanvas.Children.Add(new TextBlock { Text = model?.Connected == true ? "没有已注册的执行线程；计划与待办见总览。" : "连接工作台后显示真实线程与依赖。", TextWrapping = TextWrapping.Wrap, Width = 540 });
            return;
        }
        var groupIndex = 0; double maxBottom = 0;
        foreach (var group in threads.GroupBy(t => (t.ProjectId, t.Harness)))
        {
            var x = 12 + groupIndex++ * 340; double y = 36;
            var label = new TextBlock { Text = group.Key.ProjectId + " · " + group.Key.Harness, Width = 260, TextTrimming = TextTrimming.CharacterEllipsis };
            Canvas.SetLeft(label, x); Canvas.SetTop(label, 8); GraphCanvas.Children.Add(label);
            foreach (var thread in group)
            {
                var stack = new StackPanel { Spacing = 4 };
                stack.Children.Add(SelectButton(thread.ThreadId, "thread:" + thread.ThreadId, 40, () => model!.SelectThread(thread.ThreadId)));
                stack.Children.Add(new TextBlock { Height = 14, FontSize = 11, Text = thread.Running ? "正在执行" : thread.Status == ThreadStatus.Frozen ? "需对账" : thread.Tasks.Count + " 个任务 · 当前未运行" });
                double cursor = y + 71; // One-pixel border + padding + header/caption and gaps.
                // v4 依赖图始终保留线程内部的任务与成果端口；列表页负责折叠次级线程。
                foreach (var task in thread.Tasks)
                {
                    _taskPorts[task.TaskId] = new(x + 130, cursor + 18);
                    stack.Children.Add(SelectButton(task.Title, "task:" + task.TaskId, 36, () => model!.SelectTask(task.TaskId))); cursor += 40;
                    foreach (var part in task.Parts)
                    {
                        _partPorts[part.PartId] = new(x + 130, cursor + 14);
                        var text = part.Title + " · " + (part.Revision == 0 ? "尚未交付" : part.Integrated ? "已集成" : part.Status switch
                        { DeliveryPartStatus.Accepted => "已接受", DeliveryPartStatus.ReworkRequested => "待返工", DeliveryPartStatus.Blocked => "阻塞", DeliveryPartStatus.Deferred => "暂缓", _ => "待审核" });
                        stack.Children.Add(SelectButton(text, "part:" + part.PartId, 28, () => model!.SelectPart(part.PartId))); cursor += 32;
                    }
                }
                var height = 78 + thread.Tasks.Sum(t => 40 + t.Parts.Count * 32);
                var border = new Border { Width = 260, Height = height, Padding = new Thickness(8), Child = stack, Tag = "node:" + thread.ThreadId };
                border.Classes.Add("automation-thread"); Canvas.SetLeft(border, x); Canvas.SetTop(border, y);
                GraphCanvas.Children.Add(border); y += height + 24;
            }
            maxBottom = Math.Max(maxBottom, y);
        }
        GraphCanvas.Width = 24 + (groupIndex - 1) * 340 + 260; GraphCanvas.Height = maxBottom;
        foreach (var edge in model!.Snapshot!.Graph!.Edges)
        {
            if (!_taskPorts.TryGetValue(edge.SourceTaskId, out var from) || !_taskPorts.TryGetValue(edge.TargetTaskId, out var to)) continue;
            if (edge.SourcePartId is { } part && !_partPorts.TryGetValue(part, out from)) continue;
            var start = new Point(from.X + 130, from.Y); var end = new Point(to.X - 130, to.Y);
            var geometry = new StreamGeometry(); using (var draw = geometry.Open())
            { draw.BeginFigure(start, false); draw.CubicBezierTo(new(start.X + 40, start.Y), new(end.X - 40, end.Y), end); draw.EndFigure(false); }
            var path = new Avalonia.Controls.Shapes.Path { Data = geometry, IsHitTestVisible = false, Tag = edge };
            path.Classes.Add("automation-edge"); GraphCanvas.Children.Insert(0, path);
        }
    }
    private static Button SelectButton(string text, string id, double height, Action select)
    {
        var button = new Button { Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis },
            HorizontalAlignment = HorizontalAlignment.Stretch, Height = height, Tag = id };
        button.Classes.Add("subtle"); AutomationProperties.SetAutomationId(button, id); ToolTip.SetTip(button, id);
        button.Click += (_, _) => select(); return button;
    }
}
