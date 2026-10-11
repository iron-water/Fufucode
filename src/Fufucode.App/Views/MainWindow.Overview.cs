using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App;

public partial class MainWindow
{
    internal Task SwitchSessionForOverviewTestingAsync(string id) => SwitchSessionAsync(id);
    private DesktopMetadataStore? _mockOrganizationStore;
    private bool _overviewRefreshPending;
    private SessionOrganization Organization() => new(_host?.Manager.Metadata ??
        (_mockOrganizationStore ??= new(new AppDataLayout(Path.Combine(Path.GetTempPath(), "Fufucode",
            "organization-demo-" + Guid.NewGuid().ToString("N"))))));

    internal void RenderOverview()
    {
        if (_vm is null) return;
        if (_overview.GetVisualDescendants().OfType<Control>().Any(c => c.ContextMenu?.IsOpen == true))
        { _overviewRefreshPending = true; return; }
        _overviewRefreshPending = false;
        var organization = Organization();
        var state = organization.State;
        _overview.Toolbar.Children.Clear();
        _overview.Body.Children.Clear();
        if (organization.RestoreError is { } diagnostic) OverviewEmpty(diagnostic);
        var toolbar = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 4 };
        var mode = new ToggleSwitch { OffContent = "全部", OnContent = "活动", IsChecked = state.ActivityView, FontSize = 12 };
        mode.IsCheckedChanged += (_, _) => OverviewRun(() =>
        { organization.SetActivity(mode.IsChecked == true); RenderOverview(); return Task.CompletedTask; });
        toolbar.Children.Add(mode);
        var add = OverviewIcon("Add", "新建分区", () => EditSectionAsync(null));
        Grid.SetColumn(add, 1); toolbar.Children.Add(add);
        var options = OverviewIcon("More", "会话视图选项", () => Task.CompletedTask);
        var priority = new MenuItem { Header = "显示" };
        foreach (var (id, name) in new[] { ("attention", "优先事项部分"), ("pinned", "置顶"), ("scheduled", "定时任务") })
        {
            var item = OverviewMenu(name, () => { organization.SetPriority(id); RenderOverview(); return Task.CompletedTask; });
            item.Icon = state.Priority == id ? new TextBlock { Text = "✓" } : null;
            priority.Items.Add(item);
        }
        var menu = OverviewContextMenu(); menu.Items.Add(priority);
        menu.Items.Add(OverviewMenu("全部标为已读", () =>
        {
            foreach (var session in OverviewSessions())
            {
                var presentation = SessionPresentation.Load(_host!.Manager.Metadata, session.Id);
                SessionPresentation.Save(_host.Manager.Metadata, session.Id, presentation with { Unread = false });
            }
            RefreshSessionList(); return Task.CompletedTask;
        }, _host is not null));
        menu.Items.Add(OverviewMenu("归档聊天", ShowArchivesAsync, _host is not null));
        options.ContextMenu = menu;
        options.Click += (_, _) => menu.Open(options);
        Grid.SetColumn(options, 2); toolbar.Children.Add(options);
        _overview.Toolbar.Children.Add(toolbar);
        var all = OverviewSessions().Where(s => s.Id == CurrentSessionId ||
            !SessionPresentation.Load(_host!.Manager.Metadata, s.Id).Archived).ToArray();
        if (state.ActivityView)
        {
            OverviewHeading("优先级");
            var scheduled = new WorkbenchScheduleStore(_customization.DataRoot).Tasks
                .Where(t => t.State == "pending").Select(t => t.SessionId).ToHashSet();
            var important = all.Where(s => state.Priority switch
            {
                "pinned" => SessionPresentation.Load(_host!.Manager.Metadata, s.Id).Pinned,
                "scheduled" => scheduled.Contains(s.Id),
                _ => s.WasActive || SessionPresentation.Load(_host!.Manager.Metadata, s.Id).Unread ||
                    (s.Id == CurrentSessionId && (_vm.Input.IsTurnActive ||
                        _vm.Sidebar.SelectedSession is { IsUnread: true } or { IsFailed: true }))
            }).ToArray();
            foreach (var session in important) OverviewSessionRow(session, organization);
            if (important.Length == 0) OverviewEmpty("暂无需要关注的任务");
            foreach (var day in all.GroupBy(s => s.LastOpened.LocalDateTime.Date).OrderByDescending(g => g.Key))
            {
                OverviewHeading(day.Key == DateTime.Today ? "今天" : day.Key == DateTime.Today.AddDays(-1) ? "昨天" : day.Key.ToString("MM-dd"));
                foreach (var session in day.OrderByDescending(s => s.LastOpened)) OverviewSessionRow(session, organization);
            }
            return;
        }
        OverviewHeading("置顶");
        var pinned = all.Where(s => SessionPresentation.Load(_host!.Manager.Metadata, s.Id).Pinned).ToArray();
        foreach (var session in pinned) OverviewSessionRow(session, organization);
        if (pinned.Length == 0) OverviewEmpty("无置顶会话");
        OverviewHeading("分区");
        foreach (var section in state.Sections)
        {
            var header = OverviewHeading(section.Name, 8);
            AutomationProperties.SetAutomationId(header, "section.row." + section.Id);
            header.ContextMenu = OverviewContextMenu();
            header.ContextMenu.Items.Add(OverviewMenu("重命名", () => EditSectionAsync(section)));
            foreach (var project in OverviewProjects().Where(p => organization.Project(p).SectionId == section.Id))
                OverviewProjectRow(project, organization, all);
            foreach (var session in all.Where(s => organization.Placement(s.Id).SectionId == section.Id &&
                organization.Placement(s.Id).ProjectId is null)) OverviewSessionRow(session, organization, 8);
        }
        if (state.Sections.Count == 0) OverviewEmpty("无分区");
        OverviewHeading("项目");
        foreach (var project in OverviewProjects().Where(p => organization.Project(p).SectionId is null))
            OverviewProjectRow(project, organization, all);
        OverviewHeading("会话");
        foreach (var session in all.OrderByDescending(s => s.LastOpened)) OverviewSessionRow(session, organization);
        if (_host is null)
            foreach (var demo in _vm.Sidebar.Sessions)
            {
                var label = OverviewHeading(demo.Title, 0); label.FontWeight = FontWeight.Normal;
                ToolTip.SetTip(label, demo.StatusDescription);
            }
    }

    private IReadOnlyList<ProjectRecord> OverviewProjects() => _host?.Manager.Metadata.ListProjects() ?? [];
    private IEnumerable<SessionRecord> OverviewSessions() => OverviewProjects()
        .SelectMany(project => _host!.Manager.Metadata.ListSessions(project.Id));
    private TextBlock OverviewHeading(string title, double indent = 0)
    {
        var text = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(indent + 4, 12, 4, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        _overview.Body.Children.Add(text); return text;
    }
    private void OverviewEmpty(string text) => _overview.Body.Children.Add(new TextBlock
    { Text = text, Classes = { "meta" }, Margin = new Thickness(4, 3, 4, 5), TextWrapping = TextWrapping.Wrap });
    private Button OverviewIcon(string icon, string title, Func<Task> action)
    {
        var button = new Button { Classes = { "iconButton" }, Width = 26, Height = 26, MinWidth = 26,
            MinHeight = 26, Padding = new Thickness(6), Content = new PathIcon
            { Width = 14, Height = 14, Data = this.FindResource("Icon." + icon) as Geometry } };
        ToolTip.SetTip(button, title); AutomationProperties.SetName(button, title);
        button.Click += (_, _) => OverviewRun(action); return button;
    }
    private MenuItem OverviewMenu(string title, Func<Task> action, bool enabled = true)
    {
        var item = new MenuItem { Header = title, IsEnabled = enabled };
        item.Click += (_, e) => { e.Handled = true; OverviewRun(action); }; return item;
    }
    private ContextMenu OverviewContextMenu()
    {
        var menu = new ContextMenu();
        menu.Closed += (_, _) =>
        {
            if (_overviewRefreshPending && !_closed)
                Dispatcher.UIThread.Post(RenderOverview, DispatcherPriority.Background);
        };
        return menu;
    }
    private async void OverviewRun(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { _vm?.Timeline.AddNotification("会话管理：" + error.GetBaseException().Message); }
    }
    private void OverviewProjectRow(ProjectRecord project, SessionOrganization organization, SessionRecord[] sessions)
    {
        var settings = organization.Project(project);
        var row = new Button { Content = new TextBlock { Text = settings.Name, TextTrimming = TextTrimming.CharacterEllipsis },
            Classes = { "subtle" }, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, FontSize = 12, Margin = new Thickness(4, 1) };
        ToolTip.SetTip(row, settings.Folder);
        AutomationProperties.SetAutomationId(row, "project.row." + project.Id);
        row.Click += (_, _) => OverviewRun(() => SwitchProjectAsync(settings.Folder));
        var sections = new MenuItem { Header = "分区" };
        sections.Items.Add(OverviewMenu("无分区", () =>
        { organization.EditProject(project, settings.Name, settings.Folder, null); RenderOverview(); return Task.CompletedTask; }));
        foreach (var section in organization.State.Sections)
            sections.Items.Add(OverviewMenu(section.Name, () =>
            { organization.EditProject(project, settings.Name, settings.Folder, section.Id); RenderOverview(); return Task.CompletedTask; }));
        var menu = OverviewContextMenu();
        menu.Items.Add(OverviewMenu("新建会话", async () =>
        {
            await SwitchProjectAsync(settings.Folder);
            if (_host is null || !RealSessionHost.PathEquals(_host.ProjectPath, settings.Folder))
                throw new InvalidOperationException("目标文件夹未打开，不会在原项目新建会话。");
            await NewChatAsync();
            new SessionOrganization(_host.Manager.Metadata).PlaceSession(CurrentSessionId!, project.Id, null);
            RenderOverview();
        }));
        menu.Items.Add(OverviewMenu("编辑项目", () => EditOverviewProjectAsync(project)));
        menu.Items.Add(sections);
        menu.Items.Add(OverviewMenu("在资源管理器中打开", () => { OpenExternal(settings.Folder); return Task.CompletedTask; }));
        row.ContextMenu = menu; _overview.Body.Children.Add(row);
        foreach (var session in sessions.Where(s => organization.Placement(s.Id).ProjectId == project.Id)
            .OrderByDescending(s => s.LastOpened)) OverviewSessionRow(session, organization, 12);
    }
    private void OverviewSessionRow(SessionRecord session, SessionOrganization organization, double indent = 0)
    {
        var presentation = SessionPresentation.Load(_host!.Manager.Metadata, session.Id);
        var row = new Grid { ColumnDefinitions = new("*,52"), ColumnSpacing = 2, Margin = new Thickness(indent, 0, 0, 0) };
        var title = session.Name ?? session.Id[..Math.Min(8, session.Id.Length)];
        var button = new Button { Classes = { "subtle" }, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 34,
            Content = new TextBlock { Text = title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = session.Id == CurrentSessionId ? FontWeight.SemiBold : FontWeight.Normal } };
        ToolTip.SetTip(button, _host.Manager.Metadata.GetProject(session.ProjectId).Path);
        AutomationProperties.SetAutomationId(button, "session.row." + session.Id);
        button.Click += (_, _) => OverviewRun(() => ActivateOverviewSessionAsync(session));
        row.Children.Add(button);
        var tail = new StackPanel { Orientation = Orientation.Horizontal, IsVisible = false };
        tail.Children.Add(OverviewIcon("Pin", presentation.Pinned ? "取消置顶" : "置顶", () =>
        { SetOverviewPresentation(session.Id, p => p with { Pinned = !p.Pinned }); return Task.CompletedTask; }));
        tail.Children.Add(OverviewIcon("Archive", "归档", () => ArchiveOverviewSessionAsync(session)));
        Grid.SetColumn(tail, 1); row.Children.Add(tail);
        row.PointerEntered += (_, _) => tail.IsVisible = true;
        row.PointerExited += (_, _) => { if (!row.IsKeyboardFocusWithin) tail.IsVisible = false; };
        row.GotFocus += (_, _) => tail.IsVisible = true;
        row.LostFocus += (_, _) => { if (!row.IsPointerOver) tail.IsVisible = false; };
        var menu = OverviewContextMenu();
        menu.Items.Add(OverviewMenu("重命名", async () => { await ActivateOverviewSessionAsync(session); await RenameManagedSessionAsync(); }));
        menu.Items.Add(OverviewMenu(presentation.Pinned ? "取消置顶" : "置顶", () =>
        { SetOverviewPresentation(session.Id, p => p with { Pinned = !p.Pinned }); return Task.CompletedTask; }));
        menu.Items.Add(OverviewMenu(presentation.Unread ? "标为已读" : "标为未读", () =>
        { SetOverviewPresentation(session.Id, p => p with { Unread = !p.Unread }); return Task.CompletedTask; }));
        var projects = new MenuItem { Header = "项目" };
        projects.Items.Add(OverviewMenu("无项目", () => { organization.PlaceSession(session.Id, null, null); RenderOverview(); return Task.CompletedTask; }));
        foreach (var project in OverviewProjects())
            projects.Items.Add(OverviewMenu(organization.Project(project).Name, () =>
            { organization.PlaceSession(session.Id, project.Id, null); RenderOverview(); return Task.CompletedTask; }));
        menu.Items.Add(projects);
        var sections = new MenuItem { Header = "分区" };
        sections.Items.Add(OverviewMenu("无分区", () => { organization.PlaceSession(session.Id, null, null); RenderOverview(); return Task.CompletedTask; }));
        foreach (var section in organization.State.Sections)
            sections.Items.Add(OverviewMenu(section.Name, () =>
            { organization.PlaceSession(session.Id, null, section.Id); RenderOverview(); return Task.CompletedTask; }));
        menu.Items.Add(sections);
        menu.Items.Add(new Separator());
        var forks = new MenuItem { Header = "分叉" };
        forks.Items.Add(OverviewMenu("创建聊天分支", async () => { await ActivateOverviewSessionAsync(session); await _chatPage.ChooseForkPointAsync(); }));
        forks.Items.Add(OverviewMenu("在新工作树中创建分支", async () => { await ActivateOverviewSessionAsync(session); await ForkIntoWorktreeAsync(); }, session.FilePath is not null));
        menu.Items.Add(forks);
        var copies = new MenuItem { Header = "复制" };
        copies.Items.Add(OverviewMenu("复制深度链接", () => CopyAsync(SessionDeepLink.Create(session.Id, _host.Manager.Metadata.GetProject(session.ProjectId).Path))));
        copies.Items.Add(OverviewMenu("复制为 Markdown", async () => { await ActivateOverviewSessionAsync(session); await CopyAsync(SessionMarkdown()); }));
        copies.Items.Add(OverviewMenu("复制工作目录", () => CopyAsync(_host.Manager.Metadata.GetProject(session.ProjectId).Path)));
        menu.Items.Add(copies);
        menu.Items.Add(new Separator());
        menu.Items.Add(OverviewMenu("在新窗口中打开", async () => { await ActivateOverviewSessionAsync(session); await MoveSessionToWindowAsync(); }));
        menu.Items.Add(OverviewMenu("打开方式", async () => { await ActivateOverviewSessionAsync(session); await ShowOpenWithAsync(); }));
        menu.Items.Add(OverviewMenu("归档", () => ArchiveOverviewSessionAsync(session)));
        button.ContextMenu = menu; _overview.Body.Children.Add(row);
    }
    private void SetOverviewPresentation(string id, Func<SessionPresentation, SessionPresentation> change)
    {
        var store = _host!.Manager.Metadata;
        SessionPresentation.Save(store, id, change(SessionPresentation.Load(store, id))); RefreshSessionList();
    }
    private async Task ActivateOverviewSessionAsync(SessionRecord session)
    {
        if (IdleReal() is { } reason) throw new InvalidOperationException(reason);
        if (_host!.Current.Project.Id != session.ProjectId)
            await SwitchProjectAsync(_host.Manager.Metadata.GetProject(session.ProjectId).Path);
        if (_host.Current.Project.Id != session.ProjectId) throw new InvalidOperationException("目标项目未打开。");
        if (CurrentSessionId != session.Id) await SwitchSessionAsync(session.Id);
        if (CurrentSessionId != session.Id) throw new InvalidOperationException("目标会话未打开。");
        var presentation = SessionPresentation.Load(_host.Manager.Metadata, session.Id);
        if (presentation.Unread) SetOverviewPresentation(session.Id, p => p with { Unread = false });
    }
    private async Task ArchiveOverviewSessionAsync(SessionRecord session)
    {
        if (session.Id == CurrentSessionId)
        {
            if (IdleReal() is { } reason) throw new InvalidOperationException(reason);
            await ArchiveCurrentAsync();
        }
        else SetOverviewPresentation(session.Id, p => p with { Archived = true });
        RenderOverview();
    }
    private async Task EditSectionAsync(SessionSection? section)
    {
        var name = await OverviewNameDialogAsync(section is null ? "新建分区" : "重命名分区", section?.Name ?? "");
        if (name is null) return;
        var organization = Organization();
        if (section is null) organization.AddSection(name); else organization.RenameSection(section.Id, name);
        RenderOverview();
    }
    private async Task<string?> OverviewNameDialogAsync(string title, string value)
    {
        var input = new TextBox { Text = value, MaxLength = 120 };
        var save = new Button { Content = "保存", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var dialog = new Window { Title = title, Width = 400, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { input,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, save } } } } };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.Close(input.Text.Trim()); };
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        return await dialog.ShowDialog<string?>(this);
    }
    private async Task EditOverviewProjectAsync(ProjectRecord project)
    {
        var organization = Organization();
        var settings = organization.Project(project);
        var name = new TextBox { Text = settings.Name, MaxLength = 120 };
        var folder = new TextBox { Text = settings.Folder };
        var pick = OverviewIcon("Folder", "选择文件夹", async () =>
        {
            var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "项目文件夹" });
            if (selected.Count > 0 && selected[0].TryGetLocalPath() is { } path) folder.Text = path;
        });
        var folderRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6, Children = { folder, pick } };
        Grid.SetColumn(pick, 1);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "保存", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var dialog = new Window { Title = "编辑项目", Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 10, Children = {
                new TextBlock { Text = "名称" }, name, new TextBlock { Text = "新会话文件夹" }, folderRow, error,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, save } } } } };
        save.Click += (_, _) =>
        {
            try { organization.EditProject(project, name.Text ?? "", folder.Text ?? "", settings.SectionId); dialog.Close(); RenderOverview(); }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }
}
