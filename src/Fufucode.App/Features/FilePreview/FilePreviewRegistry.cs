using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.App.Features.FilePreview;

public sealed class FilePreviewRegistry : IDisposable
{
    private readonly IReadOnlyList<IFilePreviewProvider> _providers;
    private readonly FilePreviewPluginRegistry _plugins;

    public FilePreviewRegistry(IEnumerable<IFilePreviewProvider> providers, FilePreviewPluginRegistry? plugins = null)
    {
        _providers = providers.ToArray();
        _plugins = plugins ?? new FilePreviewPluginRegistry();
    }

    public IReadOnlyList<IFilePreviewProvider> Providers => _providers;
    public FilePreviewPluginRegistry Plugins => _plugins;

    public IFilePreviewProvider? Resolve(FilePreviewRequest request) =>
        _plugins.Resolve(request) ?? _providers.FirstOrDefault(provider => provider.CanPreview(request));

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
        _plugins.Dispose();
    }

    public static FilePreviewRegistry CreateDefault()
    {
        var plugins = new FilePreviewPluginRegistry();

#if ENHANCED_PREVIEW
        // 扩展实现：PDF 渲染 + DOCX/RTF 富文档 + 音视频播放
        plugins.Register(new PdfViewerPreviewPlugin());
        plugins.Register(new DocxPreviewPlugin());
        plugins.Register(new MediaPlayerPreviewPlugin());
        string[] coveredIds = ["document.pdf", "document.office", "media.player"];
#else
        // 纯净轨：PDF 文本提取；Office 仍为 Unsupported 降级
        plugins.Register(new PdfPreviewPlugin());
        string[] coveredIds = ["document.pdf"];
#endif

        foreach (var plugin in BuiltInComplexPreviewPlugins.CreateUnavailable()
                     .Where(plugin => !coveredIds.Contains(plugin.Descriptor.Id)))
            plugins.Register(plugin);

        return new([
            new MermaidFilePreviewProvider(),
            new MarkdownFilePreviewProvider(),
            new StructuredFilePreviewProvider(),
            new CsvFilePreviewProvider(),
            new ImageFilePreviewProvider(),
            new TextFilePreviewProvider(),
            new BinaryFilePreviewProvider(),
        ], plugins);
    }
}
