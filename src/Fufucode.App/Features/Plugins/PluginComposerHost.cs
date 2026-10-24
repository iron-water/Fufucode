using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Features.Plugins;

/// <summary>宿主给可选插件提供输入栏工具的受控接缝。</summary>
public sealed class PluginComposerHost
{
    private readonly MainWindow _window;

    public PluginComposerHost(MainWindow window) => _window = window;

    public void Populate(MenuFlyout menu, MainViewModel vm)
    {
        var manager = _window.PluginManager;
        if (manager is null) return;
        foreach (var pluginId in manager.GetActivePluginIds())
        {
            if (!manager.TryGetComposerContribution(pluginId, out var contribution)) continue;
            var item = new MenuItem { Header = contribution.MenuText };
            item.Click += async (_, _) =>
            {
                var context = new PluginComposerContext(
                    _window,
                    vm.Input.SupportsImages,
                    path => _window.ChatPageForPlugins.AttachFilesAsync([path]),
                    ShowDialogAsync,
                    message => _window.ChatPageForPlugins.ReportActionError(message));
                await contribution.ExecuteAsync(context);
            };
            menu.Items.Add(item);
        }
    }

    private async Task<bool> ShowDialogAsync(object dialog)
    {
        if (dialog is not Window window) return false;
        return await SkinDialog.ShowAsync<bool>(window, _window);
    }
}
