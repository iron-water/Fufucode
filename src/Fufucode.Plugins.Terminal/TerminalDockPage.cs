using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Fufucode.App.Features.Plugins;
using Fufucode.Plugins.Terminal.Infrastructure;
using Fufucode.Plugins.Terminal.ViewModels;
using Fufucode.Plugins.Terminal.Views;

namespace Fufucode.Plugins.Terminal;

/// <summary>
/// 插件对宿主的唯一页面入口：把现有终端视图包装成 <see cref="IPluginWorkspacePage"/>，
/// 并按项目路径保留各自的终端页签状态。宿主不引用本程序集的任何类型。
/// </summary>
public sealed class TerminalDockPage : IPluginWorkspacePage
{
    private readonly TerminalDockView _view = new();
    private readonly Dictionary<string, TerminalDockViewModel> _projects = new(StringComparer.OrdinalIgnoreCase);
    private TerminalDockViewModel? _current;
    private string? _projectPath;
    private bool _useMock;
    private bool _disposed;

    private readonly TerminalSettingsStore? _settingsStore;
    private TerminalSettings _settings = TerminalSettings.Default;

    /// <summary>插件页面由宿主创建；设置文件位于插件自己的数据目录。</summary>
    public TerminalDockPage(string? dataRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            _settingsStore = new TerminalSettingsStore(dataRoot);
            _settings = _settingsStore.Load();
        }
        _view.ApplyDisplaySettings(_settings);
        _view.SettingsChanged += ApplySettings;
    }

    /// <summary>当前设置；页面据此构造新会话并应用显示选项。</summary>
    public TerminalSettings Settings => _settings;

    /// <summary>替换设置并持久化；新终端生效，现有终端保留自己的设置直到关闭。</summary>
    public void ApplySettings(TerminalSettings settings)
    {
        _settings = settings.Normalize();
        _settingsStore?.Save(_settings);
        _view.ApplyDisplaySettings(_settings);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public string PageId => "terminal";
    public Control View => _view;
    public event EventHandler? StateChanged;

    public bool IsVisible => _current?.IsVisible == true;
    public bool HasTabs => _current?.HasTabs == true;
    public bool HasRunningTab => _current?.SelectedTab?.IsRunning == true;
    public bool HasOutputSelection => _current?.HasOutputSelection == true;
    public Control? CommandInput => _view.CommandInputControl;

    public Func<string, KeyEventArgs, bool>? ShortcutMatches
    {
        get => _view.ShortcutMatches;
        set => _view.ShortcutMatches = value;
    }

    public void SetProject(string? projectPath, bool useMock)
    {
        if (_disposed) return;
        var key = string.IsNullOrWhiteSpace(projectPath) ? string.Empty : Path.GetFullPath(projectPath);
        if (string.Equals(key, _projectPath, StringComparison.OrdinalIgnoreCase) && _current is not null) return;
        var wasVisible = _current?.IsVisible == true;
        if (_current is not null) _current.PropertyChanged -= OnCurrentChanged;
        if (!_projects.TryGetValue(key, out var next))
        {
            next = new TerminalDockViewModel(useMock, string.IsNullOrEmpty(key) ? null : key, settings: _settings);
            _projects[key] = next;
        }
        _current = next;
        _projectPath = key;
        _useMock = useMock;
        _view.DataContext = next;
        next.PropertyChanged += OnCurrentChanged;
        if (wasVisible) next.Show();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Show()
    {
        if (_current is null) SetProject(Environment.CurrentDirectory, _useMock);
        _current?.Show();
    }

    public void Hide() => _current?.Hide();

    public void UpdateShortcutLabels(Func<string, string> binding) => _view.UpdateShortcutLabels(binding);

    public Task CopySelectionAsync() => _view.CopySelectionAsync();
    public Task PasteToDraftAsync() => _view.PasteToDraftAsync();
    public void FindOutput() => _view.FindOutput();
    public void ReturnToBottom() => _view.ReturnToBottom();
    public Task CopyOrInterruptAsync() => _view.CopyOrInterruptAsync();
    public void Interrupt() => _current?.SelectedTab?.Interrupt();
    public void CreateTerminal() => _current?.CreateTerminalCommand.Execute(null);
    public Task CloseSelectedAsync() => _view.CloseSelectedAsync();

    private void OnCurrentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalDockViewModel.IsVisible)
            or nameof(TerminalDockViewModel.SelectedTab)
            or nameof(TerminalDockViewModel.HasOutputSelection))
            StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_current is not null) _current.PropertyChanged -= OnCurrentChanged;
        _current = null;
        _view.SettingsChanged -= ApplySettings;
        _view.DataContext = null;
        foreach (var project in _projects.Values) project.Dispose();
        _projects.Clear();
    }
}
