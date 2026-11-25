namespace Fufucode.Fui;

internal sealed record FuiChoice(string Value, string Label, string? Detail = null, string? ParentSessionId = null)
{
    public FuiTreePosition? TreePosition { get; init; }
}
internal readonly record struct FuiTreePosition(int Depth, bool? Expanded);
internal sealed record FuiOverlay(string Title, string Query, IReadOnlyList<FuiChoice> Items, int Selected, string? Message = null);
internal sealed record FuiActionResult(string? Notice = null, ITuiSession? Replacement = null);

// Selections carry the backend's ID, never a truncated screen label. A replacement is owned by
// the caller only after the action succeeds; a failed switch must leave the active session usable.
internal interface IFuiWorkbenchSession
{
    Task<IReadOnlyList<FuiChoice>> ChoicesAsync(string action, CancellationToken ct);
    Task<FuiActionResult> ExecuteAsync(string action, string argument, CancellationToken ct);
}
