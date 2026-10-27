using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Fufucode.App.Abstractions;
using Fufucode.Core.Diagnostics.Perf;
using Fufucode.Core.Plugins;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>
/// FUFUCODE ↔ FUFU ↔ JIII 协同网络通路性能监测看板：
/// 涵盖宿主协同核心、FUFU 桌宠 IPC 管道、JIII 远程控制 Web 通路的实时状态、RTT 延迟、事件流与审计。
/// </summary>
internal sealed class PerfCompanionNetworkView : UserControl
{
    private readonly StackPanel _root = new() { Spacing = 14 };

    // 拓扑节点指标
    private readonly TextBlock _hostEventsText = new() { FontSize = 22, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _hostStateText = new() { FontSize = 12, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _fufuStateText = new() { FontSize = 12, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _fufuMetricsText = new() { FontSize = 10.5 };
    private readonly TextBlock _fufuRenderText = new() { FontSize = 10.5 };
    private readonly TextBlock _jiiiStateText = new() { FontSize = 12, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _jiiiMetricsText = new() { FontSize = 10.5 };

    // KPI
    private readonly TextBlock _pipeRttText = new() { FontSize = 24, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _throughputText = new() { FontSize = 24, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _idempotencyLatencyText = new() { FontSize = 24, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _lossRateText = new() { FontSize = 24, FontWeight = FontWeight.SemiBold };

    // 审计流水
    private readonly StackPanel _auditRowsHost = new() { Spacing = 6 };

    public PerfCompanionNetworkView()
    {
        _hostEventsText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _fufuMetricsText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        _fufuRenderText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));
        _jiiiMetricsText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextDim"));

        _pipeRttText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _throughputText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _idempotencyLatencyText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));
        _lossRateText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextPrimary"));

        // 标题与协议版本
        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 2, 0, 2) };
        var title = Text("协同网络通路", 18, "AppTextPrimary", FontWeight.SemiBold);
        titleRow.Children.Add(title);

        var versionBadge = new Border
        {
            Padding = new Thickness(8, 2),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text("FUFUCODE ↔ FUFU ↔ JIII 规范总线", 11, "AppTextDim"),
        };
        versionBadge.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));
        Grid.SetColumn(versionBadge, 2);
        titleRow.Children.Add(versionBadge);

        // 三节点拓扑卡片
        var topologyGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };

        // 1. FUFUCODE 宿主核心
        var hostCard = MakeNodeCard("FUFUCODE 核心", "Canonical Bus", _hostStateText, "读取中", PerfPalette.Tool,
        [
            ("事件计数", (Control)_hostEventsText),
            ("路由职责", (Control)Text("去重 / 幂等 / 扇出", 11, "AppTextSecondary")),
            ("总线时延", (Control)Text("未采集", 11, "AppTextDim")),
        ]);
        topologyGrid.Children.Add(hostCard);

        // 2. FUFU 桌宠连接
        var fufuCard = MakeNodeCard("FUFU 桌宠", "service.fufu-bridge", _fufuStateText, "读取中", PerfPalette.Model,
        [
            ("IPC 管道", (Control)Text(@"\\.\pipe\fufu", 10.5, "AppTextSecondary")),
            ("传输状态", (Control)_fufuMetricsText),
            ("渲染帧率", (Control)_fufuRenderText),
        ]);
        Grid.SetColumn(fufuCard, 1);
        topologyGrid.Children.Add(fufuCard);

        // 3. JIII 远程协同
        var jiiiCard = MakeNodeCard("JIII 远程协同", "service.remote-control", _jiiiStateText, "读取中", PerfPalette.Approval,
        [
            ("传输通道", (Control)Text("WebSocket / Web 桥", 10.5, "AppTextSecondary")),
            ("远控状态", (Control)_jiiiMetricsText),
            ("意图审计", (Control)Text("Notice 仅入站", 11, "AppTextDim")),
        ]);
        Grid.SetColumn(jiiiCard, 2);
        topologyGrid.Children.Add(jiiiCard);

        // 性能指标网格
        var kpiGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 10 };
        kpiGrid.Children.Add(KpiBlock("协同事件总数", _throughputText, "规范事实与通知累计"));
        var kpi2 = KpiBlock("活跃连接器", _pipeRttText, "已连接 / 已注册");
        Grid.SetColumn(kpi2, 1);
        kpiGrid.Children.Add(kpi2);
        var kpi3 = KpiBlock("最近审计", _idempotencyLatencyText, "去重后的意图流水");
        Grid.SetColumn(kpi3, 2);
        kpiGrid.Children.Add(kpi3);
        var kpi4 = KpiBlock("待接入指标", _lossRateText, "RTT / 幂等时延 / 丢包需对端上报");
        Grid.SetColumn(kpi4, 3);
        kpiGrid.Children.Add(kpi4);

        // 实时规范事件与审计流水
        var auditHead = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        auditHead.Children.Add(Text("实时规范事件与意图流水", 13, "AppTextPrimary", FontWeight.SemiBold));
        var auditHint = Text("最近 30 条去重审计", 10, "AppTextDim");
        Grid.SetColumn(auditHint, 2);
        auditHead.Children.Add(auditHint);

        var auditCard = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(6),
            Child = new StackPanel { Spacing = 6, Children = { auditHead, _auditRowsHost } },
        };
        BindCard(auditCard);

        // 预留的未来插槽（挖坑）
        var slotCard = MakeSlotCard();

        _root.Children.Add(titleRow);
        _root.Children.Add(topologyGrid);
        _root.Children.Add(kpiGrid);
        _root.Children.Add(auditCard);
        _root.Children.Add(slotCard);

        Content = new ScrollViewer
        {
            Content = _root,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Padding = new Thickness(16, 12, 16, 16),
        };

        UpdateData();
    }

    public void UpdateData()
    {
        try
        {
            var core = PluginPackageManager.CompanionCore;
            var statuses = core.Status;
            var audits = core.RecentAudit(30);

            var fufuStatus = statuses.FirstOrDefault(s => string.Equals(s.ConnectorId, "service.fufu-bridge", StringComparison.OrdinalIgnoreCase));
            var jiiiStatus = statuses.FirstOrDefault(s => string.Equals(s.ConnectorId, "service.remote-control", StringComparison.OrdinalIgnoreCase));

            var totalEvents = statuses.Sum(s => s.ReceivedEvents + s.SentEvents);
            var running = statuses.Count(s => s.State == CompanionConnectorState.Running);
            _hostEventsText.Text = $"{totalEvents} 条";
            _hostStateText.Text = statuses.Count == 0
                ? "无连接器"
                : running > 0 ? $"{running} 个运行中" : "全部离线";

            if (fufuStatus is not null)
            {
                _fufuStateText.Text = StateText(fufuStatus.State);
                _fufuMetricsText.Text = $"收 {fufuStatus.ReceivedEvents} · 发 {fufuStatus.SentEvents}";
            }
            else
            {
                _fufuStateText.Text = "未安装 / 离线";
                _fufuMetricsText.Text = "管道尚未连接";
            }

            // 渲染探针运行在 FUFU 进程内；本进程拿不到样本时明确显示未采集，不拿默认值冒充。
            _fufuRenderText.Text = FufuRenderDiagnostics.HasSamples &&
                                   FufuRenderDiagnostics.IsFresh(TimeSpan.FromSeconds(5))
                ? $"渲染 {FufuRenderDiagnostics.CurrentFps:0.0} FPS · 单帧 {FufuRenderDiagnostics.LastFrameTimeMs:0.1}ms · 掉帧 {FufuRenderDiagnostics.DroppedFrames}"
                : "渲染帧率未采集";

            if (jiiiStatus is not null)
            {
                _jiiiStateText.Text = StateText(jiiiStatus.State);
                _jiiiMetricsText.Text = $"收 {jiiiStatus.ReceivedEvents} · 发 {jiiiStatus.SentEvents}";
            }
            else
            {
                _jiiiStateText.Text = "未连接";
                _jiiiMetricsText.Text = "远控等待配对";
            }

            // 只有连接状态、事件计数与审计流水是真实来源；时延/幂等/丢包尚无对端上报。
            _pipeRttText.Text = $"{running}/{statuses.Count}";
            _throughputText.Text = $"{totalEvents} evt";
            _idempotencyLatencyText.Text = $"{audits.Count} 条";
            _lossRateText.Text = "未接入";

            // 渲染审计记录
            _auditRowsHost.Children.Clear();
            if (audits.Count == 0)
            {
                _auditRowsHost.Children.Add(Text("尚无协同事件流转 · FUFU 或 JIII 产生动作后自动接入流水记录", 11, "AppTextDim"));
            }
            else
            {
                foreach (var entry in audits.Take(12))
                {
                    _auditRowsHost.Children.Add(BuildAuditRow(entry));
                }
            }
        }
        catch (Exception ex)
        {
            _hostEventsText.Text = "监测异常";
            System.Diagnostics.Trace.TraceWarning("Companion network view update failed: {0}", ex.Message);
        }
    }

    private static string StateText(CompanionConnectorState state) => state switch
    {
        CompanionConnectorState.Running => "运行中",
        CompanionConnectorState.Starting => "连接中",
        CompanionConnectorState.Degraded => "降级",
        CompanionConnectorState.Faulted => "故障",
        _ => "已停止",
    };

    private static Control BuildAuditRow(CanonicalAuditEntry entry)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("68,110,1*,Auto"),
            ColumnSpacing = 8,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var timeText = Text(entry.At.ToLocalTime().ToString("HH:mm:ss"), 10, "AppTextDim");
        row.Children.Add(timeText);

        var actionText = Text(entry.Kind, 10, "AppTextPrimary", FontWeight.SemiBold);
        Grid.SetColumn(actionText, 1);
        row.Children.Add(actionText);

        var targetText = Text($"{entry.ConnectorId}{(entry.Detail is null ? "" : " · " + entry.Detail)}", 10.5, "AppTextSecondary");
        Grid.SetColumn(targetText, 2);
        row.Children.Add(targetText);

        var statusChip = new Border
        {
            Padding = new Thickness(6, 1.5),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(entry.ReferenceId ?? "—", 9, "AppTextDim"),
        };
        statusChip.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppSubtleBackground"));
        Grid.SetColumn(statusChip, 3);
        row.Children.Add(statusChip);

        return row;
    }

    private static Border MakeNodeCard(string title, string subtitle, TextBlock statusText, string initialStatus,
        Color tone, (string Label, Control Value)[] rows)
    {
        var body = new StackPanel { Spacing = 8 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titleStack = new StackPanel { Spacing = 2, Children = { Text(title, 13, "AppTextPrimary", FontWeight.SemiBold), Text(subtitle, 9.5, "AppTextDim") } };
        head.Children.Add(titleStack);

        var dot = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(tone), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
        statusText.Text = initialStatus;
        statusText.FontSize = 10;
        statusText.VerticalAlignment = VerticalAlignment.Center;
        statusText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextSecondary"));
        var badge = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { dot, statusText } };
        Grid.SetColumn(badge, 1);
        head.Children.Add(badge);
        body.Children.Add(head);

        var sep = new Border { Height = 1, Margin = new Thickness(0, 2, 0, 2) };
        sep.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppDividerSubtle"));
        body.Children.Add(sep);

        foreach (var (lbl, val) in rows)
        {
            var r = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
            r.Children.Add(Text(lbl, 10.5, "AppTextDim"));
            val.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(val, 1);
            r.Children.Add(val);
            body.Children.Add(r);
        }

        var card = new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(6),
            Child = body,
        };
        BindCard(card);
        return card;
    }

    private static Border KpiBlock(string label, TextBlock valueText, string sub)
    {
        var body = new StackPanel { Spacing = 3 };
        body.Children.Add(Text(label, 11, "AppTextDim"));
        body.Children.Add(valueText);
        body.Children.Add(Text(sub, 10, "AppTextDim"));

        var card = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(6),
            Child = body,
        };
        BindCard(card);
        return card;
    }

    private static Border MakeSlotCard()
    {
        var body = new StackPanel { Spacing = 6 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = {
            new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(PerfPalette.Approval), VerticalAlignment = VerticalAlignment.Center },
            Text("FUFUCODE--FUFU--JIII 通路指标扩展插槽（已预留）", 12, "AppTextPrimary", FontWeight.SemiBold)
        }};
        body.Children.Add(titleRow);
        body.Children.Add(Text("1. [JIII 远程] 双向音频流、桌面视频流推流帧延迟与网络抖动监测插槽\n2. [FUFU 桌宠] Live2D 姿态动力学物理计算帧同步吞吐监测插槽\n3. [跨端协同] 跨进程/跨设备意图双向确认 RTT 往返时延插槽",
            11, "AppTextDim", null, true));

        var card = new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(6),
            Child = body,
            BorderThickness = new Thickness(1),
        };
        card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppCardBackground"));
        card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppDividerSubtle"));
        return card;
    }

    private static void BindCard(Border card)
    {
        card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppCardBackground"));
        card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AppBorderBrush"));
        card.BorderThickness = new Thickness(1);
    }

    private static TextBlock Text(string text, double size, string brush, FontWeight? weight = null, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
        if (weight is { } val) block.FontWeight = val;
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(brush));
        return block;
    }
}
