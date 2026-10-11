#nullable enable
using System.Text.Json;

// Anchors are provenance labels for the recorded Pi RPC behaviour baseline.
// The baseline only attests the real-Pi subset; synthetic failure cases remain fixtures.
internal static class BaselineAnchors
{
    public const string CapabilityTable = "baseline: 1.1.0 capability table";
    public const string SessionPersistence = "baseline: Core session persistence supplement (2026-09-28)";
    public const string ProjectTrust = "baseline: project trust RPC verification (2026-09-29)";
    public const string FixtureOnly = "baseline: capability table response/ID correlation; synthetic transport fixture only";
}

internal sealed record RpcContract(string BaselineAnchor,
    Func<JsonElement, string, string, Task<bool>> Handler, bool IsSimulated = true);
// Descriptive metadata only: no runtime validation, coercion, or extra wire fields.
// RequiredFields is the intersection across existing emitted variants; Variants describes
// normal payload requirements and intentional malformed/minimal fixtures separately.
internal sealed record EventSchema(string[] RequiredFields, string PayloadShape, string Variants);
internal sealed record EventContract(string BaselineAnchor, Func<object, string> Encode, EventSchema Schema);
