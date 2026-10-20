using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;
using Fufucode.Core.Updates;

namespace Fufucode.App;

public partial class MainWindow
{
    private string _branchLabel = "Git：未查询";
    private SessionPresentation _mockPresentation = new();
    private readonly List<(string? Session, AppPage Page)> _navigationHistory = new();
    private int _navigationIndex = -1;
    private bool _historyMoving;
    private double _zoom = 1;
    internal string? CurrentSessionId => _host?.Current.Session.Id;
    private string? Ready() => _vm is null ? "会话尚未就绪" : null;
    private string? IdleReal() => _host is null ? "Mock 没有持久 Pi 会话" : _switching || _vm?.Input.IsTurnActive == true ? "请等待当前回合结束" : null;

    private void RegisterWorkbenchActions()
    {
        void Action(string id, string title, string group, Func<Task> operation, Func<string?>? reason = null, string shortcut = "") =>
            Register(id, title, group, shortcut, title, operation, reason);
        void Now(string id, string title, string group, System.Action operation, Func<string?>? reason = null, string shortcut = "") =>
            Register(id, title, group, shortcut, title, () => { operation(); return Task.CompletedTask; }, reason);
        Now("toggle.left", "左侧边栏", "布局", () => ToggleDock(DockZone.Left));
        Now("toggle.right", "右侧边栏", "布局", () => ToggleDock(DockZone.Right));
        Now("toggle.bottom", "底部面板", "布局", () => ToggleDock(DockZone.Bottom), shortcut: "Ctrl+J");
        Now("layout.reset", "恢复默认布局", "布局", ResetDockLayout);
        Now("page.automation", "自动开发", "工作台", () => OpenDockPage("automation", DockZone.Right),
            () => _workbenchPages.ContainsKey("automation") ? null : "请先在设置中启用自动开发模块");
        Now("page.local-review", "本地审查", "项目", () => OpenDockPage("local-review", DockZone.Right),
            () => !_workbenchPages.ContainsKey("local-review") ? "请先在设置中启用本地审查模块" :
                _vm is null ? "会话尚未就绪" : !_vm.IsChatPage ? "返回工作区后可使用" : null);
        Now("composer.toggle", "显示/隐藏输入区", "布局", () => _customization.Change(s => s with { Composer = s.Composer == "hidden" ? "reading" : "hidden" }));
        Now("summary.toggle", "变更摘要", "布局", () => _customization.Change(s => s with { ShowChangeSummary = !s.ShowChangeSummary }));
        Action("navigate.back", "返回", "导航", () => MoveHistoryAsync(-1), () => _navigationIndex > 0 ? null : "没有上一位置");
        Action("navigate.forward", "前进", "导航", () => MoveHistoryAsync(1), () => _navigationIndex + 1 < _navigationHistory.Count ? null : "没有下一位置");
        Action("session.previous", "上一个会话", "导航", () => AdjacentSessionAsync(-1), IdleReal);
        Action("session.next", "下一个会话", "导航", () => AdjacentSessionAsync(1), IdleReal);
        Action("session.new", "新聊天", "会话", NewChatAsync, Ready);
        Now("window.new", "新建窗口", "窗口", () => new MainWindow(_options with { SessionId = null }).Show());
        Action("window.session", "在新窗口中打开当前会话", "窗口", MoveSessionToWindowAsync, IdleReal);
        _speech.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(_actions.RefreshCapabilities);
        Now("speech.stop", "停止朗读", "会话", _speech.Stop, () => _speech.IsSpeaking ? null : "没有正在朗读的回复");
        Action("speech.pause", "暂停朗读", "会话", _speech.PauseAsync, () => _speech.IsSpeaking && !_speech.IsPaused ? null : "没有可暂停的朗读");
        Action("speech.resume", "继续朗读", "会话", _speech.ResumeAsync, () => _speech.IsPaused ? null : "朗读未暂停");
        Action("speech.settings", "朗读设置", "会话", ShowSpeechSettingsAsync);
        Action("speech.start", "开始朗读", "会话", () => _speech.SpeakAsync(LastAssistantText()), () => LastAssistantText().Length == 0 ? "没有可朗读的回复" : null);
        Now("session.rename", "重命名", "会话", () => _chatPage.BeginRename(), () => _vm?.Sidebar.IsReal == true ? null : "Mock 会话没有持久名称");
        Now("session.search", "会话内查找", "会话", _chatPage.FindConversation, Ready);
        Now("session.pin", "置顶/取消置顶", "会话", () => ChangeSessionPresentation(s => s with { Pinned = !s.Pinned }), Ready);
        Now("session.unread", "标为未读", "会话", () => ChangeSessionPresentation(s => s with { Unread = true }), Ready);
        Action("session.archive", "归档当前会话", "会话", ArchiveCurrentAsync, IdleReal);
        Action("session.archives", "已归档会话", "会话", ShowArchivesAsync, () => _host is null ? "Mock 没有归档历史" : null);
        Action("session.fork", "分叉", "会话", () => _chatPage.ChooseForkPointAsync(), IdleReal);
        Action("session.side", "新建侧边聊天", "会话", StartSideChatAsync, IdleReal);
        Action("session.schedule", "添加计划任务", "会话", ShowSchedulesAsync, Ready);
        Action("session.share", "分享/导出会话", "会话", ExportSessionAsync, Ready);
        Action("session.export", "导出为 Markdown", "会话", ExportSessionAsync, Ready);
        Action("session.export.metadata", "导出元数据", "会话", ExportSessionMetadataAsync, Ready);
        Action("session.export.pdf", "导出为 PDF", "会话", ExportSessionPdfAsync, Ready);
        Action("session.fork.worktree", "在新工作树中创建分支", "会话", ForkIntoWorktreeAsync,
            () => IdleReal() ?? (_host?.Current.Session.FilePath is null ? "会话尚未落盘" : null));
        Action("copy.session", "复制会话", "会话", () => CopyAsync(SessionMarkdown()), Ready);
        Action("copy.id", "复制会话 ID", "会话", () => CopyAsync(CurrentSessionId ?? "mock-window"), Ready);
        Action("copy.project", "复制文件夹路径", "会话", () => CopyAsync(_host?.ProjectPath ?? Environment.CurrentDirectory));
        Action("copy.task", "复制会话文件路径", "会话", () => CopyAsync(_host!.Current.Session.FilePath!), () => _host?.Current.Session.FilePath is null ? "会话尚未落盘" : null);
        Action("copy.log", "复制日志路径", "诊断", () => CopyAsync(_host!.Current.DiagnosticFilePath), () => _host is null ? "当前没有 Pi 诊断日志" : null);
        Action("copy.markdown", "复制为 Markdown", "会话", () => CopyAsync(SessionMarkdown()), Ready);
        Action("copy.workdir", "复制工作目录", "会话", () => CopyAsync(_host?.ProjectPath ?? Environment.CurrentDirectory));
        Action("copy.deeplink", "复制深度链接", "会话", () => CopyAsync(SessionDeepLink.Create(CurrentSessionId!, _host!.ProjectPath)), () => _host is null ? "Mock 没有持久会话链接" : null);
        Now("project.explorer", "在资源管理器中打开", "项目", () => OpenExternal(_host?.ProjectPath ?? Environment.CurrentDirectory));
        Action("project.open", "打开文件夹", "项目", OpenProjectAsync);
        Action("project.openWith", "打开方式", "项目", ShowOpenWithAsync);
        Action("session.foreground", "前台运行", "会话", () => { Activate(); return NavigateWorkspaceAsync(AppPage.Chat); }, Ready);
        Action("diagnostics.export", "导出完整诊断包", "诊断", ExportDiagnosticsAsync);
        Action("help.shortcuts", "键盘快捷键", "帮助", () => ShowTextAsync("键盘快捷键", string.Join("\n", _actions.Actions.Where(a => a.Shortcut.Length > 0).Select(a => a.Shortcut + "    " + a.Title))));
        Action("help.troubleshoot", "故障排除", "帮助", ShowDiagnosticsAsync);
        Action("help.status", "系统状态", "帮助", ShowDiagnosticsAsync);
        Action("help.update", "检查更新", "帮助", CheckUpdatesAsync);
        Action("help.about", "关于 Fufucode", "帮助", ShowAboutAsync);
        Action("auth.manage", "模型认证管理", "设置", ShowAuthenticationAsync, Ready);
        Now("view.fullscreen", "切换全屏", "窗口", () => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen);
        Now("view.zoomIn", "放大", "窗口", () => SetZoom(_zoom + .1));
        Now("view.zoomOut", "缩小", "窗口", () => SetZoom(_zoom - .1));
        Now("view.zoomReset", "实际大小", "窗口", () => SetZoom(1));
        foreach (var (id, title) in new[] { ("status.connection", "后端连接"), ("status.shell", "Shell 连接"), ("status.activity", "会话活动"), ("status.tokens", "会话 Token"), ("status.duration", "会话用时"), ("status.project", "项目名称"), ("status.branch", "Git 分支") })
            Action(id, title, "状态", () => ShowTextAsync(title, StatusText(id)));
        Now("edit.undo", "撤销", "编辑", () => FocusedText()?.Undo());
        Now("edit.redo", "重做", "编辑", () => FocusedText()?.Redo());
        Now("edit.cut", "剪切", "编辑", () => FocusedText()?.Cut());
        Now("edit.copy", "复制", "编辑", () => FocusedText()?.Copy());
        Now("edit.paste", "粘贴", "编辑", () => FocusedText()?.Paste());
        Now("edit.delete", "删除选中文字", "编辑", () => { if (FocusedText() is { IsReadOnly: false } text) text.SelectedText = ""; });
        Now("edit.selectAll", "全选", "编辑", () => FocusedText()?.SelectAll());
    }
    private TextBox? FocusedText() => FocusManager?.GetFocusedElement() as TextBox ?? _lastTextFocus;
    private TextBox? _lastTextFocus;
    private void SetZoom(double zoom)
    { _zoom = Math.Clamp(zoom, .75, 1.5); _customization.Change(s => s with { FontSize = 13 * _zoom }); }
    private async Task NewChatAsync()
    {
        if (_host is null) { _vm?.Timeline.Reset(); if (_vm is not null) { _vm.Input.DraftText = ""; _vm.Input.ReferenceContext = ""; } }
        else await SwitchSessionAsync(null);
    }
    internal AppOptions CreateTemporaryOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "Fufucode", "temporary-" + Guid.NewGuid().ToString("N"));
        var source = _options.CreateAppDataLayout();
        var target = new AppDataLayout(root);
        target.EnsureCreated();
        new AppSettingsStore(target).Update(_ => new AppSettingsStore(source).Current);
        // Carry provider configuration, while keeping history and desktop metadata isolated.
        foreach (var name in new[] { "models.json", "auth.json", "fufucode-provider.txt" })
        {
            var path = Path.Combine(source.AgentDirectory, name);
            if (File.Exists(path)) File.Copy(path, Path.Combine(target.AgentDirectory, name));
        }
        return _options with { AppDataRootOverride = root, SessionId = null, ProjectPath = _host?.ProjectPath ?? _options.ProjectPath };
    }
    private async Task MoveSessionToWindowAsync()
    {
        if (_host is null) return;
        var id = _host.Current.Session.Id;
        var options = _options with { ProjectPath = _host.ProjectPath, SessionId = id };
        await SwitchSessionAsync(null);
        if (CurrentSessionId != id) new MainWindow(options).Show();
    }
    private Task SwitchChatTabFocusAsync()
    {
        if (_vm?.IsChatPage != true) return NavigateWorkspaceAsync(AppPage.Chat);
        if (_chatPage.IsKeyboardFocusWithin)
        {
            var page = _dockLayout.Selected[DockZone.Right];
            if (page is null || !_workbenchPages.ContainsKey(page)) page = "files";
            OpenDockPage(page, DockZone.Right);
            _workbenchPages[page].Content.Focusable = true; _workbenchPages[page].Content.Focus();
        }
        else _chatPage.Input.Focus();
        return Task.CompletedTask;
    }
    internal void PreviewArtifact(string path)
    {
        var full = Path.GetFullPath(path, _host?.ProjectPath ?? Environment.CurrentDirectory);
        if (!File.Exists(full)) throw new FileNotFoundException("文件已不存在", full);
        _vm?.WorkspacePanel.OpenFilePreview(full);
        Views.BrowserView.TryPreviewFile(WorkspacePanel, full);
        Views.BrowserView.TryPreviewFile(this, full);
        OpenDockPage("files", DockZone.Right);
    }
    private SessionPresentation CurrentPresentation => _host is null ? _mockPresentation : SessionPresentation.Load(_host.Manager.Metadata, _host.Current.Session.Id);
    private void ChangeSessionPresentation(Func<SessionPresentation, SessionPresentation> edit)
    {
        var next = edit(CurrentPresentation);
        if (_host is null) _mockPresentation = next;
        else
        {
            SessionPresentation.Save(_host.Manager.Metadata, _host.Current.Session.Id, next);
            _vm?.Sidebar.UpdateSessions(BuildSessionItems(_host), _host.Current.Session.Id, _host.Current.Session.Name ?? "");
        }
        _vm?.Sidebar.SelectedSession?.MarkUnread(next.Unread);
        RenderOverview();
    }
    private async Task ArchiveCurrentAsync()
    {
        if (_host is null) return;
        var id = _host.Current.Session.Id;
        var archivedProjects = ProjectArchive.LoadSet(_host.Manager.Metadata);
        var next = _host.Sessions.FirstOrDefault(s => s.Id != id &&
            !SessionArchive.IsArchived(_host.Manager.Metadata, s, archivedProjects));
        if (next is null) await SwitchSessionAsync(null); else await SwitchSessionAsync(next.Id);
        if (CurrentSessionId == id) throw new InvalidOperationException("当前会话未切换，不会归档正在使用的会话。");
        SessionPresentation.Save(_host.Manager.Metadata, id, SessionPresentation.Load(_host.Manager.Metadata, id) with { Archived = true });
        _vm?.Sidebar.UpdateSessions(BuildSessionItems(_host), _host.Current.Session.Id, _host.Current.Session.Name ?? "");
        RenderOverview();
    }
    /// <summary>“已归档”入口：打开设置中的归档管理页（项目与会话统一管理）。</summary>
    private async Task ShowArchivesAsync()
    {
        if (_vm is null) return;
        _vm.Settings.SelectCategory("archived");
        await NavigateWorkspaceAsync(AppPage.Settings);
        _archivePage?.Refresh();
    }
    private void RecordNavigation()
    {
        if (_historyMoving || _vm is null) return;
        var point = (CurrentSessionId, _vm.CurrentPage);
        if (_navigationIndex >= 0 && _navigationHistory[_navigationIndex] == point) return;
        if (_navigationIndex + 1 < _navigationHistory.Count) _navigationHistory.RemoveRange(_navigationIndex + 1, _navigationHistory.Count - _navigationIndex - 1);
        _navigationHistory.Add(point); _navigationIndex = _navigationHistory.Count - 1;
        _actions.RefreshCapabilities();
    }
    private async Task MoveHistoryAsync(int direction)
    {
        var index = _navigationIndex + direction;
        if (index < 0 || index >= _navigationHistory.Count) return;
        _historyMoving = true;
        try
        {
            var target = _navigationHistory[index];
            if (target.Session is { } id && id != CurrentSessionId) await SwitchSessionAsync(id);
            await NavigateWorkspaceAsync(target.Page);
            if (_vm?.CurrentPage == target.Page && target.Session == CurrentSessionId) _navigationIndex = index;
        }
        finally { _historyMoving = false; _actions.RefreshCapabilities(); }
    }
    private async Task AdjacentSessionAsync(int direction)
    {
        if (_host is null) return;
        var archivedProjects = ProjectArchive.LoadSet(_host.Manager.Metadata);
        var sessions = _host.Sessions.Where(s =>
            !SessionArchive.IsArchived(_host.Manager.Metadata, s, archivedProjects)).ToArray();
        var current = Array.FindIndex(sessions, s => s.Id == CurrentSessionId);
        var index = current + direction;
        if (index >= 0 && index < sessions.Length) await SwitchSessionAsync(sessions[index].Id);
    }
    private Task CopyAsync(string text) => Clipboard is null ? Task.FromException(new InvalidOperationException("剪贴板不可用")) : Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(Clipboard, text);
    private string LastAssistantText() => _vm is null ? "" :
        _vm.Timeline.Entries.LastOrDefault(e => e is AssistantMessageViewModel) is { } entry ? Views.MessageActionsView.MessageText(entry) : "";

    /// <summary>导出会话元数据（JSON）：会话身份、模型、用时与产物清单，不含正文。</summary>
    private async Task ExportSessionMetadataAsync()
    {
        var payload = new
        {
            sessionId = CurrentSessionId ?? "mock-window",
            title = _vm?.Sidebar.SelectedSession?.Title,
            model = _vm?.StatusBar.ModelText,
            project = _host?.ProjectPath,
            exportedAt = DateTimeOffset.Now,
            artifactCount = _vm?.Timeline.Entries.OfType<AssistantMessageViewModel>().Count(e => e.HasArtifactSummary) ?? 0,
            changeCount = _vm?.Timeline.Entries.OfType<AssistantMessageViewModel>().Count(e => e.HasChangeSummary) ?? 0,
        };
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        { Title = "导出会话元数据", SuggestedFileName = "fufucode-session.json", DefaultExtension = "json" });
        if (file is null) return;
        using var stream = await file.OpenWriteAsync(); stream.SetLength(0); using var writer = new StreamWriter(stream);
        await writer.WriteAsync(System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private string SessionMarkdown()
    {
        var text = new StringBuilder("# " + (_vm?.Sidebar.SelectedSession?.Title ?? "会话") + "\n\n");
        if (_vm is null) return text.ToString();
        foreach (var entry in _vm.Timeline.Entries)
        {
            var content = Views.MessageActionsView.MessageText(entry);
            if (content.Length == 0) continue;
            text.Append("## ").Append(entry is UserMessageViewModel ? _customization.Current.UserName : _customization.Current.AssistantName).Append("\n\n").Append(content).Append("\n\n");
        }
        return text.ToString();
    }
    /// <summary>导出为 PDF：A4 排版，保留标题、元信息、逐条消息与代码块；中文字体走系统雅黑。</summary>
    private async Task ExportSessionPdfAsync()
    {
        if (_vm is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        { Title = "导出为 PDF", SuggestedFileName = "fufucode-session.pdf", DefaultExtension = "pdf" });
        if (file is null) return;

        var messages = new List<SessionPdfMessage>();
        foreach (var entry in _vm.Timeline.Entries)
        {
            var content = Views.MessageActionsView.MessageText(entry);
            if (content.Length == 0) continue;
            var isUser = entry is UserMessageViewModel;
            messages.Add(new SessionPdfMessage(
                isUser ? _customization.Current.UserName : _customization.Current.AssistantName,
                content,
                isUser));
        }

        var title = _vm.Sidebar.SelectedSession?.Title;
        var metadata = new List<(string Label, string Value)>
        {
            ("会话", string.IsNullOrWhiteSpace(title) ? "未命名会话" : title!),
            ("会话 ID", CurrentSessionId ?? "mock-window"),
            ("项目", _host?.ProjectPath ?? Environment.CurrentDirectory),
            ("模型", _vm.StatusBar.ModelText),
            ("导出时间", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")),
        };

        using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        SessionPdfExporter.Write(stream, string.IsNullOrWhiteSpace(title) ? "Fufucode 会话" : title!, metadata, messages);
    }

    private async Task ExportSessionAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "导出会话", SuggestedFileName = "fufucode-session.md", DefaultExtension = "md" });
        if (file is null) return;
        using var stream = await file.OpenWriteAsync(); stream.SetLength(0); using var writer = new StreamWriter(stream);
        await writer.WriteAsync(SessionMarkdown());
    }
    private static void OpenExternal(string path) => Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    private Task ShowOpenWithAsync()
    {
        var panel = new StackPanel { Spacing = 8 };
        var folder = _host?.ProjectPath ?? Environment.CurrentDirectory;
        var explorer = new Button { Content = "资源管理器" }; explorer.Click += (_, _) => OpenExternal(folder); panel.Children.Add(explorer);
        foreach (var (name, executable) in new[] { ("VS Code", "code.cmd"), ("Cursor", "cursor.cmd"), ("IntelliJ IDEA", "idea64.exe") })
        {
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p.Trim('"'), executable)).FirstOrDefault(File.Exists);
            var button = new Button { Content = name, IsEnabled = path is not null };
            ToolTip.SetTip(button, path ?? "未在 PATH 中找到，可用下面的自选程序");
            button.Click += (_, _) => Process.Start(new ProcessStartInfo { FileName = path!, Arguments = "\"" + folder + "\"", UseShellExecute = true }); panel.Children.Add(button);
        }
        var custom = new Button { Content = "选择程序…" };
        custom.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择编辑器", FileTypeFilter = [new("程序") { Patterns = ["*.exe"] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) Process.Start(new ProcessStartInfo { FileName = path, ArgumentList = { folder }, UseShellExecute = false });
        };
        panel.Children.Add(custom); return ShowPanelAsync("打开方式", panel);
    }
    private async Task ShowLocalDocumentAsync(string relative)
    {
        var path = Path.Combine(FindRepositoryRoot(), relative);
        if (!File.Exists(path)) throw new FileNotFoundException("本地文档不可用", path);
        await ShowTextAsync(Path.GetFileName(path), await File.ReadAllTextAsync(path));
    }
    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "docs", "AI_DEVELOPMENT_PLAN.md"))) return directory.FullName;
        return AppContext.BaseDirectory;
    }
    private Task ShowDiagnosticsAsync() => ShowTextAsync("系统状态与故障排除", DiagnosticSummary());

    /// <summary>关于 Fufucode：版本与数据目录，并把文档、新功能、反馈三个入口收进这里。</summary>
    private Task ShowAboutAsync()
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        var version = typeof(MainWindow).Assembly.GetName().Version;
        panel.Children.Add(new TextBlock { Text = "Fufucode", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 16 });
        panel.Children.Add(new TextBlock { Text = "基于官方 Pi RPC 的 Avalonia 桌面客户端", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "版本 " + version, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .75, FontSize = 12 });
        panel.Children.Add(new TextBlock { Text = "数据目录：" + _options.CreateAppDataLayout().Root, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .75, FontSize = 12 });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var docs = new Button { Content = "文档" };
        docs.Click += async (_, _) => await ShowLocalDocumentAsync("docs/AI_DEVELOPMENT_PLAN.md");
        var changes = new Button { Content = "新功能" };
        changes.Click += async (_, _) => await ShowLocalDocumentAsync("docs/decisions.md");
        var feedback = new Button { Content = "反馈问题" };
        feedback.Click += async (_, _) => await ExportDiagnosticsAsync();
        actions.Children.Add(docs); actions.Children.Add(changes); actions.Children.Add(feedback);
        panel.Children.Add(actions);
        return ShowPanelAsync("关于 Fufucode", panel);
    }
    private string DiagnosticSummary() => $"应用：{typeof(MainWindow).Assembly.GetName().Version}\n.NET：{Environment.Version}\n系统：{Environment.OSVersion}\n{_vm?.StatusBar.ConnectionText}\n{_vm?.StatusBar.ActivityText}\n审批：{_vm?.StatusBar.ApprovalText}\n数据：{_options.CreateAppDataLayout().Root}\n日志：{_host?.Current.DiagnosticFilePath ?? "无"}\nUI 配置：{_customization.ScopePath}\nUI 诊断：{_customization.Status}";
    private async Task ExportDiagnosticsAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "导出诊断包", SuggestedFileName = "fufucode-diagnostics.zip", DefaultExtension = "zip" });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
        var count = await DiagnosticExport.WriteArchiveAsync(_options.CreateAppDataLayout(), stream,
            typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown");
        await ShowTextAsync("诊断包已导出", $"已导出 {count} 条脱敏调用记录。未包含凭据、环境变量值、会话正文或原始配置。");
    }
    private async Task CheckUpdatesAsync()
    {
        var layout = _options.CreateAppDataLayout();
        var settingsStore = _settingsStore ?? new AppSettingsStore(layout);
        using var updateService = new AppUpdateService(layout, settingsStore);
        var currentVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.4.0";
        var result = await updateService.CheckForUpdatesAsync(currentVersion);
        if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Manifest is not null)
        {
            var text = $"{result.Message}\n\n更新说明：\n{result.Manifest.ReleaseNotes}\n\n下载包：{result.Manifest.PackageUrl}\n大小：{result.Manifest.PackageSizeBytes / 1024 / 1024} MB\nSHA-256：{result.Manifest.Sha256}\n政策：{result.Manifest.PolicyNotice}\n\n提示：可在「设置 - 关于」中一键下载并安全更新。";
            await ShowTextAsync("发现软件更新", text);
        }
        else
        {
            await ShowTextAsync("检查更新", result.Message);
        }
    }
    private static async Task<string> RunOutputAsync(string executable, string[] arguments, string directory)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 " + executable);
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        return await output;
    }
    private Task ShowTextAsync(string title, string text) => ShowPanelAsync(title, new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 200 });
    private async Task ShowPanelAsync(string title, Control content)
    {
        var close = new Button { Content = "关闭", HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
        var dialog = new Window { Title = title, Width = 640, Height = 480, MinWidth = 400, MinHeight = 250, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 12 };
        grid.Children.Add(new ScrollViewer { Content = content }); Grid.SetRow(close, 1); grid.Children.Add(close);
        dialog.Content = grid; close.Click += (_, _) => dialog.Close(); await dialog.ShowDialog(this);
    }
}
