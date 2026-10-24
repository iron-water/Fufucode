using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.App.Features.FilePreview;

public sealed class FilePreviewRegistry : IDisposable
{
    private readonly IReadOnlyList<IFilePreviewProvider> _providers;

    public FilePreviewRegistry(IEnumerable<IFilePreviewProvider> providers) => _providers = providers.ToArray();
    public IReadOnlyList<IFilePreviewProvider> Providers => _providers;

    public IFilePreviewProvider? Resolve(FilePreviewRequest request) =>
        _providers.FirstOrDefault(provider => provider.CanPreview(request));

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    public static FilePreviewRegistry CreateDefault() => new([
        new MermaidFilePreviewProvider(),
        new MarkdownFilePreviewProvider(),
        new StructuredFilePreviewProvider(),
        new CsvFilePreviewProvider(),
        new ImageFilePreviewProvider(),
        new TextFilePreviewProvider(),
        new BinaryFilePreviewProvider(),
    ]);
}
