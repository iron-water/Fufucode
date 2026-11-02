using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Fufucode.App.Abstractions.Graph;

namespace Fufucode.App.Features.Graph;

/// <summary>Mailbox and correspondence, with explicit evidence and acceptance commands.</summary>
public sealed class GraphTicketsView : GraphContentView
{
    private readonly ScrollViewer _threadScroll = new()
    { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private string? _threadKey;
    private bool _restoring;
    private int _restoreGeneration;
    public GraphTicketsView(GraphWorkspaceViewModel vm) : base(vm)
    {
        AutomationProperties.SetAutomationId(this, "GraphTickets");
        var mail = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<GraphTicketRow>((row, _) => row == null ? null : MailRow(vm, row))
        };
        GraphUi.Bind(mail, ItemsControl.ItemsSourceProperty, "TicketRows");
        var mailbox = GraphUi.Scroll(mail);
        var moduleChoices = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<GraphModule>((module, _) =>
            {
                if (module == null) return null;
                var check = new CheckBox { Content = module.Name };
                GraphUi.Bind(check, ToggleButton.IsCheckedProperty, "TicketDraft.ModuleIds", source: vm,
                    converter: new FuncValueConverter<IReadOnlyCollection<string>, bool>(ids => ids?.Contains(module.Id) == true));
                check.Click += (_, _) => vm.TicketDraft.IncludeModule(module.Id, check.IsChecked == true);
                return check;
            })
        };
        GraphUi.Bind(moduleChoices, ItemsControl.ItemsSourceProperty, "Modules");
        var create = GraphUi.Stack(GraphUi.Text("新建工单", "bodyStrong"),
            GraphUi.Field("主题", GraphUi.Input("TicketDraft.Title", "GraphTicketTitle")),
            GraphUi.Field("目标", GraphUi.Input("TicketDraft.Goal", "GraphTicketGoal", true)),
            GraphUi.Field("验收条件", GraphUi.Input("TicketDraft.Acceptance", "GraphTicketAcceptance", true)),
            GraphUi.Field("关联模块", moduleChoices),
            GraphUi.Actions(GraphUi.Button("创建工单", () => vm.CreateTicket(), "GraphCreateTicket", true),
                GraphUi.Button("取消", vm.CancelTicketEdit, "GraphCancelTicket")));
        GraphUi.Bind(create, IsVisibleProperty, "IsTicketEditorOpen");
        GraphUi.Bind(create, IsEnabledProperty, "CanEdit");
        var linkedModules = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<GraphModuleOption>((module, _) => module?.Id == null ? null :
                GraphUi.Button(module.Name, () => vm.NavigateToModule(module.Id)))
        };
        GraphUi.Bind(linkedModules, ItemsControl.ItemsSourceProperty, "TicketModules");
        var entries = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<GraphEntry>((entry, _) => entry == null ? null : GraphUi.Entry(entry))
        };
        GraphUi.Bind(entries, ItemsControl.ItemsSourceProperty, "ThreadEntries");
        var runs = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<GraphRun>((run, _) => run == null ? null : GraphUi.Stack(
                GraphUi.Text(GraphLabels.Status(run.Status) + " · " + GraphLabels.Time(run.StartedUtc), "caption"),
                GraphUi.Text(run.Detail ?? "")))
        };
        GraphUi.Bind(runs, ItemsControl.ItemsSourceProperty, "SelectedTicketRuns");
        var send = GraphUi.Stack(
            GraphUi.Field("往来", GraphUi.Input("Correspondence.Text", "GraphReplyText", true)),
            GraphUi.Field("引用", GraphUi.Input("Correspondence.Reference", "GraphReplyReference")),
            GraphUi.Button("发送邮件", () => vm.SendEntry(), "GraphSendEntry", true));
        var criterion = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        GraphUi.Bind(criterion, ItemsControl.ItemsSourceProperty, "SelectedTicket.Acceptance");
        GraphUi.Bind(criterion, SelectingItemsControl.SelectedItemProperty, "Correspondence.EvidenceCriterion", BindingMode.TwoWay,
            converter: new GraphUi.SelectionIdConverter());
        var passed = new CheckBox { Content = "通过" };
        GraphUi.Bind(passed, ToggleButton.IsCheckedProperty, "Correspondence.EvidencePassed", BindingMode.TwoWay);
        var evidence = GraphUi.Stack(
            GraphUi.Field("验收项", criterion), GraphUi.Field("证据", GraphUi.Input("Correspondence.EvidenceText", "GraphEvidenceText", true)),
            GraphUi.Field("证据引用", GraphUi.Input("Correspondence.EvidenceReference", "GraphEvidenceReference")), passed,
            GraphUi.Button("添加证据", () => vm.AddEvidence(vm.Correspondence.EvidenceCriterion, vm.Correspondence.EvidenceText,
                vm.Correspondence.EvidencePassed, vm.Correspondence.EvidenceReference), "GraphAddEvidence", true));
        var checkedEffects = new CheckBox { Content = "已核查副作用" };
        AutomationProperties.SetAutomationId(checkedEffects, "GraphRecoveryChecked");
        GraphUi.Bind(checkedEffects, ToggleButton.IsCheckedProperty, "Correspondence.SideEffectsChecked", BindingMode.TwoWay);
        var retry = GraphUi.Button("允许重试", () => vm.ReconcileTicket(), "GraphAllowRetry", true);
        retry.Bind(IsEnabledProperty, new MultiBinding
        {
            Converter = BoolConverters.And,
            Bindings = { new Binding("CanEdit"), new Binding("Correspondence.SideEffectsChecked") }
        });
        var reconcile = GraphUi.Stack(checkedEffects,
            GraphUi.Field("副作用核查与恢复说明", GraphUi.Input("Correspondence.RecoveryResolution", "GraphRecoveryResolution", true)), retry);
        GraphUi.Bind(reconcile, IsVisibleProperty, "NeedsTicketReconciliation");
        var liftVeto = GraphUi.Stack(
            GraphUi.Field("解除理由", GraphUi.Input("Correspondence.LiftVetoReason", "GraphLiftVetoReason", true)),
            GraphUi.Button("解除否决", () => vm.LiftVeto(), "GraphLiftVeto", true));
        GraphUi.Bind(liftVeto, IsVisibleProperty, "HasActiveTicketVeto");
        var recovery = new Expander
        {
            Header = "人工恢复", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = GraphUi.Stack(liftVeto, reconcile)
        };
        AutomationProperties.SetAutomationId(recovery, "GraphRecovery");
        GraphUi.Bind(recovery, IsVisibleProperty, "HasTicketRecoveryActions");
        var decisions = GraphUi.Stack(
            GraphUi.Field("否决原因", GraphUi.Input("Correspondence.VetoReason", "GraphVetoReason", true)),
            GraphUi.Button("否决", () => vm.VetoTicket(), "GraphVetoTicket", true),
            GraphUi.Field("验收引用", GraphUi.Input("Correspondence.AcceptanceEvidence", "GraphAcceptanceEvidence")),
            GraphUi.Button("验收", () => vm.AcceptTicket(), "GraphAcceptTicket", true), recovery);
        var session = GraphUi.Stack(GraphUi.Field("会话", GraphUi.Input("Correspondence.SessionId", "GraphLinkSessionId")),
            GraphUi.Button("关联会话", () => vm.LinkSession(vm.Correspondence.SessionId), "GraphLinkSession", true));
        var editing = GraphUi.Stack(
            GraphUi.Actions(GraphUi.AsyncButton("运行", () => vm.RunTicketAsync(), "GraphRunTicket"),
                GraphUi.AsyncButton("停止", () => vm.StopTicketAsync(), "GraphStopTicket")),
            send, GraphUi.Divider(), evidence, GraphUi.Divider(), decisions, GraphUi.Divider(), session);
        GraphUi.Bind(editing, IsVisibleProperty, "!IsHistorical");
        var details = GraphUi.Stack(GraphUi.Value("SelectedTicketStatus", "caption"),
            GraphUi.Value("SelectedTicket.Goal"), GraphUi.Field("验收条件", GraphUi.Value("SelectedTicketAcceptance")),
            linkedModules, GraphUi.Value("SelectedTicket.SessionId", "caption"),
            GraphUi.Button("打开会话", () => vm.OpenChat(null, vm.SelectedTicketId), "GraphTicketChat"),
            GraphUi.Divider(), entries, runs, editing);
        GraphUi.Bind(details, IsVisibleProperty, "HasSelectedTicket");
        var thread = GraphUi.Stack(create, GraphUi.Value("TicketSelectionLabel", "bodyStrong"), details);
        thread.Margin = new Thickness(0, 10, 0, 8);
        _threadScroll.Content = thread;
        _threadScroll.ScrollChanged += (_, _) =>
        { if (!_restoring && _threadKey != null) vm.SaveReadingOffset(_threadKey, _threadScroll.Offset); };
        var commands = GraphUi.Actions(GraphUi.Button("新建", vm.BeginTicket, "GraphNewTicket", true),
            GraphUi.AsyncButton("启动推进", () => vm.SetAutonomyAsync(true), "GraphStartAutonomy"),
            GraphUi.AsyncButton("停止推进", () => vm.SetAutonomyAsync(false), "GraphStopAutonomy"),
            GraphUi.AsyncButton("停止全部", () => vm.StopAllAsync(), "GraphStopAll"));
        var top = GraphUi.Stack(GraphUi.Version(vm), GraphUi.Error(vm), GraphUi.Busy(), commands, GraphUi.Value("ProjectStatus", "caption"));
        top.Spacing = 4;
        var splitter = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        splitter.Classes.Add("dockSplitter");
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,170,4,*"), Margin = new Thickness(12, 8), MinHeight = 200 };
        root.Children.Add(top); Grid.SetRow(mailbox, 1); root.Children.Add(mailbox);
        Grid.SetRow(splitter, 2); root.Children.Add(splitter);
        Grid.SetRow(_threadScroll, 3); root.Children.Add(_threadScroll); Content = root;
    }
    private static Control MailRow(GraphWorkspaceViewModel vm, GraphTicketRow row)
    {
        var title = GraphUi.Text(row.Ticket.Title, "bodyStrong");
        title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        var preview = GraphUi.Text(row.Preview, "caption");
        preview.TextWrapping = TextWrapping.NoWrap; preview.TextTrimming = TextTrimming.CharacterEllipsis;
        var content = GraphUi.Stack(title, GraphUi.Text(row.Sender, "caption"), preview,
            GraphUi.Text(row.Status + " · " + row.Time, "caption"));
        content.Spacing = 3;
        var indicator = new Border { Width = 2, Margin = new Thickness(0, 0, 8, 0) };
        indicator.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppAccent"));
        GraphUi.Bind(indicator, IsVisibleProperty, "SelectedTicketId", source: vm,
            converter: new FuncValueConverter<string?, bool>(id => id == row.Ticket.Id));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 6) };
        grid.Children.Add(indicator); Grid.SetColumn(content, 1); grid.Children.Add(content);
        var button = GraphUi.Button(row.Ticket.Title, () => vm.SelectTicket(row.Ticket.Id), "GraphMail-" + row.Ticket.Id);
        button.Content = grid; button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        return button;
    }
    protected override void OnWorkspaceChanged(string? name)
    {
        var key = Workspace.Snapshot.ProjectId + "/" + Workspace.VersionLabel + "/" + Workspace.SelectedTicketId;
        if (key == _threadKey) return;
        if (_threadKey != null && !_restoring) Workspace.SaveReadingOffset(_threadKey, _threadScroll.Offset);
        _threadKey = key; _restoring = true;
        var generation = ++_restoreGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _restoreGeneration) return;
            _threadScroll.Offset = Workspace.ReadReadingOffset(key);
            _restoring = false;
        }, DispatcherPriority.Loaded);
    }
    public override void Dispose()
    {
        if (_threadKey != null && !_restoring) Workspace.SaveReadingOffset(_threadKey, _threadScroll.Offset);
        ++_restoreGeneration; base.Dispose();
    }
}
