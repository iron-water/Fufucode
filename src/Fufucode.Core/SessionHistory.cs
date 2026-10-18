using System.Text.Json;

namespace Fufucode.Core;

public sealed record HistoryEntry(string Id, string? ParentId, string Type, PiMessage? Message,
    string? Summary, bool OnCurrentBranch, bool BeforeCompaction, JsonElement Raw);

/// <summary>Derived view of Pi's append-only entry tree, including old and abandoned history.</summary>
public sealed class SessionHistory
{
    private readonly List<JsonElement> _rawEntries = [];
    public IReadOnlyList<HistoryEntry> Entries { get; private set; } = [];
    public string? Cursor => _rawEntries.Count == 0 ? null : String(_rawEntries[^1], "id");
    public string? LeafId { get; private set; }
    /// <summary>All recorded branches, each assistant entry counted once.</summary>
    public SessionUsageReport GetUsage() => SessionUsageQuery.QueryHistory(this);
    public IReadOnlyList<PiMessage> CurrentBranchMessages => Entries
        .Where(e => e.OnCurrentBranch && e.Message?.Role is "user" or "assistant" or "toolResult" or "custom")
        .Select(e => e.Message!).ToArray();

    public void ApplyEntriesResponse(JsonElement data, bool replace = false)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array ||
            !data.TryGetProperty("leafId", out var leaf) || leaf.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new RpcException("get_entries lacks entries or leafId.");
        var previousRaw = _rawEntries.ToArray();
        var previousEntries = Entries;
        var previousLeaf = LeafId;
        if (replace) _rawEntries.Clear();
        try
        {
            var known = _rawEntries.Select(e => String(e, "id")).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || String(entry, "id") is not { Length: > 0 } id ||
                    String(entry, "type") is null)
                    throw new RpcException("get_entries contains an invalid entry.");
                if (!known.Add(id)) throw new RpcException($"get_entries repeated entry {id}.");
                _rawEntries.Add(entry.Clone());
            }
            LeafId = leaf.ValueKind == JsonValueKind.String ? leaf.GetString() : null;
            Rebuild();
        }
        catch
        {
            _rawEntries.Clear();
            _rawEntries.AddRange(previousRaw);
            LeafId = previousLeaf;
            Entries = previousEntries;
            throw;
        }
    }

    private void Rebuild()
    {
        var byId = _rawEntries.ToDictionary(e => String(e, "id")!, StringComparer.Ordinal);
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        var cursor = LeafId;
        while (cursor is not null)
        {
            if (!byId.TryGetValue(cursor, out var entry)) throw new RpcException("Session leaf references an unknown entry.");
            if (!activeIds.Add(cursor)) throw new RpcException("Session entry tree contains a cycle.");
            cursor = String(entry, "parentId");
        }
        var firstKeptIndex = 0;
        for (var i = 0; i < _rawEntries.Count; i++)
            if (activeIds.Contains(String(_rawEntries[i], "id")!) && String(_rawEntries[i], "type") == "compaction")
            {
                var kept = String(_rawEntries[i], "firstKeptEntryId");
                firstKeptIndex = kept == String(_rawEntries[i], "id") ? i :
                    _rawEntries.FindIndex(e => String(e, "id") == kept);
                if (firstKeptIndex < 0) throw new RpcException("Compaction references an unknown retained entry.");
            }
        var result = new List<HistoryEntry>(_rawEntries.Count);
        for (var i = 0; i < _rawEntries.Count; i++)
        {
            var raw = _rawEntries[i];
            var id = String(raw, "id")!;
            var type = String(raw, "type")!;
            PiMessage? message = null;
            if (type == "message" && raw.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.Object &&
                String(value, "role") is "user" or "assistant" or "toolResult" or "custom") message = PiProtocol.ParseMessage(value);
            result.Add(new HistoryEntry(id, String(raw, "parentId"), type, message,
                String(raw, "summary"), activeIds.Contains(id), activeIds.Contains(id) && i < firstKeptIndex, raw));
        }
        Entries = result;
    }

    private static string? String(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;
}
