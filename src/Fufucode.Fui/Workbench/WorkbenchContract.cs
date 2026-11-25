namespace Fufucode.Fui;

internal sealed record FuiChoice(string Value, string Label, string? Detail = null, string? ParentSessionId = null,
    string? SearchText = null)
{
    public FuiTreePosition? TreePosition { get; init; }
}
internal readonly record struct FuiTreePosition(int Depth, bool? Expanded);
internal enum FuiOverlayKind { Picker, Outline, Transcript, Copy, Context, Capabilities, Extensions, Mcp, Jobs }
internal sealed record FuiOverlay(string Title, string Query, IReadOnlyList<FuiChoice> Items, int Selected,
    string? Message = null, string? Footer = null, FuiOverlayKind Kind = FuiOverlayKind.Picker);
internal sealed record FuiActionResult(string? Notice = null, ITuiSession? Replacement = null);

internal static class FuiChoiceGroups
{
    internal static bool IsGroup(FuiChoice choice) =>
        FuiAgentActivityRoster.IsGroupChoice(choice) ||
        FuiContextWorkbench.IsGroupChoice(choice) ||
        FuiRuntimeCatalog.IsGroupChoice(choice);
}

// Selections carry the backend's ID, never a truncated screen label. A replacement is owned by
// the caller only after the action succeeds; a failed switch must leave the active session usable.
internal interface IFuiWorkbenchSession
{
    Task<IReadOnlyList<FuiChoice>> ChoicesAsync(string action, CancellationToken ct);
    Task<FuiActionResult> ExecuteAsync(string action, string argument, CancellationToken ct);
}
