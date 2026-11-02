using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Fufucode.App.Abstractions.Graph;
using Fufucode.App.Views;
using Fufucode.Core;

namespace Fufucode.App.Features.SettingsExperience;

public sealed class GraphSettingsView : UserControl
{
    private readonly IGraphProjectService _service;
    private readonly ToggleSwitch _enabled = new();
    private readonly NumericUpDown _budget = Number(0, 1440);
    private readonly NumericUpDown _concurrency = Number(1, 16);
    private readonly NumericUpDown _quota = Number(0, 100);
    private readonly NumericUpDown _depth = Number(0, 20);
    private readonly ComboBox _model = new() { MinWidth = 240, MaxWidth = 340 };
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _save = new() { Content = "保存并应用" };
    public event Func<GraphOptions, Task>? Applied;
    public event Action? ModelsRequested;
    private sealed record ModelOption(string Id, string Label) { public override string ToString() => Label; }

    public GraphSettingsView(IGraphProjectService service, AppSettingsStore settings)
    {
        _service = service;
        Classes.Add("autodevSettings");
        var rows = new StackPanel { Spacing = 0 };
        rows.Children.Add(SettingsLayout.GroupHeader("工作模式", true));
        rows.Children.Add(SettingsLayout.Group(SettingsLayout.ToggleRow("启用 Graph 与自主开发", null, _enabled, "Automation")));
        rows.Children.Add(SettingsLayout.GroupHeader("项目执行"));
        rows.Children.Add(SettingsLayout.Group(
            SettingsLayout.Row("运行预算（分钟）", null, _budget, "History"),
            SettingsLayout.Row("并行运行上限", null, _concurrency, "Automation"),
            SettingsLayout.Row("计算使用比例（%）", null, _quota, "Automation"),
            SettingsLayout.Row("执行模型", null, _model, "Brain")));
        var openModels = new Button { Content = "模型与提供商" };
        openModels.Click += (_, _) => ModelsRequested?.Invoke();
        rows.Children.Add(SettingsLayout.Row("共享模型配置", null, openModels, "Settings"));
        rows.Children.Add(SettingsLayout.GroupHeader("项目结构"));
        rows.Children.Add(SettingsLayout.Group(SettingsLayout.Row("目录扫描深度", null, _depth, "Folder")));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 20, 0, 0) };
        _save.Click += async (_, _) => await SaveAsync();
        buttons.Children.Add(_save);
        rows.Children.Add(buttons);
        _status.Classes.Add("settingsRowDesc");
        _status.Margin = new Thickness(0, 10, 0, 0);
        rows.Children.Add(_status);
        Content = rows;
        var choices = new List<ModelOption> { new("", "跟随共享模型设置") };
        var configured = settings.Current.Endpoint?.AvailableModels ?? [];
        foreach (var id in configured.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
            choices.Add(new(id, id));
        var current = _service.ReadOptions();
        if (!string.IsNullOrWhiteSpace(current.Model) && choices.All(item => item.Id != current.Model))
            choices.Add(new(current.Model, current.Model));
        _model.ItemsSource = choices;
        _model.SelectedItem = choices.First(item => item.Id == current.Model);
        _enabled.IsChecked = settings.Current.GraphEnabled;
        _budget.Value = current.BudgetMinutes;
        _concurrency.Value = current.Concurrency;
        _quota.Value = (decimal)(current.ComputeQuota * 100);
        _depth.Value = current.ScanDepth;
        AutomationProperties.SetAutomationId(_enabled, "graph-setting-enabled");
        AutomationProperties.SetAutomationId(_save, "graph-settings-apply");
        AutomationProperties.SetAutomationId(_budget, "graph-setting-budget");
        AutomationProperties.SetAutomationId(_model, "graph-setting-model");
    }

    private async Task SaveAsync()
    {
        _save.IsEnabled = false;
        try
        {
            var options = _service.ReadOptions() with
            {
                Enabled = _enabled.IsChecked == true,
                BudgetMinutes = (int)(_budget.Value ?? 60),
                Concurrency = (int)(_concurrency.Value ?? 1),
                ComputeQuota = (double)(_quota.Value ?? 100) / 100,
                ScanDepth = (int)(_depth.Value ?? 6),
                Model = (_model.SelectedItem as ModelOption)?.Id ?? ""
            };
            if (Applied is { } applied) await applied(options);
            else await _service.SaveOptionsAsync(options);
            _status.Text = "已保存并应用";
        }
        catch (Exception error) { _status.Text = "应用失败：" + error.Message; }
        finally { _save.IsEnabled = true; }
    }

    private static NumericUpDown Number(decimal min, decimal max) =>
        new() { Minimum = min, Maximum = max, Increment = 1, Width = 130 };
}
