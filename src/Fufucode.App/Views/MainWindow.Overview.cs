using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core;

namespace Fufucode.App;

public partial class MainWindow
{
    internal Task SwitchSessionForOverviewTestingAsync(string id) => SwitchSessionAsync(id);
    private DesktopMetadataStore? _mockOrganizationStore;
    private bool _overviewRefreshPending;
    // 会话管理的折叠状态：只影响展示，不写盘；项目/分区分开记忆。
    private readonly HashSet<string> _collapsedProjects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedSections = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedProjects = new(StringComparer.Ordinal);
    private bool _allProjectsCollapsed;
    // 项目内默认先显示最近活动的几条会话，其余折叠起来。
    private const int ProjectSessionPreview = 4;

    /// <summary>可固定到会话行右侧的快捷操作（最多 4 个）。</summary>
    private static readonly (string Id, string Label, string Icon)[] SessionQuickActions =
    [
        ("pin", "置顶", "Pin"),
        ("archive", "归档", "Archive"),
        ("rename", "重命名", "Edit"),
        ("unread", "标为未读", "Chat"),
        ("fork", "分叉", "Branch"),
        ("copy", "复制", "Copy"),
        ("openWindow", "新窗口中打开", "WindowNew"),
        ("openWith", "打开方式", "Open"),
        ("share", "分享", "Share"),
        ("delete", "永久删除", "DismissCircle"),
    ];

    private static readonly string[] DefaultQuickActions = ["pin", "archive"];
    private const int MaxQuickActions = 4;

    private static List<string> QuickActionsOf(SessionPresentation presentation)
    {
        IEnumerable<string> source = presentation.RowActions ?? (IEnumerable<string>)DefaultQuickActions;
        return source
            .Where(id => id is not null && SessionQuickActions.Any(action => action.Id == id))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxQuickActions)
            .ToList();
    }

    private static bool IsQuickAction(string id) => SessionQuickActions.Any(action => action.Id == id);

    /// <summary>会话是否属于“不在项目中工作”的独立工作区。</summary>
    private bool IsScratchSession(SessionRecord session) =>
        ScratchProject() is { } scratch && session.ProjectId == scratch.Id;
    /// <summary>真实会话用宿主元数据；mock 用一次性临时库，保证归档页也有一致的空态。</summary>
    private DesktopMetadataStore MetadataOrMock() => _host?.Manager.Metadata ??
        (_mockOrganizationStore ??= new(new AppDataLayout(Path.Combine(Path.GetTempPath(), "Fufucode",
            "organization-demo-" + Guid.NewGuid().ToString("N")))));

    private SessionOrganization Organization() => new(MetadataOrMock());

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
        var addMenu = OverviewContextMenu();
        addMenu.Items.Add(OverviewMenu("新建分区", () => EditSectionAsync(null)));
        addMenu.Items.Add(OverviewMenu("添加项目", AddOverviewProjectAsync, _host is not null));
        Button add = null!;
        add = OverviewIcon("Add", "新建分区或项目",
            () => { addMenu.Open(add); return Task.CompletedTask; });
        add.ContextMenu = addMenu;
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
        menu.Items.Add(OverviewMenu("管理已归档…", ShowArchivesAsync, _host is not null));
        options.ContextMenu = menu;
        options.Click += (_, _) => menu.Open(options);
        Grid.SetColumn(options, 2); toolbar.Children.Add(options);
        _overview.Toolbar.Children.Add(toolbar);
        var metadataForArchive = MetadataOrMock();
        var archivedProjects = ProjectArchive.LoadSet(metadataForArchive);
        // 空会话（还没发第一条消息）不进入会话管理。
        var all = OverviewSessions()
            .Where(s => SessionPresence.IsEstablished(metadataForArchive, s))
            .Where(s => s.Id == CurrentSessionId ||
                !SessionArchive.IsArchived(metadataForArchive, s, archivedProjects)).ToArray();
        if (state.ActivityView)
        {
            OverviewHeading("优先级");
            var store = _schedules ?? ResolveScheduleStoreForTesting(_customization.DataRoot);
            var scheduled = store.Tasks.Where(t => t.State == "pending").Select(t => t.SessionId).ToHashSet();
            var important = all.Where(s => state.Priority switch
            {
                "pinned" => SessionPresentation.Load(_host!.Manager.Metadata, s.Id).Pinned,
                "scheduled" => scheduled.Contains(s.Id),
                _ => s.WasActive || SessionPresentation.Load(_host!.Manager.Metadata, s.Id).Unread ||
                    (s.Id == CurrentSessionId && (_vm.Input.IsTurnActive ||
                        _vm.Sidebar.SelectedSession is { IsUnread: true } or { IsFailed: true }))
            }).ToArray();
            foreach (var session in important) _overview.Body.Children.Add(BuildOverviewSessionRow(session, organization));
            if (important.Length == 0) OverviewEmpty("暂无需要关注的任务");
            foreach (var day in all.GroupBy(s => SessionActivityAt(s).LocalDateTime.Date).OrderByDescending(g => g.Key))
            {
                OverviewHeading(day.Key == DateTime.Today ? "今天" : day.Key == DateTime.Today.AddDays(-1) ? "昨天" : day.Key.ToString("MM-dd"));
                OverviewSessionGroup(day.OrderByDescending(SessionActivityAt), organization);
            }
            return;
        }
        OverviewHeading("置顶");
        var pinned = all.Where(s => SessionPresentation.Load(_host!.Manager.Metadata, s.Id).Pinned).ToArray();
        OverviewSessionGroup(pinned, organization);
        if (pinned.Length == 0) OverviewEmpty("无置顶会话");
        OverviewHeading("分区");
        foreach (var section in state.Sections)
        {
            var sectionCollapsed = _collapsedSections.Contains(section.Id);
            var header = OverviewCollapsibleHeading(section.Name, sectionCollapsed, () =>
            {
                if (!_collapsedSections.Add(section.Id)) _collapsedSections.Remove(section.Id);
            }, 8);
            AutomationProperties.SetAutomationId(header, "section.row." + section.Id);
            header.ContextMenu = OverviewContextMenu();
            header.ContextMenu.Items.Add(OverviewMenu("重命名", () => EditSectionAsync(section)));
            if (sectionCollapsed) continue;
            foreach (var project in OverviewProjects().Where(p => organization.Project(p).SectionId == section.Id))
                OverviewProjectRow(project, organization, all);
            OverviewSessionGroup(all.Where(s => organization.Placement(s.Id).SectionId == section.Id &&
                organization.Placement(s.Id).ProjectId is null), organization);
        }
        if (state.Sections.Count == 0) OverviewEmpty("无分区");
        var projectsHeading = OverviewCollapsibleHeading("项目", _allProjectsCollapsed,
            () => _allProjectsCollapsed = !_allProjectsCollapsed);
        AutomationProperties.SetAutomationId(projectsHeading, "projects.heading");
        if (!_allProjectsCollapsed)
            foreach (var project in OverviewProjects().Where(p => organization.Project(p).SectionId is null))
                OverviewProjectRow(project, organization, all);
        OverviewHeading("会话");
        // “会话”列出所有未归入用户项目的会话：显式无项目，以及“不在项目中工作”的独立工作区。
        var scratch = ScratchProject();
        var loose = all.Where(s =>
        {
            var placement = organization.Placement(s.Id);
            // 放置记录为空或与真实归属不同（存在展示分组）时按放置判断。
            if (organization.State.Sessions.ContainsKey(s.Id))
                return placement.ProjectId is null || placement.ProjectId == scratch?.Id;
            return s.ProjectId == scratch?.Id;
        }).OrderByDescending(SessionActivityAt).ToArray();
        OverviewSessionGroup(loose, organization);
        if (loose.Length == 0) OverviewEmpty("没有不在项目中的会话");
        if (_host is null)
            foreach (var demo in _vm.Sidebar.Sessions)
            {
                var label = OverviewHeading(demo.Title, 0); label.FontWeight = FontWeight.Normal;
                ToolTip.SetTip(label, demo.StatusDescription);
            }
    }

    /// <summary>“不在项目中工作”的独立工作区不是用户项目：不作为项目行展示。</summary>
    private ProjectRecord? ScratchProject()
    {
        var layout = _options.CreateAppDataLayout();
        var path = ScratchWorkspace.PathFor(layout);
        if (!Directory.Exists(path)) return null;
        try { return MetadataOrMock().TryGetProject(path); }
        catch (Exception) { return null; }
    }

    private IReadOnlyList<ProjectRecord> OverviewProjects()
    {
        var metadata = MetadataOrMock();
        var currentProjectId = _host?.Current.Project.Id;
        var scratchId = ScratchProject()?.Id;
        var visible = metadata.ListProjects()
            .Where(project => project.Id != scratchId)
            .Where(project => project.Id == currentProjectId || !ProjectArchive.IsArchived(metadata, project.Id))
            .ToArray();
        // 新登记的项目默认排在最前，不用在列表里逐个翻找。
        return ProjectOrder.Apply(metadata, visible);
    }

    /// <summary>所有会话（含独立工作区），项目行只展示真正的项目。</summary>
    private IEnumerable<SessionRecord> OverviewSessions()
    {
        var metadata = MetadataOrMock();
        return metadata.ListProjects().SelectMany(project => metadata.ListSessions(project.Id));
    }

    /// <summary>会话最后活动时间：优先会话文件写入时间，缺失时退回打开时间。</summary>
    private static DateTimeOffset SessionActivityAt(SessionRecord session) =>
        session.FilePath is { Length: > 0 } path && File.Exists(path)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
            : session.LastOpened;
    private TextBlock OverviewHeading(string title, double indent = 0)
    {
        // Win11 侧栏分组标题与条目图标列对齐：条目内边距 4+图标 margin 8 = 12px 基线。
        var text = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(indent + 12, 12, 4, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextSecondary"));
        _overview.Body.Children.Add(text); return text;
    }

    /// <summary>可折叠分组标题：整行可点，左侧箭头指示展开/收起。</summary>
    private Button OverviewCollapsibleHeading(string title, bool collapsed, Action toggle, double indent = 0)
    {
        var chevron = new PathIcon { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        chevron.Bind(PathIcon.DataProperty, new DynamicResourceExtension(
            collapsed ? "Icon.ChevronRight" : "Icon.ChevronDown"));
        var text = new TextBlock
        {
            Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextSecondary"));
        var content = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 6 };
        content.Children.Add(chevron);
        Grid.SetColumn(text, 1); content.Children.Add(text);
        var button = new Button
        {
            Classes = { "subtle" }, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(indent + 8, 4), MinHeight = 28,
        };
        button.Click += (_, _) => { toggle(); RenderOverview(); };
        _overview.Body.Children.Add(button);
        return button;
    }

    /// <summary>把一组会话行放进分层卡（页底 → 组卡 → 会话行）。</summary>
    private void OverviewSessionGroup(IEnumerable<SessionRecord> sessions, SessionOrganization organization,
        double indent = 0)
    {
        // 独立工作区的会话用「无项目」标记，避免用户看到内部目录名；分叉按父子树缩进。
        var rows = SessionTree(sessions.ToArray()).Select(entry => BuildOverviewSessionRow(entry.Session,
            organization, indent, IsScratchSession(entry.Session) ? "不在项目中" : null, entry.Depth)).ToArray();
        if (rows.Length == 0) return;
        var stack = new StackPanel { Spacing = 2 };
        foreach (var row in rows) stack.Children.Add(row);
        _overview.Body.Children.Add(new Border { Classes = { "overviewGroup" }, Child = stack });
    }
    private void OverviewEmpty(string text) => _overview.Body.Children.Add(new TextBlock
    { Text = text, Classes = { "meta" }, Margin = new Thickness(4, 3, 4, 5), TextWrapping = TextWrapping.Wrap });
    private Button OverviewIcon(string icon, string title, Func<Task> action)
    {
        var button = WorkbenchUi.IconButton(icon, title, 26);
        button.Padding = new Thickness(6);
        if (button.Content is PathIcon glyph) glyph.Width = glyph.Height = 14;
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
        var collapsed = _collapsedProjects.Contains(project.Id);
        // 按会话内容的最后写入时间排序，而不是“最近打开”：切换会话不会把旧会话顶到最前。
        var projectSessions = sessions.Where(s => organization.Placement(s.Id).ProjectId == project.Id)
            .OrderByDescending(SessionActivityAt).ToArray();

        var chevron = new PathIcon { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        chevron.Bind(PathIcon.DataProperty, new DynamicResourceExtension(
            collapsed ? "Icon.ChevronRight" : "Icon.ChevronDown"));
        var name = new TextBlock
        {
            Text = settings.Name, FontSize = 12, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var count = new TextBlock
        {
            Text = $"{projectSessions.Length} 个会话", Classes = { "meta" },
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8 };
        content.Children.Add(chevron);
        Grid.SetColumn(name, 1); content.Children.Add(name);
        Grid.SetColumn(count, 2); content.Children.Add(count);

        var row = new Button { Content = content, Classes = { "overviewProject" } };
        ToolTip.SetTip(row, settings.Folder + "（点击折叠/展开会话）");
        AutomationProperties.SetAutomationId(row, "project.row." + project.Id);
        row.Click += (_, _) =>
        {
            if (!_collapsedProjects.Add(project.Id)) _collapsedProjects.Remove(project.Id);
            RenderOverview();
        };
        var sections = new MenuItem { Header = "分区" };
        sections.Items.Add(OverviewMenu("无分区", () =>
        { organization.EditProject(project, settings.Name, settings.Folder, null); RenderOverview(); return Task.CompletedTask; }));
        foreach (var section in organization.State.Sections)
            sections.Items.Add(OverviewMenu(section.Name, () =>
            { organization.EditProject(project, settings.Name, settings.Folder, section.Id); RenderOverview(); return Task.CompletedTask; }));
        var menu = OverviewContextMenu();
        menu.Items.Add(OverviewMenu("打开项目", () => SwitchProjectAsync(settings.Folder)));
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
        menu.Items.Add(OverviewMenu("归档项目", () => ArchiveOverviewProjectAsync(project, settings)));
        menu.Items.Add(OverviewMenu("在资源管理器中打开", () => { OpenExternal(settings.Folder); return Task.CompletedTask; }));
        row.ContextMenu = menu;

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(row);
        if (!collapsed)
        {
            var expanded = _expandedProjects.Contains(project.Id);
            // 分叉跟随父会话，预览截断也要按树来截，避免只显示子会话。
            var tree = SessionTree(projectSessions);
            var visible = expanded ? tree : tree.Take(ProjectSessionPreview).ToArray();
            foreach (var entry in visible)
                stack.Children.Add(BuildOverviewSessionRow(entry.Session, organization, 8, null, entry.Depth));
            if (projectSessions.Length > ProjectSessionPreview)
            {
                var rest = projectSessions.Length - ProjectSessionPreview;
                var more = new Button
                {
                    Classes = { "subtle" }, Content = expanded ? "收起" : $"展开显示（还有 {rest} 个）",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    FontSize = 12, Padding = new Thickness(8, 3), Margin = new Thickness(8, 1, 0, 0),
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                };
                more.Click += (_, _) =>
                {
                    if (!_expandedProjects.Add(project.Id)) _expandedProjects.Remove(project.Id);
                    RenderOverview();
                };
                stack.Children.Add(more);
            }
        }
        _overview.Body.Children.Add(new Border { Classes = { "overviewGroup" }, Child = stack });
    }
    /// <summary>按父子关系展开会话树：分叉紧跟父会话，并记录缩进层级。</summary>
    private static IReadOnlyList<(SessionRecord Session, int Depth)> SessionTree(
        IReadOnlyList<SessionRecord> sessions)
    {
        var list = sessions.ToArray();
        var ids = list.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
        var children = list.Where(session => session.ParentSessionId is { Length: > 0 } parent && ids.Contains(parent))
            .GroupBy(session => session.ParentSessionId!)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(SessionActivityAt).ToArray(), StringComparer.Ordinal);
        var roots = list.Where(session => session.ParentSessionId is not { Length: > 0 } parent || !ids.Contains(parent))
            .OrderByDescending(SessionActivityAt);
        var result = new List<(SessionRecord, int)>();
        var guard = 0;
        void Add(SessionRecord session, int depth)
        {
            if (guard++ > 200) return;
            result.Add((session, depth));
            if (depth >= 8 || !children.TryGetValue(session.Id, out var kids)) return;
            foreach (var child in kids) Add(child, depth + 1);
        }
        foreach (var root in roots) Add(root, 0);
        return result;
    }

    private Control BuildOverviewSessionRow(SessionRecord session, SessionOrganization organization,
        double indent = 0, string? projectLabel = null, int depth = 0)
    {
        var metadata = _host!.Manager.Metadata;
        var presentation = SessionPresentation.Load(metadata, session.Id);
        var title = SessionTitles.Display(session);
        var projectPath = metadata.GetProject(session.ProjectId).Path;
        var isCurrent = session.Id == CurrentSessionId;

        var titleText = new TextBlock
        {
            Text = title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = isCurrent ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var button = new Button
        {
            Classes = { "subtle" }, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 30,
            Content = titleText,
        };
        ToolTip.SetTip(button, OverviewSessionCard(session, presentation,
            projectLabel ?? projectPath, title));
        ToolTip.SetShowDelay(button, 500);
        AutomationProperties.SetAutomationId(button, "session.row." + session.Id);
        button.Click += (_, _) => OverviewRun(() => ActivateOverviewSessionAsync(session));

        var tail = new StackPanel { Orientation = Orientation.Horizontal, IsVisible = false, Spacing = 0 };
        AutomationProperties.SetAutomationId(tail, "session.tail." + session.Id);
        foreach (var actionId in QuickActionsOf(presentation))
        {
            if (BuildSessionQuickAction(session, presentation, actionId) is { } action)
                tail.Children.Add(action);
        }

        var grid = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 2 };
        grid.Children.Add(button);
        Grid.SetColumn(tail, 1); grid.Children.Add(tail);
        var row = new Border
        {
            Classes = { "overviewSession" }, Margin = new Thickness(indent, 0, 0, 0), Child = grid,
        };
        void SetHover(bool on)
        {
            if (on) row.Classes.Add("hover"); else row.Classes.Remove("hover");
            tail.IsVisible = on;
        }
        // PointerEntered/Exited 是直接事件、不冒泡：行内按钮铺满后 Border 永远收不到，
        // 表现为"悬停不亮、移开闪一下"。用 IsPointerOver 属性（覆盖子树）驱动。
        row.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Input.InputElement.IsPointerOverProperty)
                SetHover(row.IsPointerOver || row.IsKeyboardFocusWithin);
        };
        row.GotFocus += (_, _) => SetHover(true);
        row.LostFocus += (_, _) => { if (!row.IsPointerOver) SetHover(false); };

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
        menu.Items.Add(OverviewMenu("分享", () => CopyAsync(SessionDeepLink.Create(session.Id, projectPath))));
        var copies = new MenuItem { Header = "复制" };
        copies.Items.Add(OverviewMenu("复制深度链接", () => CopyAsync(SessionDeepLink.Create(session.Id, projectPath))));
        copies.Items.Add(OverviewMenu("复制为 Markdown", async () => { await ActivateOverviewSessionAsync(session); await CopyAsync(SessionMarkdown()); }));
        copies.Items.Add(OverviewMenu("复制工作目录", () => CopyAsync(projectPath)));
        menu.Items.Add(copies);
        menu.Items.Add(QuickActionsMenu(session, presentation));
        menu.Items.Add(new Separator());
        menu.Items.Add(OverviewMenu("在新窗口中打开", async () => { await ActivateOverviewSessionAsync(session); await MoveSessionToWindowAsync(); }));
        menu.Items.Add(OverviewMenu("打开方式", async () => { await ActivateOverviewSessionAsync(session); await ShowOpenWithAsync(); }));
        menu.Items.Add(OverviewMenu("归档", () => ArchiveOverviewSessionAsync(session)));
        menu.Items.Add(OverviewMenu("永久删除", () => DeleteOverviewSessionAsync(session),
            session.Id != CurrentSessionId));
        button.ContextMenu = menu;

        // 分叉关系：子会话缩进，并在左侧画一条竖线（对齐 Claude Code 的分支树）。
        if (depth > 0)
        {
            row.Margin = new Thickness(indent + depth * 14, 0, 0, 0);
            var guide = new Border
            {
                Width = 1, Margin = new Thickness(-9, 3, 0, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
            };
            guide.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AppCardStroke"));
            var host = new Grid { Children = { guide, row } };
            return host;
        }
        return row;
    }

    /// <summary>右键菜单里的“固定到栏”：勾选后出现在会话行右侧，最多 4 个。</summary>
    private MenuItem QuickActionsMenu(SessionRecord session, SessionPresentation presentation)
    {
        var active = QuickActionsOf(presentation);
        var menu = new MenuItem { Header = "固定到栏" };
        foreach (var action in SessionQuickActions)
        {
            var item = new MenuItem { Header = action.Label };
            if (active.Contains(action.Id)) item.Icon = new TextBlock { Text = "✓" };
            item.Click += (_, e) =>
            {
                e.Handled = true;
                OverviewRun(() =>
                {
                    SetOverviewPresentation(session.Id, p =>
                    {
                        var list = QuickActionsOf(p);
                        if (list.Remove(action.Id)) return p with { RowActions = list };
                        if (list.Count >= MaxQuickActions)
                        {
                            _vm?.Timeline.AddNotification($"每个会话最多固定 {MaxQuickActions} 个快捷操作。");
                            return p;
                        }
                        list.Add(action.Id);
                        return p with { RowActions = list };
                    });
                    return Task.CompletedTask;
                });
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    private Button? BuildSessionQuickAction(SessionRecord session, SessionPresentation presentation, string actionId)
    {
        return actionId switch
        {
            "pin" => OverviewIcon("Pin", presentation.Pinned ? "取消置顶" : "置顶", () =>
            { SetOverviewPresentation(session.Id, p => p with { Pinned = !p.Pinned }); return Task.CompletedTask; }),
            "archive" => OverviewIcon("Archive", "归档", () => ArchiveOverviewSessionAsync(session)),
            "rename" => OverviewIcon("Edit", "重命名", async () =>
            { await ActivateOverviewSessionAsync(session); await RenameManagedSessionAsync(); }),
            "unread" => OverviewIcon("Chat", presentation.Unread ? "标为已读" : "标为未读", () =>
            { SetOverviewPresentation(session.Id, p => p with { Unread = !p.Unread }); return Task.CompletedTask; }),
            "fork" => OverviewIcon("Branch", "分叉", async () =>
            { await ActivateOverviewSessionAsync(session); await _chatPage.ChooseForkPointAsync(); }),
            "copy" => OverviewIcon("Copy", "复制深度链接",
                () => CopyAsync(SessionDeepLink.Create(session.Id,
                    _host!.Manager.Metadata.GetProject(session.ProjectId).Path))),
            "share" => OverviewIcon("Share", "分享",
                () => CopyAsync(SessionDeepLink.Create(session.Id,
                    _host!.Manager.Metadata.GetProject(session.ProjectId).Path))),
            "openWindow" => OverviewIcon("WindowNew", "在新窗口中打开", async () =>
            { await ActivateOverviewSessionAsync(session); await MoveSessionToWindowAsync(); }),
            "openWith" => OverviewIcon("Open", "打开方式", async () =>
            { await ActivateOverviewSessionAsync(session); await ShowOpenWithAsync(); }),
            "delete" => OverviewIcon("DismissCircle", "永久删除", () => DeleteOverviewSessionAsync(session)),
            _ => null,
        };
    }

    /// <summary>永久删除会话：二次确认后由 SessionDeletion 校验租约与路径。</summary>
    private async Task DeleteOverviewSessionAsync(SessionRecord session)
    {
        if (_host is null) return;
        if (session.Id == CurrentSessionId)
            throw new InvalidOperationException("不能删除当前打开的会话；请先切换到其他会话。");
        if (IdleReal() is { } reason) throw new InvalidOperationException(reason);
        var title = SessionTitles.Display(session);
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 88 };
        var delete = new Button { Content = "永久删除", MinWidth = 88 };
        var dialog = Views.AppDialog.Create("永久删除会话", 480);
        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = $"将删除会话「{title}」的历史文件与元数据，无法恢复。",
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = "归档是可恢复的；永久删除不会进入已归档列表。",
                    Classes = { "caption" }, TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto") };
        root.Children.Add(Views.AppDialog.Header("Icon.DismissCircle", "永久删除会话？",
            "此操作不可撤销。"));
        var content = Views.AppDialog.Body(Views.AppDialog.Card(body));
        Grid.SetRow(content, 1); root.Children.Add(content);
        var footer = Views.AppDialog.Footer(cancel, delete);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        dialog.Content = root;
        delete.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        if (!await Fufucode.App.Views.SkinDialog.ShowAsync<bool>(dialog, this)) return;
        var project = _host.Manager.Metadata.GetProject(session.ProjectId);
        SessionDeletion.Delete(_host.Manager.Layout, _host.Manager.Metadata, project.Path, session.Id);
        _archivePage?.Refresh();
        RefreshSessionList();
    }
    /// <summary>会话行悬停卡：标题、状态、后端、项目、更新时间与分叉信息。</summary>
    private Control OverviewSessionCard(SessionRecord session, SessionPresentation presentation,
        string projectPath, string title)
    {
        var card = new StackPanel { Spacing = 3, MaxWidth = 320 };
        card.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        var status = session.Id == CurrentSessionId ? "当前会话"
            : session.WasActive ? "上次运行中断"
            : presentation.Unread ? "有未读结果"
            : presentation.Pinned ? "已置顶"
            : "空闲";
        card.Children.Add(new TextBlock { Text = status, Classes = { "caption" } });
        card.Children.Add(new TextBlock
        {
            Text = "后端：" + BackendCapabilities.For(session.Backend).Name,
            Classes = { "caption" },
        });
        card.Children.Add(new TextBlock
        {
            Text = projectPath,
            Classes = { "caption", "mono" },
            TextWrapping = TextWrapping.Wrap,
        });
        card.Children.Add(new TextBlock
        {
            Text = "更新：" + session.LastOpened.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            Classes = { "caption" },
        });
        if (session.IsFork)
            card.Children.Add(new TextBlock { Text = "由其他会话分叉", Classes = { "caption" } });
        card.Children.Add(new TextBlock
        {
            // 悬停卡保留完整 id，方便排查；行标题本身不再显示 id。
            Text = "ID " + session.Id,
            Classes = { "caption", "mono" },
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return card;
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
        _archivePage?.Refresh();
        RenderOverview();
    }

    /// <summary>
    /// 归档整个项目：只写显示状态，不动目录与会话。若归档的是当前项目，
    /// 优先切到另一个仍存在且未归档的最近项目；没有则保留当前会话并提示。
    /// </summary>
    private async Task ArchiveOverviewProjectAsync(ProjectRecord project, SessionProjectOptions settings)
    {
        if (_host is null) return;
        var metadata = _host.Manager.Metadata;
        var isCurrent = RealSessionHost.PathEquals(_host.ProjectPath, settings.Folder);
        if (isCurrent && IdleReal() is { } reason) throw new InvalidOperationException(reason);
        ProjectArchive.SetArchived(metadata, project.Id, true);
        if (isCurrent)
        {
            var next = ProjectPreferences.LoadRecent(metadata)
                .FirstOrDefault(path => !RealSessionHost.PathEquals(path, settings.Folder) &&
                    Directory.Exists(path) && !ProjectArchive.IsArchivedPath(metadata, path));
            if (next is not null) await SwitchProjectAsync(next);
            else _vm?.Timeline.AddNotification("当前项目已归档；关闭应用后不会再自动打开。可在 设置 › 已归档 恢复。");
        }
        _archivePage?.Refresh();
        RefreshSessionList();
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
        var save = new Button { Content = "保存", IsDefault = true, Classes = { "accent" }, MinWidth = 88 };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 88 };
        var dialog = Views.AppDialog.Create(title, 440);
        var body = new StackPanel
        {
            Spacing = 12,
            Children = { Views.AppDialog.Field("分区名称", input, "分区用于在会话管理中归类项目与会话。") },
        };
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto") };
        root.Children.Add(Views.AppDialog.Header("Icon.AppsList", title, "给会话与项目一个固定归类。"));
        var content = Views.AppDialog.Body(Views.AppDialog.Card(body));
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        var footer = Views.AppDialog.Footer(cancel, save);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        dialog.Content = root;
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.Close(input.Text.Trim()); };
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        return await Fufucode.App.Views.SkinDialog.ShowAsync<string?>(dialog, this);
    }
    /// <summary>“添加项目”：选择文件夹并登记为项目；不切换当前会话的工作目录。</summary>
    private async Task AddOverviewProjectAsync()
    {
        if (_host is null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "添加项目", AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        var project = _host.Manager.Metadata.GetOrCreateProject(path);
        RenderOverview();
        await EditOverviewProjectAsync(project);
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
        var folderRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { folder, pick } };
        Grid.SetColumn(pick, 1);
        var error = Views.AppDialog.ErrorText();
        var save = new Button { Content = "保存", IsDefault = true, Classes = { "accent" }, MinWidth = 88 };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 88 };
        var dialog = Views.AppDialog.Create("编辑项目", 560);
        var body = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Views.AppDialog.Field("名称", name, "仅影响应用内列表显示。"),
                Views.AppDialog.Field("新会话文件夹", folderRow,
                    "只影响之后新建的会话；当前会话的工作目录保持不变。"),
                error,
            },
        };
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto") };
        root.Children.Add(Views.AppDialog.Header("Icon.Folder", "编辑项目",
            "调整项目在会话管理中的显示方式。"));
        var content = Views.AppDialog.Body(Views.AppDialog.Card(body));
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        var footer = Views.AppDialog.Footer(cancel, save);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        dialog.Content = root;
        dialog.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        save.Click += (_, _) =>
        {
            try
            {
                organization.EditProject(project, name.Text ?? "", folder.Text ?? "", settings.SectionId);
                dialog.Close();
                RenderOverview();
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
            }
        };
        cancel.Click += (_, _) => dialog.Close();
        await Fufucode.App.Views.SkinDialog.ShowAsync(dialog, this);
    }
}
