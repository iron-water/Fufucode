using System;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Features.TerminalExperience;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.ViewModels;

/// <summary>Static project-owned terminal container for H-S09; agent tool jobs have no ownership claim here.</summary>
public sealed partial class TerminalDockViewModel : ViewModelBase, IDisposable
{
    private readonly bool _useMock;
    private readonly Func<ITerminalSession> _factory;
    private int _nextNumber;
    private bool _disposed;

    public TerminalDockViewModel(bool useMock = false, string? workspacePath = null, Func<ITerminalSession>? sessionFactory = null)
    {
        _useMock = useMock;
        WorkspacePath = Directory.Exists(workspacePath) ? workspacePath! : Environment.CurrentDirectory;
        _factory = sessionFactory ?? (() => _useMock ? new TerminalMockSession() : new ConPtyTerminalSession());
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
        var tab = new TerminalTabViewModel(++_nextNumber, _useMock, _factory());
        Tabs.Add(tab); SelectedTab = tab; IsVisible = true;
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
    public TerminalTabViewModel(int number, bool isMock, ITerminalSession session)
    {
        TerminalSessionId = Guid.NewGuid().ToString("N");
        Title = $"终端 {number}";
        IsMock = isMock;
        _session = session;
        _session.Output += OnOutput;
        _session.Exited += OnExited;
        Buffer.Changed += () => _snapshot = null;
    }

    public string TerminalSessionId { get; }
    public string Title { get; }
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
    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _searchMatchCase;
    [ObservableProperty] private string _searchStatus = string.Empty;
    [ObservableProperty] private bool _isSearchOpen;

    public void Start(string workspace)
    {
        try { _session.Start(workspace, Buffer.Columns, Buffer.Rows); IsRunning = _session.IsRunning; StatusText = IsMock ? "演示" : "cmd"; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or PlatformNotSupportedException or IOException or System.Runtime.InteropServices.COMException)
        {
            StatusText = "启动失败";
            Buffer.Append("\u001b[31m终端启动失败：" + error.Message + "\u001b[0m\r\n");
        }
    }
    private void OnOutput(string text) => Dispatch(() =>
    {
        if (_disposed) return;
        Buffer.Append(text);
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
