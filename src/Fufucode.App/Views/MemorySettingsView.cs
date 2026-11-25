using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

/// <summary>设置 → 其他 → 记忆。与设置页共用行卡样式；数据来自 <see cref="MemorySettingsViewModel"/>。</summary>
public sealed class MemorySettingsView : UserControl
{
    private MemorySettingsViewModel? _model;
    private bool _syncing;
    private readonly ToggleSwitch _enabled = new() { OnContent = "", OffContent = "" };
    private readonly Button _install = new() { Content = "安装" };
    private readonly Border _installRow;
    private readonly Border _enabledRow;
    private readonly TextBox _input = new() { MinWidth = 320, MaxLength = 500 };
    private readonly StackPanel _list = new() { Spacing = 0 };
    private readonly TextBlock _status = SettingsLayout.Note(string.Empty, warning: true);

    public MemorySettingsView()
    {
        AutomationProperties.SetAutomationId(_enabled, "memory-plugin-enabled");
        AutomationProperties.SetAutomationId(_install, "memory-plugin-install");
        AutomationProperties.SetAutomationId(_input, "memory-add-input");
        _status.IsVisible = false;

        _enabled.IsCheckedChanged += (_, _) =>
        {
            if (!_syncing) _model?.SetPluginEnabled(_enabled.IsChecked == true);
        };
        _install.Click += (_, _) => _model?.InstallPlugin();

        var add = new Button { Content = "添加" };
        AutomationProperties.SetAutomationId(add, "memory-add");
        add.Click += (_, _) => Submit();
        _input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Submit();
        };
        var addHost = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        addHost.Children.Add(_input);
        addHost.Children.Add(add);

        _installRow = SettingsLayout.Row("记忆插件", null, _install, "Brain");
        _enabledRow = SettingsLayout.ToggleRow("记忆插件", null, _enabled, "Brain");

        var open = new Button { Content = "打开文件夹" };
        AutomationProperties.SetAutomationId(open, "memory-open-folder");
        open.Click += (_, _) => _model?.OpenFolder();

        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(SettingsLayout.GroupHeader("记忆", first: true));
        body.Children.Add(SettingsLayout.Group(_installRow, _enabledRow));
        body.Children.Add(SettingsLayout.GroupHeader("条目"));
        body.Children.Add(SettingsLayout.Row("添加", null, addHost, "Add"));
        body.Children.Add(_status);
        body.Children.Add(_list);
        body.Children.Add(SettingsLayout.Row("MEMORY.md", null, open, "Document"));
        Content = body;

        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
            _model = DataContext as MemorySettingsViewModel;
            if (_model is not null) _model.PropertyChanged += OnModelChanged;
            Sync();
        };
        // 每次显示都重读文件：Agent 可能刚写入或删除了条目。
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && IsVisible) _model?.Refresh();
        };
    }

    private void Submit()
    {
        if (_model?.Add(_input.Text) == true) _input.Text = string.Empty;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args) => Sync();

    private void Sync()
    {
        if (_model is null) return;
        _syncing = true;
        try
        {
            var installed = _model.Plugin is { IsInstalled: true };
            _enabledRow.IsVisible = installed;
            _installRow.IsVisible = !installed;
            _enabled.IsChecked = _model.Plugin?.IsEnabled == true;

            _list.Children.Clear();
            if (_model.Entries.Count > 0)
            {
                var rows = new List<Control>();
                foreach (var entry in _model.Entries)
                {
                    var remove = new Button { Content = "删除" };
                    AutomationProperties.SetName(remove, "删除记忆：" + entry.Text);
                    remove.Click += (_, _) => _model?.Remove(entry);
                    rows.Add(SettingsLayout.Row(entry.Text, entry.Date?.ToString("yyyy-MM-dd"), remove));
                }
                _list.Children.Add(SettingsLayout.Group(rows.ToArray()));
            }

            var status = _model.Status ?? (_model.Plugin?.StatusMessage is { Length: > 0 } message && !installed ? message : null);
            _status.Text = status ?? string.Empty;
            _status.IsVisible = !string.IsNullOrEmpty(status);
            IsEnabled = _model.IsAvailable;
        }
        finally { _syncing = false; }
    }
}
