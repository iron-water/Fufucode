using System.Text.Json.Serialization;

namespace Fufucode.Core;

public sealed record PiPromptImage(string Data, string MimeType, string Type = "image");
public sealed record PiPromptFields(string Message, string? StreamingBehavior = null, IReadOnlyList<PiPromptImage>? Images = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PiPromptFields))]
public partial class PiRpcJsonContext : JsonSerializerContext;
