using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Abstractions;
using Fufucode.App.Infrastructure;
using Fufucode.App.Mock;
using Fufucode.App.Features.Backend;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.App.ViewModels;

/// <summary>主区域页面：对话 / 扩展 / 设置（M6）。</summary>
public enum AppPage
{
    Chat,
    Extensions,
    Settings,
}

/// <summary>组合入口：装配会话（mock 或真实 Core 的 IChatSession）与各视图模型。</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    public MainViewModel(IChatSession session, IClipboardService clipboard, IRefreshScheduler refreshScheduler,
        IReadOnlyList<SessionItemViewModel>? sessions = null, SidebarViewModel.Callbacks? sidebarCallbacks = null)
    {
        Session = session;
        Timeline = new TimelineViewModel(session, refreshScheduler, clipboard);
        Fufucode.Core.Diagnostics.StartupProbe.Mark("vm.timeline", "TimelineViewModel");
        Input = new InputViewModel(session);
        Fufucode.Core.Diagnostics.StartupProbe.Mark("vm.input", "InputViewModel");
        StatusBar = new StatusBarViewModel(session);
        Fufucode.Core.Diagnostics.StartupProbe.Mark("vm.statusbar", "StatusBarViewModel");
        Sidebar = new SidebarViewModel(session, Timeline, sessions, sidebarCallbacks);
        Fufucode.Core.Diagnostics.StartupProbe.Mark("vm.sidebar", "SidebarViewModel");
        Extensions = new ExtensionsPageViewModel();
        Settings = new SettingsPageViewModel();
        WorkspacePanel = new WorkspacePanelViewModel(clipboard);
        Fufucode.Core.Diagnostics.StartupProbe.Mark("vm.rest", "extensions/settings/workspace");

        // 发送/停止失败在真实链路下必须显式可见（不静默吞异常）。
        Input.OperationFailed += message => Timeline.AddNotification(message);
        Fufucode.Core.Diagnostics.StartupProbe.Mark("vm.wire", "input failure wiring");
    }

    /// <summary>当前会话：mock 为脚本化演示；真实实现由当前后端（默认 FUMO 原生内核）提供。</summary>
    public IChatSession Session { get; }

    /// <summary>侧栏副标题：如实标注数据来源，mock 不得冒充真实连接。</summary>
    public string ModeText => Session is MockChatSession ? "mock 数据演示 · 未连接真实后端" :
        "真实 " + (Session is IBackendChatSession backend ? backend.BackendName : "后端") + " 会话";

    /// <summary>当前项目根：Markdown 相对图片按此解析。由组合根在切换项目时写入。</summary>
    [ObservableProperty] private string? _projectRootPath;

    public TimelineViewModel Timeline { get; }

    public InputViewModel Input { get; }

    public StatusBarViewModel StatusBar { get; }

    public SidebarViewModel Sidebar { get; }

    /// <summary>扩展页 VM。</summary>
    public ExtensionsPageViewModel Extensions { get; }

    /// <summary>设置页 VM（M6/W1-a）。</summary>
    public SettingsPageViewModel Settings { get; }

    public WorkspacePanelViewModel WorkspacePanel { get; }

    public Func<AppPage, Task>? NavigateRequested { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatPage))]
    [NotifyPropertyChangedFor(nameof(IsExtensionsPage))]
    [NotifyPropertyChangedFor(nameof(IsSettingsPage))]
    [NotifyPropertyChangedFor(nameof(CurrentPageContent))]
    private AppPage _currentPage = AppPage.Chat;

    /// <summary>页面宿主绑定的内容对象：Chat 页用本 VM，扩展/设置页用各自 VM。</summary>
    public object CurrentPageContent => CurrentPage switch
    {
        AppPage.Extensions => Extensions,
        AppPage.Settings => Settings,
        _ => this,
    };

    public bool IsRightPanelOpen { get; private set; }

    public void ToggleRightPanel()
    {
        IsRightPanelOpen = !IsRightPanelOpen;
        OnPropertyChanged(nameof(IsRightPanelOpen));
    }

    public void SetRightPanelOpen(bool open)
    {
        if (IsRightPanelOpen == open) return;
        IsRightPanelOpen = open;
        OnPropertyChanged(nameof(IsRightPanelOpen));
    }

    public bool IsChatPage => CurrentPage == AppPage.Chat;

    public bool IsExtensionsPage => CurrentPage == AppPage.Extensions;

    public bool IsSettingsPage => CurrentPage == AppPage.Settings;

    /// <summary>底部导航切换页面。</summary>
    [RelayCommand]
    private async Task NavigateAsync(AppPage page)
    {
        if (NavigateRequested is { } navigate) await navigate(page);
        else CurrentPage = page;
    }

    /// <summary>连接会话（mock 为同步置位；真实会话在组合根启动阶段已连接，无需再次连接）。</summary>
    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (Session is MockChatSession mock)
        {
            await mock.ConnectAsync();
        }
    }


    [ObservableProperty] private string _currentBackendId = "fumo";
    [ObservableProperty] private string _currentBackendName = "FUMO";

}
