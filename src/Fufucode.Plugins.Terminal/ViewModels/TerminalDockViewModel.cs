using System;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.Plugins.Terminal.Features.TerminalExperience;
using Fufucode.App.ViewModels;
using Fufucode.Plugins.Terminal.Infrastructure;

namespace Fufucode.Plugins.Terminal.ViewModels;

/// <summary>Static project-owned terminal container for H-S09; agent tool jobs have no ownership claim here.</summary>
public sealed partial class TerminalDockViewModel : ViewModelBase, IDisposable
{
    private readonly bool _useMock;
    private readonly Func<ITerminalSession> _factory;
    private readonly TerminalSettings _settings;
    private int _nextNumber;
    private bool _disposed;

    public TerminalDockViewModel(bool useMock = false, string? workspacePath = null, Func<ITerminalSession>? sessionFactory = null,
        TerminalSettings? settings = null)
    {
        _useMock = useMock;
        _settings = (settings ?? TerminalSettings.Default).Normalize();
        WorkspacePath = Directory.Exists(workspacePath) ? workspacePath! : Environment.CurrentDirectory;
        _factory = sessionFactory ?? DefaultSessionFactory;
    }

    /// <summary>当前终端设置（字号、回滚、shell 配置等）；由页面在设置变化时整体替换。</summary>
    public TerminalSettings Settings => _settings;

    /// <summary>默认会话工厂：按设置解析 shell 配置；不可用时把原因带给标签页，而不是静默回退。</summary>
    private ITerminalSession DefaultSessionFactory()
    {
        if (_useMock) return new TerminalMockSession();
        var profile = string.Equals(_settings.ShellProfile, ShellProfiles.AutoId, StringComparison.OrdinalIgnoreCase)
            ? ShellProfiles.ResolveAuto()
            : ShellProfiles.Find(_settings.ShellProfile) ?? ShellProfiles.ResolveAuto();
        var executable = ShellProfiles.Resolve(profile);
        if (executable is null)
            return new UnavailableSession($"所选 shell 不可用：{profile.DisplayName}。请在终端设置中选择已安装的 shell。");
        var commandLine = ShellProfiles.BuildCommandLine(profile, executable, _settings.CommandOverride);
        return new ConPtyTerminalSession(commandLine);
    }

    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = new();
    public string WorkspacePath { get; private set; }
    public void SetWorkspacePath(string? workspacePath)
    {
        if (Directory.Exists(workspacePath)) { WorkspacePath = workspacePath!; OnPropertyChanged(nameof(WorkspacePath)); }
    }
    public string OwnershipText => "项目终端";
    public string OwnershipTooltip => "终端属于当前项目容器。Agent 工具执行与这些交互 shell 的实例归属将在后续接线。";
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private TerminalTabViewModel? _selectedTab;
    /// <summary>Mirrors the selected tab's output selection so the copy entry can reflect availability.</summary>
    [ObservableProperty] private bool _hasOutputSelection;
    public bool HasTabs => Tabs.Count > 0;

    /// <summary>Called by the view after a selection gesture so the copy entry stays in sync per tab.</summary>
    public void RefreshOutputSelection() => HasOutputSelection = SelectedTab?.OutputSelection.HasText == true;

    public void Show()
    {
        if (_disposed) return;
        if (Tabs.Count == 0) CreateTerminal();
        IsVisible = true;
    }
    public void Toggle() { if (IsVisible) Hide(); else Show(); }
    [RelayCommand] public void Hide() => IsVisible = false;
    [RelayCommand] public void CreateTerminal()
    {
        if (_disposed) return;
        // Set visibility before the tab selection event: the host reacts to SelectedTab and must see
        // the page as visible, otherwise it would close the dock page it is currently opening.
        IsVisible = true;
        var tab = new TerminalTabViewModel(++_nextNumber, _useMock, _factory(), _settings);
        Tabs.Add(tab); SelectedTab = tab;
        OnPropertyChanged(nameof(HasTabs));
        tab.Start(WorkspacePath);
    }
    [RelayCommand] public void CloseTerminal(TerminalTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null || !Tabs.Contains(tab)) return;
        var index = Tabs.IndexOf(tab);
        tab.Dispose(); Tabs.Remove(tab);
        if (ReferenceEquals(SelectedTab, tab)) SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        if (Tabs.Count == 0) IsVisible = false;
        OnPropertyChanged(nameof(HasTabs));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var tab in Tabs) tab.Dispose();
    }
}

public sealed partial class TerminalTabViewModel : ViewModelBase, IDisposable
{
    private readonly ITerminalSession _session;
    private bool _disposed;
    private TerminalSnapshot? _snapshot;
    public TerminalTabViewModel(int number, bool isMock, ITerminalSession session, TerminalSettings? settings = null)
    {
        TerminalSessionId = Guid.NewGuid().ToString("N");
        _title = $"终端 {number}";
        IsMock = isMock;
        _session = session;
        Settings = (settings ?? TerminalSettings.Default).Normalize();
        Buffer.ScrollbackLimit = Settings.ScrollbackLines;
        Buffer.Bell += OnBell;
        _session.Output += OnOutput;
        _session.Exited += OnExited;
        Buffer.Changed += () => _snapshot = null;
    }

    public TerminalSettings Settings { get; }
    /// <summary>Last terminal title reported by the shell (OSC 0/1/2); empty when none was set.</summary>
    public string ShellTitle => Buffer.Title;
    /// <summary>Raised for BEL according to the configured bell mode (none/visual/beep).</summary>
    public event Action? BellRequested;
    private void OnBell() { if (Settings.Bell != "none") Dispatch(() => BellRequested?.Invoke()); }

    public string TerminalSessionId { get; }
    /// <summary>User-visible tab title; starts as “终端 N” and can be renamed in place.</summary>
    [ObservableProperty] private string _title = "终端";
    public bool IsMock { get; }
    /// <summary>True once this tab has been closed; pending asynchronous work must not touch it.</summary>
    public bool IsDisposed => _disposed;
    public TerminalBuffer Buffer { get; } = new();
    public double ScrollOffsetY { get; set; }
    public bool FollowOutput { get; set; } = true;

    /// <summary>
    /// Stable snapshot of the bounded screen + scrollback. Cached until the buffer changes so selection
    /// and search can prove they still refer to the text they were resolved against.
    /// </summary>
    public TerminalSnapshot Snapshot => _snapshot ??= Buffer.Snapshot();

    /// <summary>Selection is per tab and anchored to the snapshot it was captured from.</summary>
    public TerminalOutputSelection OutputSelection { get; } = new();

    /// <summary>Command history for this tab only; in memory, bounded, never persisted or shared.</summary>
    public TerminalCommandHistory History { get; } = new();

    /// <summary>Per-tab output search state (query, hits and current position).</summary>
    public TerminalSearchSession Search { get; } = new();

    [ObservableProperty] private string _statusText = "启动中";
    /// <summary>OSC 8 hyperlink under the pointer (rendering highlight only).</summary>
    [ObservableProperty] private string? _hoveredLink;
    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _searchMatchCase;
    [ObservableProperty] private string _searchStatus = string.Empty;
    [ObservableProperty] private bool _isSearchOpen;

    public void Start(string workspace)
    {
        RestartDirectory = workspace;
        if (_session.StartError is { Length: > 0 } unavailable)
        {
            // A configured shell that is not installed is reported honestly instead of silently using cmd.
            IsRunning = false;
            StatusText = "shell 不可用";
            Buffer.Append("\u001b[31m" + unavailable + "\u001b[0m\r\n");
            return;
        }
        try
        {
            _session.Start(workspace, Buffer.Columns, Buffer.Rows);
            IsRunning = _session.IsRunning;
            StatusText = IsMock ? "演示" : ShellProfiles.DescribeResolution(Settings.ShellProfile);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or PlatformNotSupportedException or IOException or System.Runtime.InteropServices.COMException)
        {
            StatusText = "启动失败";
            Buffer.Append("\u001b[31m终端启动失败：" + error.Message + "\u001b[0m\r\n");
        }
    }

    /// <summary>重启当前 shell（退出后使用）；会话实例按标签页保留，重启沿用同一项目目录。</summary>
    public void Restart()
    {
        if (_disposed || IsRunning) return;
        try { _session.Start(RestartDirectory, Buffer.Columns, Buffer.Rows); IsRunning = _session.IsRunning; StatusText = "已重启"; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or PlatformNotSupportedException or IOException or System.Runtime.InteropServices.COMException)
        {
            StatusText = "启动失败";
            Buffer.Append("\u001b[31m终端重启失败：" + error.Message + "\u001b[0m\r\n");
        }
    }
    private string RestartDirectory { get; set; } = Environment.CurrentDirectory;

    private void OnOutput(string text) => Dispatch(() =>
    {
        if (_disposed) return;
        var titleBefore = Buffer.Title;
        Buffer.Append(text);
        if (!string.Equals(titleBefore, Buffer.Title, StringComparison.Ordinal)) OnPropertyChanged(nameof(ShellTitle));
        // Answer DSR/DA queries (for example CSI 6n) so prompt frameworks and full-screen apps do not wait.
        var reply = Buffer.TakePendingResponse();
        if (reply.Length > 0) SendRaw(reply);
    });
    private void OnExited(int? code, string? diagnostic) => Dispatch(() =>
    {
        if (_disposed) return;
        IsRunning = false;
        StatusText = diagnostic is null ? $"已退出 {code?.ToString() ?? "?"}" : "连接错误";
        if (diagnostic is not null) Buffer.Append("\r\n\u001b[31m终端连接错误：" + diagnostic + "\u001b[0m\r\n");
    });
    private static void Dispatch(Action action) { if (Dispatcher.UIThread.CheckAccess()) action(); else Dispatcher.UIThread.Post(action); }

    [RelayCommand] public void SendInput()
    {
        if (!IsRunning) return;
        var command = InputText;
        SendRaw(command + "\r");
        History.Record(command);
        InputText = string.Empty;
    }
    [RelayCommand] public void Interrupt() => SendRaw("\u0003");
    [RelayCommand] public void RestartSession() => Restart();

    /// <summary>Paste into the draft only; the shell is never written to and nothing is auto-run.</summary>
    public void PasteToDraft(string? clipboardText)
    {
        var normalized = TerminalInputDraft.Normalize(clipboardText);
        if (normalized.Length == 0) return;
        InputText = TerminalInputDraft.Merge(InputText, normalized);
    }

    /// <summary>Step back through this tab's history, keeping the unsent draft for restoration.</summary>
    public bool HistoryPrevious()
    {
        var previous = History.Previous(InputText);
        if (previous is null) return false;
        InputText = previous;
        return true;
    }

    /// <summary>Step forward through this tab's history, returning to the original draft at the end.</summary>
    public bool HistoryNext()
    {
        var next = History.Next();
        if (next is null) return false;
        InputText = next;
        return true;
    }

    /// <summary>Run the pending query against the current bounded snapshot; returns the hit count.</summary>
    public int RunSearch(bool fromEnd = false)
    {
        var count = Search.Run(Snapshot, SearchQuery, SearchMatchCase, fromEnd);
        SearchStatus = Search.StatusText();
        return count;
    }

    /// <summary>Move to the next/previous hit. Re-resolves against the live snapshot when output changed.</summary>
    public TerminalSearchMatch? MoveSearch(int direction)
    {
        Search.EnsureCurrent(Snapshot);
        var match = Search.Move(direction);
        SearchStatus = Search.StatusText();
        return match;
    }

    /// <summary>Drop the per-tab selection and search highlights (used when a tab is reused or closed).</summary>
    public void ClearOutputInteraction()
    {
        OutputSelection.Clear();
        Search.Clear();
        SearchQuery = string.Empty;
        SearchStatus = string.Empty;
        IsSearchOpen = false;
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Search.Clear();
            SearchStatus = string.Empty;
            return;
        }
        RunSearch();
    }

    partial void OnSearchMatchCaseChanged(bool value)
    {
        if (SearchQuery.Length > 0) RunSearch();
    }
    public void SendRaw(string text)
    {
        try { _session.Write(text); }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            OnExited(null, error.Message);
        }
    }
    public void Resize(int columns, int rows)
    {
        if (_disposed || (columns == Buffer.Columns && rows == Buffer.Rows)) return;
        Buffer.Resize(columns, rows);
        try { _session.Resize(columns, rows); }
        catch (System.Runtime.InteropServices.COMException error) { OnExited(null, error.Message); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Output -= OnOutput; _session.Exited -= OnExited; _session.Dispose(); IsRunning = false;
        _snapshot = null;
        ClearOutputInteraction();
    }
}
