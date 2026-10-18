using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fufucode.App.Views;

namespace Fufucode.App.Features.Storage;

/// <summary>
/// 存储与数据管理页（任务书 M2）。沿用设置页的分组卡 + 行式条目语言，
/// 状态与行为都在本控件 + <see cref="StorageManagerViewModel"/> 内，不触碰共享外壳：
/// 调用方只需提供清单（数据根）与可选的「打开目录」回调。
/// </summary>
public sealed class StoragePage : UserControl
{
    private readonly StorageManagerViewModel _vm;
    private StackPanel? _sections;
    private Button? _cleanButton;
    private bool _siteDataConfirmed;

    public StoragePage(StorageManagerViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Build();
        _vm.ViewChanged += () => Dispatcher.UIThread.Post(RenderSections);
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(StorageManagerViewModel.HasPreview)
                or nameof(StorageManagerViewModel.HasResult)
                or nameof(StorageManagerViewModel.HasMeasurement)
                or nameof(StorageManagerViewModel.HasFailures)
                or nameof(StorageManagerViewModel.HasSkipped))
            {
                Dispatcher.UIThread.Post(RenderSections);
            }
        };
        RenderSections();
    }

    public StorageManagerViewModel ViewModel => _vm;

    /// <summary>测试钩子：站点数据确认状态。</summary>
    internal bool SiteDataConfirmedForTesting => _siteDataConfirmed;

    private void Build()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };

        var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
        var title = new TextBlock { Text = "存储与数据" };
        title.Classes.Add("settingsPageTitle");
        header.Children.Add(title);
        var help = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 0) };
        help.Classes.Add("settingsRowDesc");
        help.Text = "查看应用数据各目录的实际占用，按保留策略生成清理预览并记录实际结果。计量可取消；" +
            "默认只清理可重建缓存与过期日志，不搬迁数据根、不动会话、配置、认证与资料。";
        header.Children.Add(help);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        actions.Children.Add(BuildButton("重新计量", () => _ = _vm.MeasureAsync(), nameof(StorageManagerViewModel.CanMeasure), icon: "ArrowUndo"));
        actions.Children.Add(BuildButton("取消计量", _vm.CancelMeasure, nameof(StorageManagerViewModel.CanCancelMeasure)));

        var retentionLabel = new TextBlock { Text = "日志保留（天）", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        retentionLabel.Classes.Add("settingsRowDesc");
        actions.Children.Add(retentionLabel);
        var retention = new NumericUpDown
        {
            Minimum = StorageRetentionPolicy.MinLogRetentionDays,
            Maximum = StorageRetentionPolicy.MaxLogRetentionDays,
            FormatString = "0",
            Width = 104,
            VerticalAlignment = VerticalAlignment.Center,
        };
        retention.Bind(NumericUpDown.ValueProperty,
            new Binding(nameof(StorageManagerViewModel.LogRetentionDays)) { Source = _vm, Mode = BindingMode.TwoWay });
        actions.Children.Add(retention);

        actions.Children.Add(BuildButton("预览清理", () => _ = _vm.PreviewCleanupAsync(), nameof(StorageManagerViewModel.CanPreview), icon: "Search"));
        _cleanButton = BuildButton("执行清理", OnCleanClicked, null, icon: "Wrench");
        actions.Children.Add(_cleanButton);
        actions.Children.Add(BuildButton("取消清理", _vm.CancelClean, nameof(StorageManagerViewModel.CanCancelClean)));

        Grid.SetRow(actions, 1);
        root.Children.Add(actions);

        var status = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
        var statusText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        statusText.Bind(TextBlock.TextProperty, new Binding(nameof(StorageManagerViewModel.Status)) { Source = _vm });
        status.Children.Add(statusText);
        var measureText = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "meta" } };
        measureText.Bind(TextBlock.TextProperty, new Binding(nameof(StorageManagerViewModel.MeasureStatus)) { Source = _vm });
        status.Children.Add(measureText);
        Grid.SetRow(status, 2);
        root.Children.Add(status);

        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _sections = new StackPanel { Spacing = 0 };
        scroller.Content = _sections;
        Grid.SetRow(scroller, 3);
        root.Children.Add(scroller);

        Content = root;
    }

    private Button BuildButton(string text, Action action, string? canExecuteProperty, string? icon = null)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        if (canExecuteProperty is not null)
            button.Bind(IsEnabledProperty, new Binding(canExecuteProperty) { Source = _vm });
        if (icon is not null) button.Tag = icon;
        return button;
    }

    private void OnCleanClicked()
    {
        if (!CanExecuteClean()) return;
        _ = _vm.CleanAsync();
    }

    private bool CanExecuteClean()
    {
        if (!_vm.CanClean) return false;
        var siteData = _vm.SelectedScopes().Contains(StorageCleanupScope.BrowserSiteData) &&
            _vm.Targets.Any(target => target.ScopeText.Contains("站点", StringComparison.Ordinal));
        return !siteData || _siteDataConfirmed;
    }

    private void UpdateCleanButtonState()
    {
        if (_cleanButton is null) return;
        // 站点数据范围必须显式勾选确认行，避免一次点击就清掉登录状态。
        _cleanButton.IsEnabled = CanExecuteClean();
    }

    private void RenderSections()
    {
        if (_sections is null) return;
        _sections.Children.Clear();

        _sections.Children.Add(SettingsLayout.GroupHeader("占用"));
        _sections.Children.Add(SettingsLayout.Group(_vm.Areas.Select(BuildAreaRow).ToArray()));
        if (_vm.HasMeasurement) _sections.Children.Add(SettingsLayout.Note(_vm.MeasuredText));

        _sections.Children.Add(SettingsLayout.GroupHeader("清理范围"));
        _sections.Children.Add(SettingsLayout.Group(_vm.Scopes.Select(BuildScopeRow).ToArray()));
        _sections.Children.Add(SettingsLayout.Note(_vm.RetentionText));

        if (_vm.HasPreview)
        {
            _sections.Children.Add(SettingsLayout.GroupHeader("清理预览（执行前确认）"));
            _sections.Children.Add(SettingsLayout.Group(_vm.Targets.Select(BuildTargetRow).ToArray()));
            _sections.Children.Add(SettingsLayout.Note(_vm.PreviewSummary));

            if (_vm.SelectedScopes().Contains(StorageCleanupScope.BrowserSiteData))
            {
                var confirm = new CheckBox
                {
                    Content = "我已了解：清理浏览器登录/站点数据后相关网站需要重新登录、离线内容需重新下载",
                    IsChecked = _siteDataConfirmed,
                };
                confirm.IsCheckedChanged += (_, _) =>
                {
                    _siteDataConfirmed = confirm.IsChecked == true;
                    UpdateCleanButtonState();
                };
                _sections.Children.Add(SettingsLayout.Row("确认清理登录数据", "未勾选时「执行清理」保持禁用", confirm, "Warning"));
            }
        }

        if (_vm.HasPreviewWarnings) _sections.Children.Add(SettingsLayout.Note(_vm.PreviewWarnings, warning: true));

        if (_vm.HasPreview && _vm.HasSkipped)
        {
            _sections.Children.Add(SettingsLayout.GroupHeader("跳过（未删除）"));
            _sections.Children.Add(SettingsLayout.Group(_vm.Skipped.Select(BuildSkipRow).ToArray()));
        }

        if (_vm.HasResult)
        {
            _sections.Children.Add(SettingsLayout.GroupHeader("清理结果"));
            var rows = new List<Control> { SettingsLayout.Row("结果", _vm.ResultText, null, "CheckmarkCircle") };
            rows.AddRange(_vm.Failures.Select(failure =>
                (Control)SettingsLayout.Row("失败（已保留原文件）", failure.Path + "\n" + failure.Reason, null, "Warning")));
            rows.AddRange(_vm.Skipped.Select(skip =>
                (Control)SettingsLayout.Row("跳过（未删除）", skip.Path + "\n" + skip.Reason, null, "Warning")));
            _sections.Children.Add(SettingsLayout.Group(rows.ToArray()));
        }

        _sections.Children.Add(SettingsLayout.Note(
            "默认范围只有「可重建缓存」与「过期日志」；浏览器登录数据与 Service Worker 离线存储、会话、配置、认证、资料库、项目源码与工作树都不在自动清理内。" +
            "发现、预览与执行都不跟随 junction/符号链接，也不删除链接目标；预览后发生变化的目标会保留并要求重新预览，不会把新内容并入删除范围。" +
            "本页不移动或搬迁任何数据；被占用的文件会跳过并如实记录。"));
        UpdateCleanButtonState();
    }

    private Control BuildAreaRow(StorageAreaRow area)
    {
        var open = new Button { Content = "打开", Classes = { "subtle" }, Padding = new Thickness(8, 2) };
        open.IsEnabled = _vm.CanOpenFolders;
        open.Click += (_, _) => _vm.OpenFolder(area.Path);
        open.Tag = area.Path;

        var line = SettingsLayout.Row(area.Title, area.UsageText + " · " + area.PolicyText, open, "Folder");
        if (line.Child is Grid grid && grid.Children.OfType<StackPanel>().FirstOrDefault() is { } textPanel)
        {
            foreach (var text in new[] { area.Path, area.Description })
            {
                var detail = new TextBlock { Text = text };
                detail.Classes.Add("settingsRowDesc");
                ToolTip.SetTip(detail, text);
                textPanel.Children.Add(detail);
            }
        }
        line.Tag = area;
        return line;
    }

    private Control BuildScopeRow(StorageScopeOption option)
    {
        var toggle = new CheckBox { IsChecked = option.IsEnabled, VerticalAlignment = VerticalAlignment.Center };
        toggle.IsCheckedChanged += (_, _) => option.IsEnabled = toggle.IsChecked == true;
        toggle.Tag = option.Scope;

        var description = option.Description + (option.HasWarning ? "\n" + option.Warning : string.Empty);
        return SettingsLayout.Row(option.Title, description, toggle, option.HasWarning ? "Warning" : "Info");
    }

    private Control BuildTargetRow(StorageTargetRow target)
    {
        var line = SettingsLayout.Row(target.ScopeText, target.SizeText + " · " + target.DetailText, null, "Document");
        if (line.Child is Grid grid && grid.Children.OfType<StackPanel>().FirstOrDefault() is { } textPanel)
        {
            var detail = new TextBlock { Text = target.Path };
            detail.Classes.Add("settingsRowDesc");
            ToolTip.SetTip(detail, target.Path);
            textPanel.Children.Add(detail);
        }
        return line;
    }

    private static Control BuildSkipRow(StorageSkipRow skip)
    {
        var line = SettingsLayout.Row(skip.KindText, skip.Reason, null, "Warning");
        if (line.Child is Grid grid && grid.Children.OfType<StackPanel>().FirstOrDefault() is { } textPanel)
        {
            var detail = new TextBlock { Text = skip.Path };
            detail.Classes.Add("settingsRowDesc");
            ToolTip.SetTip(detail, skip.Path);
            textPanel.Children.Add(detail);
        }
        return line;
    }
}
