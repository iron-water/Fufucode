using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Fufucode.App.Features.FilePreview;

public interface IFilePreviewProvider : IDisposable
{
    FilePreviewKind Kind { get; }
    bool CanPreview(FilePreviewRequest request);
    Task<FilePreviewResult> LoadAsync(FilePreviewRequest request, CancellationToken cancellationToken);
    Control CreateView(FilePreviewResult result);
}
