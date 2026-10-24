using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Fufucode.App.Features.FilePreview;

public enum FilePreviewPluginMediaKind
{
    Document,
    Audio,
    Video,
}

/// <summary>复杂文件预览插件的声明；插件本身不得绕过项目路径和只读宿主。</summary>
public sealed record FilePreviewPluginDescriptor(
    string Id,
    string DisplayName,
    FilePreviewPluginMediaKind MediaKind,
    IReadOnlySet<string> Extensions,
    bool IsInstalled,
    bool IsEnabled,
    string? UnavailableReason = null);

/// <summary>PDF/DOCX/音视频等可选 provider 的最小契约。</summary>
public interface IFilePreviewPlugin : IFilePreviewProvider
{
    FilePreviewPluginDescriptor Descriptor { get; }
    bool CanHandle(FilePreviewRequest request);
}

/// <summary>插件管理器只负责声明、启停和 provider 注册，不加载文件内容到网络。</summary>
public sealed class FilePreviewPluginRegistry : IDisposable
{
    private readonly List<IFilePreviewPlugin> _plugins = [];

    public IReadOnlyList<IFilePreviewPlugin> Plugins => _plugins;

    public void Register(IFilePreviewPlugin plugin)
    {
        if (_plugins.Any(existing => string.Equals(existing.Descriptor.Id, plugin.Descriptor.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"文件预览插件 ID 已注册：{plugin.Descriptor.Id}");
        _plugins.Add(plugin);
    }

    public bool Unregister(string id)
    {
        var plugin = _plugins.FirstOrDefault(item => string.Equals(item.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase));
        if (plugin is null) return false;
        _plugins.Remove(plugin);
        plugin.Dispose();
        return true;
    }

    public IFilePreviewPlugin? Resolve(FilePreviewRequest request) =>
        _plugins.FirstOrDefault(plugin => plugin.CanHandle(request));

    public void Dispose()
    {
        foreach (var plugin in _plugins) plugin.Dispose();
        _plugins.Clear();
    }
}

/// <summary>未安装插件时的可读降级 provider，避免空白页或异常弹窗。</summary>
public sealed class UnsupportedFilePreviewPlugin : IFilePreviewPlugin
{
    public UnsupportedFilePreviewPlugin(FilePreviewPluginDescriptor descriptor) => Descriptor = descriptor;
    public FilePreviewPluginDescriptor Descriptor { get; }
    public FilePreviewKind Kind => FilePreviewKind.Error;
    public bool CanHandle(FilePreviewRequest request) => Descriptor.Extensions.Contains(Path.GetExtension(request.Path).TrimStart('.'));
    public bool CanPreview(FilePreviewRequest request) => CanHandle(request);
    public Task<FilePreviewResult> LoadAsync(FilePreviewRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(FilePreviewResult.Failure(request.Path,
            Descriptor.UnavailableReason ?? $"未安装“{Descriptor.DisplayName}”预览插件。"));
    public Control CreateView(FilePreviewResult result) => new Border
    {
        Padding = new Avalonia.Thickness(16),
        Child = new TextBlock { Text = result.Error ?? "此文件暂时无法预览。", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
    };
    public void Dispose() { }
}

public static class BuiltInComplexPreviewPlugins
{
    public static IReadOnlyList<IFilePreviewPlugin> CreateUnavailable() =>
    [
        new UnsupportedFilePreviewPlugin(new("document.pdf", "PDF 文档", FilePreviewPluginMediaKind.Document,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pdf" }, false, false, "PDF 预览插件尚未安装；可选择系统打开。")),
        new UnsupportedFilePreviewPlugin(new("document.office", "Office 文档", FilePreviewPluginMediaKind.Document,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "doc", "docx", "xls", "xlsx", "ppt", "pptx" }, false, false, "Office 预览插件尚未安装；可选择系统打开。")),
        new UnsupportedFilePreviewPlugin(new("media.player", "音视频", FilePreviewPluginMediaKind.Audio,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mp3", "wav", "flac", "ogg", "mp4", "webm", "mkv", "avi", "mov" }, false, false, "音视频预览插件尚未安装；可选择系统打开。")),
    ];
}
