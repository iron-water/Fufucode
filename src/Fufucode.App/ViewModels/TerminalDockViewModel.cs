using System;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    public bool HasTabs => Tabs.Count > 0;

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
    public TerminalTabViewModel(int number, bool isMock, ITerminalSession session)
    {
        TerminalSessionId = Guid.NewGuid().ToString("N");
        Title = $"终端 {number}";
        IsMock = isMock;
        _session = session;
        _session.Output += OnOutput;
        _session.Exited += OnExited;
    }

    public string TerminalSessionId { get; }
    public string Title { get; }
    public bool IsMock { get; }
    public TerminalBuffer Buffer { get; } = new();
    public double ScrollOffsetY { get; set; }
    public bool FollowOutput { get; set; } = true;
    [ObservableProperty] private string _statusText = "启动中";
    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private bool _isRunning;

    public void Start(string workspace)
    {
        try { _session.Start(workspace, Buffer.Columns, Buffer.Rows); IsRunning = _session.IsRunning; StatusText = IsMock ? "演示" : "cmd"; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or PlatformNotSupportedException or IOException or System.Runtime.InteropServices.COMException)
        {
            StatusText = "启动失败";
            Buffer.Append("\u001b[31m终端启动失败：" + error.Message + "\u001b[0m\r\n");
        }
    }
    private void OnOutput(string text) => Dispatch(() => { if (!_disposed) Buffer.Append(text); });
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
        SendRaw(InputText + "\r"); InputText = string.Empty;
    }
    [RelayCommand] public void Interrupt() => SendRaw("\u0003");
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
    }
}
