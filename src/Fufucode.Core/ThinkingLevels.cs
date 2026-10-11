using System.Text.Json;
using Fufucode.Pi;

namespace Fufucode.Core;

/// <summary>
/// Thinking levels the connected Pi actually offers for the current model. Pi clamps an
/// unsupported level silently, so a successful set_thinking_level is not proof it was applied.
/// </summary>
public sealed record ThinkingLevelAvailability(IReadOnlyList<string> Levels, string? CurrentLevel, string? Model)
{
    /// <summary>Case-insensitive membership against the wire vocabulary reported by Pi.</summary>
    public bool Supports(string level)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        return Levels.Contains(level, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsEmpty => Levels.Count == 0;

    /// <summary>Reads the get_available_thinking_levels response and the level Pi reports as active.</summary>
    public static ThinkingLevelAvailability Parse(PiRpcResponse response, string? model = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.Success || response.Data is not { ValueKind: JsonValueKind.Object } data)
            throw new PiRpcException($"Pi get_available_thinking_levels failed: {response.Error}");
        if (!data.TryGetProperty("levels", out var levels) || levels.ValueKind != JsonValueKind.Array)
            throw new PiRpcException("Pi get_available_thinking_levels returned no level list.");
        var values = new List<string>();
        foreach (var item in levels.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } value)
                throw new PiRpcException("Pi get_available_thinking_levels returned a non-string level.");
            values.Add(value);
        }
        var current = data.TryGetProperty("level", out var level) && level.ValueKind == JsonValueKind.String
            ? level.GetString() : null;
        return new(values, current, model);
    }

    /// <summary>Reads the level Pi reports as active from a get_state payload, when present.</summary>
    public static string? ReadCurrentLevel(JsonElement state) =>
        state.ValueKind == JsonValueKind.Object &&
        state.TryGetProperty("thinkingLevel", out var level) && level.ValueKind == JsonValueKind.String
            ? level.GetString() : null;
}
