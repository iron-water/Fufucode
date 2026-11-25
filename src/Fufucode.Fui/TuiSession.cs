using System.Threading.Channels;

namespace Fufucode.Fui;

internal sealed record TuiImage(string Label, string MimeType, byte[]? Bytes = null, Func<byte[]?>? LoadBytes = null,
    int MaxRows = 16);

internal sealed record TuiLine(string Kind, string Text, IReadOnlyList<TuiImage>? Images = null);

internal sealed record TuiView(string Project, string Session, string Model, string Status,
    IReadOnlyList<TuiLine> Lines, string Draft, int Cursor, int Scroll, string? Approval, bool Busy = false,
    FuiOverlay? Overlay = null, bool ExpandTools = false, bool ShowThinking = true,
    Func<string, ResolvedImage?>? ResolveImage = null);

internal abstract record TuiEvent
{
    public sealed record Message(string Id, string Kind, string Text, bool Append = false,
        IReadOnlyList<TuiImage>? Images = null) : TuiEvent;
    public sealed record Status(string Text, bool Busy) : TuiEvent;
    public sealed record Approval(string Id, string Text) : TuiEvent;
    public sealed record ApprovalClosed(string Id) : TuiEvent;
    public sealed record AgentsChanged(FuiAgentActivitySnapshot Snapshot) : TuiEvent;
}

// The terminal consumes session events, not rendered CLI output or desktop view models.
internal interface ITuiSession : IAsyncDisposable
{
    string Project { get; }
    string Id { get; }
    string? Model { get; }
    IReadOnlyList<TuiLine> History { get; }
    ChannelReader<TuiEvent> Events { get; }
    Task SendAsync(string text, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task RespondAsync(string id, bool allow, CancellationToken ct);
}
