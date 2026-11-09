using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Abstractions.Graph;

namespace Fufucode.App.Features.Graph;

/// <summary>Human correspondence. Opening mail changes presentation and selection only.</summary>
public sealed class GraphTicketsView : GraphContentView
{
    // Presentation belongs to this workspace, including when its dock content is recreated.
    private static readonly ConditionalWeakTable<GraphWorkspaceViewModel, MailState> States = new();
    private sealed class MailState
    {
        public readonly Dictionary<string, string> Drafts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, bool> OpenDetails = new(StringComparer.Ordinal);
        public GridLength ListWidth = new(2, GridUnitType.Star);
        public GridLength DetailWidth = new(3, GridUnitType.Star);
    }

    private readonly MailState _state;
    private readonly ListBox _inbox;
    private readonly ScrollViewer _detailScroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly Grid _panes = new();
    private readonly Grid _listPane = new();
    private readonly Grid _detailPane = new();
    private readonly GridSplitter _splitter;
    private readonly Button _back;
    private readonly TextBlock _emptyList = GraphUi.Text("暂无邮件", "caption");
    private readonly TextBlock _emptyDetail = GraphUi.Text("选择邮件", "caption");
    private readonly StackPanel _message;
    private readonly TextBox _draft;
    private ScrollViewer? _listScroll;
    private string? _contextKey, _messageKey;
    private bool _wide, _detailOpen, _synchronizing, _restoring, _attached;
    private int _restoreGeneration;

    public GraphTicketsView(GraphWorkspaceViewModel vm) : base(vm)
    {
        _state = States.GetValue(vm, _ => new MailState());
        AutomationProperties.SetAutomationId(this, "GraphTickets");
        _inbox = new ListBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            AutoScrollToSelectedItem = false,
            ItemsSource = vm.HumanInbox,
            ItemTemplate = new FuncDataTemplate<GraphOrganizationMessage>((mail, _) => mail == null ? null : MailRow(mail))
        };
        _inbox.Classes.Add("graphMailInbox");
        ScrollViewer.SetHorizontalScrollBarVisibility(_inbox, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_inbox, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(_inbox, "GraphHumanInbox");
        AutomationProperties.SetName(_inbox, "邮件");
        _inbox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _inbox.SelectedItem is GraphOrganizationMessage mail)
            { OpenHumanMessage(mail.Id); e.Handled = true; }
        };
        _emptyList.Margin = new Thickness(16);
        _listPane.Children.Add(_inbox);
        _listPane.Children.Add(_emptyList);

        // Drafts are deliberately not bound to the VM's single reply slot. Selection and
        // snapshot refresh may reset that slot; each message keeps its own presentation draft.
        _draft = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96,
            MaxHeight = 240, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _draft.Classes.Add("dialogInput");
        AutomationProperties.SetAutomationId(_draft, "GraphOrganizationReply");
        AutomationProperties.SetName(_draft, "回复");
        _draft.TextChanged += (_, _) =>
        {
            if (!_synchronizing && _messageKey != null) _state.Drafts[_messageKey] = _draft.Text ?? "";
        };
        var send = GraphUi.Button("发送回复", SendReply, "GraphSendOrganizationReply", true);
        var reply = GraphUi.Stack(GraphUi.Field("回复", _draft), GraphUi.Actions(send));
        GraphUi.Bind(reply, IsEnabledProperty, "CanEdit");
        var subject = GraphUi.Text("", "bodyStrong");
        GraphUi.Bind(subject, TextBlock.TextProperty, "SelectedHumanMessage.Content.Text",
            converter: new FuncValueConverter<string?, string>(Subject));
        var kind = GraphUi.Text("", "caption");
        GraphUi.Bind(kind, TextBlock.TextProperty, "SelectedHumanMessage.Content.Kind",
            converter: new FuncValueConverter<string?, string>(MessageKind));
        var status = GraphUi.Text("", "caption");
        GraphUi.Bind(status, TextBlock.TextProperty, "SelectedHumanMessage.Status",
            converter: new FuncValueConverter<GraphMessageStatus, string>(value => MessageStatus(value)));
        var senderName = GraphUi.Value("SelectedHumanMessage.SenderId");
        GraphUi.Bind(senderName, TextBlock.TextProperty, "SelectedHumanMessage.SenderId",
            converter: new FuncValueConverter<string?, string>(value => GraphLabels.Actor(value ?? "")));
        _message = GraphUi.Stack(subject,
            senderName, kind, status,
            GraphUi.Divider(), GraphUi.Value("SelectedHumanMessage.Content.Text"),
            GraphUi.Value("SelectedHumanMessage.Resolution", "caption"),
            GraphUi.Divider(), reply);
        _message.Spacing = 12;
        _message.Margin = new Thickness(16);
        _message.MaxWidth = 960;
        _message.HorizontalAlignment = HorizontalAlignment.Stretch;
        var reading = new Grid();
        reading.Children.Add(_message);
        _emptyDetail.Margin = new Thickness(16);
        reading.Children.Add(_emptyDetail);
        _detailScroll.Content = reading;
        AutomationProperties.SetAutomationId(_detailScroll, "GraphMailDetailScroll");
        _detailScroll.ScrollChanged += (_, _) => SaveDetailOffset();
        _back = GraphUi.Button("返回", BackToList, "GraphMailBack");
        var navigation = GraphUi.Actions(_back);
        navigation.Margin = new Thickness(8, 4, 8, 0);
        _detailPane.Bind(Panel.BackgroundProperty, new DynamicResourceExtension("AppCardBackground"));
        _detailPane.RowDefinitions = new RowDefinitions("Auto,*");
        _detailPane.Children.Add(navigation);
        Grid.SetRow(_detailScroll, 1);
        _detailPane.Children.Add(_detailScroll);
        _splitter = new GridSplitter
        {
            Width = 8, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };
        _splitter.Classes.Add("dockSplitter");
        AutomationProperties.SetAutomationId(_splitter, "GraphMailSplitter");
        AutomationProperties.SetName(_splitter, "调整邮件列表宽度");
        _panes.Children.Add(_listPane);
        _panes.Children.Add(_splitter);
        _panes.Children.Add(_detailPane);
        var top = GraphUi.Stack(GraphUi.Version(vm), GraphUi.Error(vm), GraphUi.Busy());
        top.Spacing = 4;
        top.Margin = new Thickness(12, 0);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(top);
        Grid.SetRow(_panes, 1);
        root.Children.Add(_panes);
        Content = root;
        SetPaneLayout(false);
        SizeChanged += (_, _) =>
        {
            var wide = Bounds.Width >= 720;
            if (wide == _wide) return;
            SaveOffsets();
            SetPaneLayout(wide);
            RestoreOffsets();
        };
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            vm.HumanInbox.CollectionChanged += InboxChanged;
            Synchronize();
            RestoreOffsets();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            SavePresentation();
            vm.HumanInbox.CollectionChanged -= InboxChanged;
            _attached = false;
            ++_restoreGeneration;
            _restoring = false;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_wide && _detailOpen) { BackToList(); e.Handled = true; }
        };
    }

    private Control MailRow(GraphOrganizationMessage mail)
    {
        var sender = SingleLine(GraphLabels.Actor(mail.SenderId), "bodyStrong");
        var subject = SingleLine(Subject(mail.Content.Text), "body");
        var firstLineEnd = mail.Content.Text.TrimStart().IndexOfAny(['\r', '\n']);
        var remainder = firstLineEnd < 0 ? "" : mail.Content.Text.TrimStart()[(firstLineEnd + 1)..].Trim();
        var preview = SingleLine(remainder.Replace('\r', ' ').Replace('\n', ' '), "caption");
        preview.IsVisible = remainder.Length != 0;
        var metadata = SingleLine(MessageKind(mail.Content.Kind) + " · " + MessageStatus(mail.Status), "caption");
        var text = GraphUi.Stack(sender, subject, preview, metadata);
        text.Spacing = 4;
        var button = GraphUi.Button(mail.SenderId, () => OpenHumanMessage(mail.Id), "GraphInbox-" + mail.Id);
        button.Content = text;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(12);
        AutomationProperties.SetName(button, mail.SenderId + " · " + Subject(mail.Content.Text));
        return button;
    }

    private static string Subject(string? text)
    {
        var line = (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    private static string MessageKind(string? kind) => kind switch
    {
        "question" => "提问", "report" => "报告", "reply" => "回复",
        "notification" or "notify" => "通知", "request" => "请求",
        null => "", _ => GraphLabels.EntryKind(kind)
    };

    private static string MessageStatus(GraphMessageStatus status) =>
        status == GraphMessageStatus.Rejected ? "已拒绝" : GraphLabels.Status(status.ToString());

    private static TextBlock SingleLine(string text, string style)
    {
        var block = GraphUi.Text(text, style);
        block.TextWrapping = TextWrapping.NoWrap;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        return block;
    }

    internal void OpenHumanMessage(string id)
    {
        if (!Workspace.HumanInbox.Any(mail => mail.Id == id)) return;
        SavePresentation();
        _restoring = true;
        _synchronizing = true;
        try { Workspace.SelectHumanMessage(id); }
        finally { _synchronizing = false; }
        Synchronize();
        _detailOpen = true;
        if (_contextKey != null) _state.OpenDetails[_contextKey] = true;
        UpdateVisibility();
        RestoreOffsets();
    }

    internal void BackToList()
    {
        SavePresentation();
        _detailOpen = false;
        if (_contextKey != null) _state.OpenDetails[_contextKey] = false;
        UpdateVisibility();
        RestoreOffsets();
        Dispatcher.UIThread.Post(() => { if (_attached && !_wide) _inbox.Focus(); }, DispatcherPriority.Loaded);
    }

    private void SendReply()
    {
        if (!Workspace.CanEdit || Workspace.IsHistorical || Workspace.SelectedHumanMessage == null) return;
        SavePresentation();
        var key = _messageKey;
        Workspace.OrganizationReply = _draft.Text ?? "";
        if (!Workspace.ReplyToHumanMessage()) return;
        if (key != null) _state.Drafts[key] = "";
        if (key == _messageKey) _draft.Text = "";
    }

    private void InboxChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reconciliation can replace the selected record. Wait until the VM has finished
        // publishing the snapshot before restoring its selection and offsets.
        Dispatcher.UIThread.Post(() => { if (_attached) Synchronize(); }, DispatcherPriority.Loaded);
    }

    protected override void OnWorkspaceChanged(string? name)
    {
        if (!_synchronizing) Synchronize();
    }

    private void Synchronize()
    {
        var context = Workspace.Snapshot.ProjectId + "/" + Workspace.VersionLabel + "/human-mail";
        var key = Workspace.SelectedHumanMessage is { } mail ? context + "/" + mail.Id : null;
        var changed = context != _contextKey || key != _messageKey;
        if (changed)
        {
            SavePresentation();
            if (context != _contextKey)
                _detailOpen = _state.OpenDetails.TryGetValue(context, out var open) && open;
            _contextKey = context;
            _messageKey = key;
            _synchronizing = true;
            try { _draft.Text = key != null && _state.Drafts.TryGetValue(key, out var draft) ? draft : ""; }
            finally { _synchronizing = false; }
        }
        _synchronizing = true;
        try { _inbox.SelectedItem = Workspace.SelectedHumanMessage; }
        finally { _synchronizing = false; }
        UpdateVisibility();
        if (changed) RestoreOffsets();
    }

    private void SetPaneLayout(bool wide)
    {
        if (_wide)
        {
            _state.ListWidth = _panes.ColumnDefinitions[0].Width;
            _state.DetailWidth = _panes.ColumnDefinitions[2].Width;
        }
        _wide = wide;
        _panes.ColumnDefinitions = wide ? new ColumnDefinitions
        {
            new ColumnDefinition { Width = _state.ListWidth, MinWidth = 260 },
            new ColumnDefinition(GridLength.Auto),
            new ColumnDefinition { Width = _state.DetailWidth, MinWidth = 320 }
        } : new ColumnDefinitions("*");
        Grid.SetColumn(_splitter, wide ? 1 : 0);
        Grid.SetColumn(_detailPane, wide ? 2 : 0);
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var hasMessage = Workspace.SelectedHumanMessage != null;
        _listPane.IsVisible = _wide || !_detailOpen || !hasMessage;
        _detailPane.IsVisible = _wide || (_detailOpen && hasMessage);
        _splitter.IsVisible = _wide;
        _back.IsVisible = !_wide;
        _inbox.IsVisible = Workspace.HumanInbox.Count != 0;
        _emptyList.IsVisible = Workspace.HumanInbox.Count == 0;
        _message.IsVisible = hasMessage;
        _emptyDetail.IsVisible = !hasMessage;
    }

    private void SaveDetailOffset()
    {
        if (!_restoring && _messageKey != null && _detailPane.IsVisible)
            Workspace.SaveReadingOffset(_messageKey, _detailScroll.Offset);
    }

    private void ListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!_restoring && _contextKey != null && _listPane.IsVisible && _listScroll != null)
            Workspace.SaveReadingOffset(_contextKey + "/list", _listScroll.Offset);
    }

    private void SaveOffsets()
    {
        SaveDetailOffset();
        if (!_restoring && _contextKey != null && _listPane.IsVisible && _listScroll != null)
            Workspace.SaveReadingOffset(_contextKey + "/list", _listScroll.Offset);
    }

    private void SavePresentation()
    {
        if (_wide)
        {
            _state.ListWidth = _panes.ColumnDefinitions[0].Width;
            _state.DetailWidth = _panes.ColumnDefinitions[2].Width;
        }
        SaveOffsets();
        if (_messageKey != null) _state.Drafts[_messageKey] = _draft.Text ?? "";
        if (_contextKey != null) _state.OpenDetails[_contextKey] = _detailOpen;
    }

    private void RestoreOffsets()
    {
        _restoring = true;
        var generation = ++_restoreGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _restoreGeneration) return;
            var scroll = _inbox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll != _listScroll)
            {
                if (_listScroll != null) _listScroll.ScrollChanged -= ListScrollChanged;
                _listScroll = scroll;
                if (_listScroll != null)
                {
                    _listScroll.ScrollChanged += ListScrollChanged;
                    AutomationProperties.SetAutomationId(_listScroll, "GraphMailListScroll");
                }
            }
            if (_contextKey != null && _listScroll != null && _listPane.IsVisible)
                _listScroll.Offset = Workspace.ReadReadingOffset(_contextKey + "/list");
            if (_messageKey != null && _detailPane.IsVisible)
                _detailScroll.Offset = Workspace.ReadReadingOffset(_messageKey);
            _restoring = false;
        }, DispatcherPriority.Loaded);
    }

    internal bool IsWideForTesting => _wide;
    internal bool IsDetailOpenForTesting => _detailOpen;
    internal bool IsListVisibleForTesting => _listPane.IsVisible;
    internal bool IsDetailVisibleForTesting => _detailPane.IsVisible;
    internal ListBox InboxForTesting => _inbox;
    internal ScrollViewer? ListScrollForTesting => _listScroll;
    internal ScrollViewer DetailScrollForTesting => _detailScroll;
    internal TextBox DraftForTesting => _draft;
    internal string? SelectedMessageIdForTesting => Workspace.SelectedHumanMessage?.Id;

    public override void Dispose()
    {
        SavePresentation();
        Workspace.HumanInbox.CollectionChanged -= InboxChanged;
        if (_listScroll != null) _listScroll.ScrollChanged -= ListScrollChanged;
        ++_restoreGeneration;
        _attached = false;
        base.Dispose();
    }
}
