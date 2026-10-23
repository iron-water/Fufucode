using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using Fufucode.App.Features.Plugins;
using Fufucode.Core;
using Fufucode.App.Integration;

namespace Fufucode.App;

public partial class MainWindow
{
    private LocalPluginService _localPlugins = null!;
    private LocalPluginViewModel _localPluginViewModel = null!;

    internal LocalPluginService LocalPluginsForTesting => _localPlugins;
    internal LocalPluginViewModel LocalPluginViewModelForTesting => _localPluginViewModel;
    internal Views.ExtensionsPage ExtensionsPageForTesting => _extensionsPage;
    internal Task<ExtensionApplyFailure?> ApplyExtensionsForTestingAsync(string[] paths, CancellationToken token)
        => ApplyExtensionsAsync(paths, token);

    private void InitializeLocalPlugins()
    {
        // 与设置/资料库同一数据根；自测始终使用隔离根，绝不落到真实用户目录。
        var layout = new AppDataLayout(Program.SelfTestDataRoot ?? _options.CreateAppDataLayout().Root);
        var host = new PiLocalPluginHost(layout, () => _host,
            () => RealSessionBootstrap.ResolveRuntime(_options, required: false).Cli,
            () => _closed || _restarting || _switching,
            (expected, paths, cancellationToken) => ApplyExtensionsAsync(paths, cancellationToken, expected));
        _localPlugins = new LocalPluginService(layout, host);
        _localPluginViewModel = new LocalPluginViewModel(_localPlugins,
            new LocalPluginStoragePicker(this));
        _extensionsPage.AttachLocalPlugins(_localPluginViewModel);
    }

    // 新的真实窗口即使还在bootstrap阶段也会发连接事件；此时插件组合尚未完成。
    private void NotifyLocalPluginHostChanged() => _localPluginViewModel?.NotifyHostChanged();

    private sealed class LocalPluginStoragePicker(Control owner) : ILocalPluginPicker
    {
        public async Task<string?> PickPluginDirectoryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = TopLevel.GetTopLevel(owner)?.StorageProvider
                ?? throw new InvalidOperationException("请先打开扩展页再选择目录。");
            if (!provider.CanPickFolder) throw new InvalidOperationException("当前窗口不支持目录选择。");
            var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择本地插件目录", AllowMultiple = false,
            });
            cancellationToken.ThrowIfCancellationRequested();
            return result.Count == 0 ? null : result[0].TryGetLocalPath();
        }

        public async Task<string?> PickEntryFileAsync(string rootDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = TopLevel.GetTopLevel(owner)?.StorageProvider
                ?? throw new InvalidOperationException("请先打开扩展页再选择入口。");
            if (!provider.CanOpen) throw new InvalidOperationException("当前窗口不支持文件选择。");
            var start = await provider.TryGetFolderFromPathAsync(rootDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择插件包内的入口", AllowMultiple = false, SuggestedStartLocation = start,
            });
            cancellationToken.ThrowIfCancellationRequested();
            // 越界/链接入口由服务的正式预览校验拒绝；选择器不绕过描述文件及路径门禁。
            return result.Count == 0 ? null : result[0].TryGetLocalPath();
        }
    }
}
