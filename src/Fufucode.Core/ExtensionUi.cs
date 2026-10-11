using System.Text.Json;
using Fufucode.Pi;

namespace Fufucode.Core;

/// <summary>The original request is the reply token. Its generation and payload must not be replaced.</summary>
public sealed record ExtensionUiRequest(Guid Generation, string Id, string Method, JsonElement Payload,
    DateTimeOffset ReceivedAt, TimeSpan Timeout);

public enum ApprovalAvailability { Unverified, Verified, Unavailable }

/// <summary>Optional UI contract alongside the frozen IChatSession chat contract.</summary>
public interface IExtensionUiSession
{
    IReadOnlyList<ExtensionUiRequest> PendingExtensionUiRequests { get; }
    ApprovalAvailability ApprovalAvailability { get; }
    string? ApprovalDiagnostic { get; }
    event Action<ExtensionUiRequest>? ExtensionUiRequestReceived;
    event Action<ExtensionUiRequest>? ExtensionUiRequestClosed;
    event Action<PiExtensionUiRequestEvent>? ExtensionUiNotification;
    event Action<PiExtensionErrorEvent>? ExtensionError;
    Task RespondToExtensionUiAsync(ExtensionUiRequest request, PiExtensionUiResponse response,
        CancellationToken cancellationToken = default);
}
