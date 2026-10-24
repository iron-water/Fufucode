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
        foreach (var plugin in BuiltInComplexPreviewPlugins.CreateUnavailable()) plugins.Register(plugin);
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
