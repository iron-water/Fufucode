using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App.Views;

/// <summary>设置 → 高级。与设置页共用行卡样式；数据来自 <see cref="AdvancedSettingsViewModel"/>。</summary>
public sealed class AdvancedSettingsView : UserControl
{
    private AdvancedSettingsViewModel? _model;
    private bool _syncing;
    private readonly ComboBox _shell = new() { MinWidth = 220 };
    private readonly ToggleSwitch _profile = new() { OnContent = "", OffContent = "" };
    private readonly NumericUpDown _timeout = new()
    {
        Minimum = 1, Maximum = FumoSettings.MaxCommandTimeoutSeconds, Increment = 30, Width = 130, FormatString = "0",
    };
    private readonly TextBlock _effective;
    private readonly TextBlock _status = SettingsLayout.Note(string.Empty, warning: true);

    public AdvancedSettingsView()
    {
        AutomationProperties.SetAutomationId(_shell, "advanced-fumo-shell");
        AutomationProperties.SetAutomationId(_profile, "advanced-fumo-shell-profile");
        AutomationProperties.SetAutomationId(_timeout, "advanced-fumo-command-timeout");
        _status.IsVisible = false;

        _shell.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _shell.SelectedItem is AdvancedSettingsViewModel.ShellOption option) _model?.SetShell(option.Id);
        };
        _profile.IsCheckedChanged += (_, _) =>
        {
            if (!_syncing) _model?.SetLoadProfile(_profile.IsChecked == true);
        };
        _timeout.ValueChanged += (_, args) =>
        {
            if (!_syncing && args.NewValue is { } value) _model?.SetCommandTimeout((int)value);
        };
        var reset = new Button { Content = "恢复默认" };
        AutomationProperties.SetAutomationId(reset, "advanced-fumo-reset");
        reset.Click += (_, _) => _model?.Reset();

        // 行说明位显示实际生效的可执行文件：选择与实际不一致（未安装、后备）时一眼可见。
        var shellRow = SettingsLayout.Row("命令解释器", " ", _shell, "Terminal");
        _effective = shellRow.GetLogicalDescendants().OfType<TextBlock>().First(block => block.Classes.Contains("settingsRowDesc"));
        AutomationProperties.SetAutomationId(_effective, "advanced-fumo-shell-effective");

        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(SettingsLayout.GroupHeader("FUMO 内核", first: true));
        body.Children.Add(SettingsLayout.Group(
            shellRow,
            SettingsLayout.ToggleRow("加载 shell 配置文件", null, _profile, "Code"),
            SettingsLayout.Row("命令默认超时（秒）", null, _timeout, "History")));
        body.Children.Add(_status);
        body.Children.Add(SettingsLayout.Row("恢复 FUMO 默认设置", null, reset, "ArrowUndo"));
        Content = body;

        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
            _model = DataContext as AdvancedSettingsViewModel;
            if (_model is not null) _model.PropertyChanged += OnModelChanged;
            Sync();
        };
        // 每次显示时重新探测解释器：用户可能刚安装或卸载了 PowerShell 7。
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && IsVisible) _model?.Refresh();
        };
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args) => Sync();

    private void Sync()
    {
        if (_model is null) return;
        _syncing = true;
        try
        {
            if (!ReferenceEquals(_shell.ItemsSource, _model.ShellOptions)) _shell.ItemsSource = _model.ShellOptions;
            _shell.SelectedItem = _model.ShellOptions.FirstOrDefault(option =>
                string.Equals(option.Id, _model.SelectedShellId, StringComparison.OrdinalIgnoreCase));
            var current = _model.Current;
            _profile.IsChecked = current.LoadShellProfile;
            _timeout.Value = current.CommandTimeoutSeconds;
            _effective.Text = _model.EffectiveShellText;
            _status.Text = _model.Status ?? string.Empty;
            _status.IsVisible = _model.HasError;
            IsEnabled = _model.IsAvailable;
        }
        finally { _syncing = false; }
    }
}
