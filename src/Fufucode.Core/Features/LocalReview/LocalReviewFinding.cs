using System.Text;
using System.Text.Json;

namespace Fufucode.Core.Features.LocalReview;

public enum LocalReviewPriority
{
    Critical,
    High,
    Medium,
    Low,
}

/// <summary>One located finding inside the frozen snapshot. <see cref="Line"/> is a 1-based line
/// verified against the snapshot entry's captured bytes; a finding without a valid location is
/// rejected during parsing and never shown as precisely located.</summary>
public sealed record LocalReviewFinding(LocalReviewPriority Priority, string Path, int Line,
    string Explanation, string Evidence, string? Reproduced = null)
{
    /// <summary>Machine reference used by jump/copy/export: path:line.</summary>
    public string Location => Path + ":" + Line;
}

/// <summary>Why a model response produced something we could not accept as located evidence.</summary>
public sealed record LocalReviewParseIssue(string Reason, string? Path, int? Line, string? Detail);

/// <summary>Strict parse result. Counts stay separate so a truncated response cannot be shown as
/// a complete review.</summary>
public sealed record LocalReviewParseResult(IReadOnlyList<LocalReviewFinding> Findings,
    IReadOnlyList<LocalReviewParseIssue> Rejected, bool Truncated, bool HadAnyFinding)
{
    /// <summary>How many finding objects the response contained before validation. Zero together
    /// with no whole-response failure is the only shape that may be read as "nothing found":
    /// "the reviewer listed findings and all of them were invalid" is a different outcome.</summary>
    public int ItemsSeen { get; init; }

    public string Summary => Findings.Count + " 项有效发现，拒绝 " + Rejected.Count +
        " 项无效定位" + (Truncated ? "（响应被截断，结果不完整）" : "");

    /// <summary>True when the payload had finding items but strict validation accepted none.</summary>
    public bool AllFindingsInvalid => ItemsSeen > 0 && Findings.Count == 0;
}

/// <summary>
/// Strict review parser. A model response is never a proof of completion: every finding must name
/// a path inside the frozen snapshot whose entry is usable, and a 1-based line that exists in the
/// captured bytes. Anything else is recorded as a rejected item with a machine-readable reason
/// instead of being dropped or coerced into a plausible location.
/// </summary>
public static class LocalReviewFindingsParser
{
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    /// <summary>Reasons that invalidate the whole response rather than one finding.</summary>
    public static readonly IReadOnlySet<string> WholeResponseFailures =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "empty-response", "unparseable-response", "missing-findings-array", "oversize-response",
        };

    /// <summary>The response contract sent verbatim with the request.</summary>
    public const string ResponseContract =
        "Reply with ONLY a JSON object: {\"findings\":[{\"priority\":\"critical|high|medium|low\"," +
        "\"path\":\"<relative path exactly as listed>\",\"line\":<1-based line in the listed content>," +
        "\"explanation\":\"<one sentence>\",\"evidence\":\"<quoted line or short snippet>\"}]}. " +
        "No markdown, no prose before or after, no other keys. An empty findings array is valid " +
        "when nothing is wrong.";

    public static LocalReviewParseResult Parse(string? rawOutput, FileDiffSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var rejected = new List<LocalReviewParseIssue>();
        if (string.IsNullOrWhiteSpace(rawOutput))
            return new([], [new LocalReviewParseIssue("empty-response", null, null, null)], false, false);
        // The cap is a byte budget, but string slicing works in UTF-16 units: a multibyte payload can
        // exceed the byte cap while being shorter than the character cap. Slicing at
        // rawOutput[..MaximumResponseBytes] would then throw ArgumentOutOfRangeException. The response
        // is therefore treated as a byte budget end-to-end: over budget is classified as an
        // incomplete/invalid result, never truncated by character count and never raised as an
        // exception.
        var byteCount = Encoding.UTF8.GetByteCount(rawOutput);
        if (byteCount > MaximumResponseBytes)
            return new([], [new LocalReviewParseIssue("oversize-response", null, null,
                $"{byteCount} bytes exceeds the {MaximumResponseBytes}-byte response limit")],
                true, false);
        var text = rawOutput;
        // Anything over the byte budget already returned above, so inside this path nothing was
        // cut: the flag stays false rather than being re-derived from a character count.
        const bool truncated = false;

        JsonDocument document;
        try { document = JsonDocument.Parse(FindJsonObject(text)); }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return new([], [new LocalReviewParseIssue("unparseable-response", null, null, error.Message)],
                truncated, false);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("findings", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return new([], [new LocalReviewParseIssue("missing-findings-array", null, null, null)],
                    truncated, false);

            var accepted = new List<LocalReviewFinding>();
            var lookup = new Dictionary<string, GitDiffSnapshotEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in snapshot.Entries)
                lookup.TryAdd(entry.RelativePath, entry);

            var itemsSeen = 0;
            foreach (var item in items.EnumerateArray())
            {
                itemsSeen++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    rejected.Add(new LocalReviewParseIssue("finding-not-an-object", null, null, null));
                    continue;
                }
                var path = Text(item, "path");
                var priorityText = Text(item, "priority");
                var explanation = Text(item, "explanation");
                var evidence = Text(item, "evidence");
                var lineElement = item.TryGetProperty("line", out var raw) ? raw : default;

                if (string.IsNullOrWhiteSpace(path))
                {
                    rejected.Add(new LocalReviewParseIssue("missing-path", null, null, null));
                    continue;
                }
                if (!lookup.TryGetValue(path!, out var entry))
                {
                    rejected.Add(new LocalReviewParseIssue("path-outside-snapshot", path, null, null));
                    continue;
                }
                if (entry.Error is not null)
                {
                    rejected.Add(new LocalReviewParseIssue("snapshot-entry-unusable", path, null, entry.Error));
                    continue;
                }
                // A stale captured entry cannot anchor a precise location: its bytes were already
                // observed to change during capture, so line numbers are unreliable by definition.
                if (entry.Stale)
                {
                    rejected.Add(new LocalReviewParseIssue("stale-snapshot-entry", path, null,
                        "entry changed during capture"));
                    continue;
                }
                if (!TryPriority(priorityText, out var priority))
                {
                    rejected.Add(new LocalReviewParseIssue("invalid-priority", path, null, priorityText));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(explanation))
                {
                    rejected.Add(new LocalReviewParseIssue("missing-explanation", path, null, null));
                    continue;
                }
                // Evidence is mandatory: an unquoted claim cannot be verified against the snapshot,
                // so it is rejected rather than accepted as a located finding.
                if (string.IsNullOrWhiteSpace(evidence))
                {
                    rejected.Add(new LocalReviewParseIssue("missing-evidence", path, null, null));
                    continue;
                }
                if (lineElement.ValueKind != JsonValueKind.Number || !lineElement.TryGetInt32(out var line)
                    || line < 1)
                {
                    rejected.Add(new LocalReviewParseIssue("invalid-line", path, null,
                        lineElement.ValueKind.ToString()));
                    continue;
                }
                if (entry.Bytes is null)
                {
                    rejected.Add(new LocalReviewParseIssue("no-captured-bytes", path, line, null));
                    continue;
                }
                // Binary entries are never line-locatable: their bytes are a byte copy, not text.
                if (entry.Diff.IsBinary == true)
                {
                    rejected.Add(new LocalReviewParseIssue("binary-entry-not-locatable", path, line, null));
                    continue;
                }
                var lines = SplitLines(entry.Bytes);
                if (line > lines.Length)
                {
                    rejected.Add(new LocalReviewParseIssue("line-beyond-content", path, line,
                        "content has " + lines.Length + " lines"));
                    continue;
                }
                var quoted = evidence!.Trim();
                if (!LineMatches(lines[line - 1], quoted))
                {
                    rejected.Add(new LocalReviewParseIssue("evidence-not-at-line", path, line, null));
                    continue;
                }
                accepted.Add(new LocalReviewFinding(priority, entry.RelativePath, line,
                    explanation!.Trim(), quoted));
            }
            return new LocalReviewParseResult(
                accepted.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => PriorityRank(f.Priority)).ThenBy(f => f.Line).ToList(),
                rejected, truncated, accepted.Count > 0 || rejected.Count > 0)
            {
                ItemsSeen = itemsSeen,
            };
        }
    }

    /// <summary>
    /// Enum.TryParse happily converts numeric strings ("99") into undefined enum values. A review
    /// priority must be one of the four named levels, so digits and undefined values are rejected.
    /// </summary>
    internal static bool TryPriority(string? text, out LocalReviewPriority priority)
    {
        priority = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed.Any(char.IsDigit)) return false;
        if (!Enum.TryParse(trimmed, ignoreCase: true, out priority)) return false;
        if (!Enum.IsDefined(priority)) { priority = default; return false; }
        return true;
    }

    public static int PriorityRank(LocalReviewPriority priority) => priority switch
    {
        LocalReviewPriority.Critical => 0,
        LocalReviewPriority.High => 1,
        LocalReviewPriority.Medium => 2,
        _ => 3,
    };

    /// <summary>Prose or fences around the payload are tolerated only in the sense that we locate
    /// the first balanced top-level JSON object; a response without one is a parse failure.</summary>
    internal static string FindJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) throw new ArgumentException("no-json-object");
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text[start..(i + 1)];
                    if (depth < 0) throw new ArgumentException("unbalanced-json");
                    break;
            }
        }
        throw new ArgumentException("truncated-json-object");
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string[] SplitLines(byte[] bytes)
    {
        var content = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Replace('\r', '\n');
        if (content.Length == 0) return [];
        var lines = content.Split('\n');
        // A trailing newline does not create an extra addressable line.
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static bool LineMatches(string actual, string evidence)
    {
        var left = actual.Trim();
        var right = evidence.Trim();
        if (left.Length == 0 || right.Length == 0) return false;
        return left.Contains(right, StringComparison.Ordinal) ||
            right.Contains(left, StringComparison.Ordinal);
    }
}
