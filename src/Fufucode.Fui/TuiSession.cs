using System.Threading.Channels;

namespace Fufucode.Fui;

internal sealed record TuiImage(string Label, string MimeType, byte[]? Bytes = null, Func<byte[]?>? LoadBytes = null,
    int MaxRows = 16);

internal sealed record TuiLine(string Kind, string Text, ToolCallSnapshot? Tool = null,
    IReadOnlyList<TuiImage>? Images = null);

internal sealed record TuiView(string Project, string Session, string Model, string Status,
    IReadOnlyList<TuiLine> Lines, string Draft, int Cursor, int Scroll, string? Approval, bool Busy = false,
    FuiOverlay? Overlay = null, bool ExpandTools = false, bool ShowThinking = true,
    IReadOnlyList<FileAttachment>? Attachments = null, bool CompactCards = true, bool MouseWheel = true,
    string Theme = "auto", bool Hyperlinks = false, IReadOnlyList<string>? StatusSegments = null,
    string CommandKey = "Ctrl+P", Func<string, ResolvedImage?>? ResolveImage = null,
    int SelectionStart = -1, int SelectionEnd = -1, string? EditorMode = null);

internal abstract record TuiEvent
{
    public sealed record Message(string Id, string Kind, string Text, bool Append = false,
        ToolCallSnapshot? Tool = null, IReadOnlyList<TuiImage>? Images = null) : TuiEvent;
    public sealed record Status(string Text, bool Busy) : TuiEvent;
    public sealed record Approval(string Id, string Text) : TuiEvent;
    public sealed record ApprovalClosed(string Id) : TuiEvent;
    public sealed record AgentsChanged(FuiAgentActivitySnapshot Snapshot) : TuiEvent;
    public sealed record TurnFinished(FuiTurnOutcome Outcome, string? Summary) : TuiEvent;
}

internal enum FuiTurnOutcome
{
    Completed,
    Failed,
    Interrupted,
    Other
}

// The terminal consumes session events, not rendered CLI output or desktop view models.
internal interface ITuiSession : IAsyncDisposable
{
    string Project { get; }
    string Id { get; }
    string? Model { get; }
    IReadOnlyList<TuiLine> History { get; }
    IReadOnlyList<string?> HistorySources => [];
    ChannelReader<TuiEvent> Events { get; }
    Task SendAsync(string text, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task RespondAsync(string id, bool allow, CancellationToken ct);
}

internal sealed record FuiUsageInfo(long? Input, long? Output, long? Total, long? CacheRead, long? CacheWrite);
internal sealed record FuiQueueEntry(string Text, bool Steering);

internal sealed record FuiSessionInfo(
    string Project,
    string Id,
    string? Model,
    string? ThinkingLevel,
    int Messages,
    int Tools,
    int Errors,
    int Attachments,
    int QueueCount,
    FuiUsageInfo? Usage,
    bool HistoryPartial = false,
    int Thinking = 0,
    long? ContextWindow = null,
    string? ContextWindowSource = null,
    string? ContextWindowUnknownReason = null,
    string? ExecutionMode = null,
    IReadOnlyList<FuiQueueEntry>? QueueEntries = null,
    IReadOnlyList<string>? RecoverableQueue = null);

// Optional facts are implemented only where the session can prove them. Commands must fail
// explicitly rather than infer a retry target or synthesize usage in the controller.
internal interface IFuiSessionInspector
{
    Task<FuiSessionInfo> InspectAsync(CancellationToken ct);
    Task<string> RetryLastUserAsync(CancellationToken ct);
}

// One live read produces the capability catalogue and the extension/MCP projections that
// consume it. Callers must not append a second hardcoded tool list.
internal interface IFuiRuntimeCatalogSource
{
    Task<FuiRuntimeCatalog> ReadRuntimeCatalogAsync(CancellationToken ct);
}

internal interface IFuiStatusProvider
{
    string RuntimeLabel => "FUMO";
    int QueuedCount => 0;
    string? UsageSummary => null;
}
