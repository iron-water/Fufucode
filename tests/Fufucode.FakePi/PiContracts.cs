#nullable enable
using System.Text.Json;

// Anchors refer to stable section labels in docs/runtime-baseline.md.
// The baseline only attests the real-Pi subset; synthetic failure cases remain fixtures.
internal static class BaselineAnchors
{
    public const string CapabilityTable = "docs/runtime-baseline.md: 1.1.0 当前运行基线";
    public const string SessionPersistence = "docs/runtime-baseline.md: M4 Core 实测补充 (2026-09-28)";
    public const string ProjectTrust = "docs/runtime-baseline.md: F01 项目信任 RPC 补验 (2026-09-29)";
    public const string FixtureOnly = "docs/runtime-baseline.md: 能力表 / response 与 ID 关联; synthetic transport fixture only";
}

internal sealed record RpcContract(string BaselineAnchor,
    Func<JsonElement, string, string, Task<bool>> Handler, bool IsSimulated = true);
// Descriptive metadata only: no runtime validation, coercion, or extra wire fields.
// RequiredFields is the intersection across existing emitted variants; Variants describes
// normal payload requirements and intentional malformed/minimal fixtures separately.
internal sealed record EventSchema(string[] RequiredFields, string PayloadShape, string Variants);
internal sealed record EventContract(string BaselineAnchor, Func<object, string> Encode, EventSchema Schema);
