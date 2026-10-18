using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fufucode.App.Abstractions.Automation;

namespace Fufucode.App.Features.Plugins;

/// <summary>
/// 自动开发页的宿主容器：插件安装并启用时挂载它贡献的视图；否则显示真实安装指引。
/// 页面 ID 与停靠状态由宿主保留，插件激活/停用只替换内容，不影响页签身份。
/// </summary>
public sealed class AutomationPageHost : ContentControl, IDisposable
{
    private Control? _view;
    private IAutomationPageController? _controller;

    public AutomationPageHost()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        ShowPlaceholder();
    }

    /// <summary>当前挂载的插件页面控制器；未安装/未启用时为 null。</summary>
    public IAutomationPageController? Controller => _controller;

    /// <summary>插件页面报告已连接数据库（与项目上下文一起回传，宿主只保存自己的项目）。</summary>
    public event Action<string, string?>? DatabaseConnected;

    public void Attach(Control view)
    {
        ArgumentNullException.ThrowIfNull(view);
        DetachCurrent();
        _view = view;
        Content = view;
        _controller = view.DataContext as IAutomationPageController;
        if (_controller is not null) _controller.DatabaseConnected += OnDatabaseConnected;
    }

    /// <summary>卸载插件页面并释放视图模型；容器本身保留给停靠布局。</summary>
    public void Detach()
    {
        DetachCurrent();
        _controller = null;
        ShowPlaceholder();
    }

    public void SetProjectContext(string? projectPath) => _controller?.SetProjectContext(projectPath);

    public void Disconnect() => _controller?.Disconnect();

    public Task ConnectAsync(string databasePath) =>
        _controller?.ConnectAsync(databasePath) ?? Task.CompletedTask;

    private void DetachCurrent()
    {
        if (_controller is not null) _controller.DatabaseConnected -= OnDatabaseConnected;
        _controller = null;
        if (_view is not null) Content = null;
        _view = null;
    }

    private void OnDatabaseConnected(string database, string? context) =>
        DatabaseConnected?.Invoke(database, context);

    private void ShowPlaceholder()
    {
        Content = new Border
        {
            Padding = new Thickness(24),
            Child = new TextBlock
            {
                Text = "自动开发工作台未安装。\n请在插件市场中安装并启用“自动开发工作台”（automation.auto-dev）。",
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    public void Dispose() => DetachCurrent();
}
