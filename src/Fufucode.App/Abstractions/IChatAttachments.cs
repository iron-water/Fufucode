using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.App.Abstractions;

public sealed record ChatImage(string Data, string MimeType);
public interface IChatAttachments
{
    bool CanSendImages { get; }
    Task SendWithImagesAsync(string text, IReadOnlyList<ChatImage> images, CancellationToken cancellationToken = default);
}
public interface IExecutionModeSession
{
    bool SupportsExecutionModes { get; }
    string ExecutionMode { get; }
    event System.Action<string>? ExecutionModeChanged;
    Task SetExecutionModeAsync(string mode, CancellationToken cancellationToken = default);
}
