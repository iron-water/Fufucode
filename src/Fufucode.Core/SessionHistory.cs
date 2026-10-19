using System.Text.Json;

namespace Fufucode.Core;

public sealed record HistoryEntry(string Id, string? ParentId, string Type, PiMessage? Message,
    string? Summary, bool OnCurrentBranch, bool BeforeCompaction, JsonElement Raw);

/// <summary>Derived view of Pi's append-only entry tree, including old and abandoned history.</summary>
public sealed class SessionHistory
{
    private readonly List<JsonElement> _rawEntries = [];
    // 按 id 索引原始条目：追加/去重/父链回溯都是 O(1)，不再每次重建整张字典。
    private readonly Dictionary<string, JsonElement> _rawById = new(StringComparer.Ordinal);
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
        ApplyEntries(RawEntries(entries), leaf, replace);
    }

    private static IEnumerable<JsonElement> RawEntries(JsonElement entries)
    {
        if (entries.ValueKind != JsonValueKind.Array)
            throw new RpcException("get_entries lacks entries or leafId.");
        foreach (var entry in entries.EnumerateArray()) yield return entry;
    }

    /// <summary>
    /// 批量追加（或替换）条目并只重建一次派生视图。恢复路径在循环外一次性传入全部条目，
    /// 避免每条条目各触发一次全量重建。
    /// </summary>
    public void ApplyEntries(IEnumerable<JsonElement> entries, JsonElement leaf, bool replace = false)
    {
        if (leaf.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new RpcException("get_entries lacks entries or leafId.");
        var newLeaf = leaf.ValueKind == JsonValueKind.String ? leaf.GetString() : null;

        if (replace)
        {
            var previousRaw = _rawEntries.ToArray();
            var previousEntries = Entries;
            var previousLeaf = LeafId;
            _rawEntries.Clear();
            _rawById.Clear();
            try
            {
                Append(Validate(entries));
                LeafId = newLeaf;
                Rebuild();
            }
            catch
            {
                _rawEntries.Clear();
                _rawEntries.AddRange(previousRaw);
                _rawById.Clear();
                foreach (var raw in previousRaw) _rawById[String(raw, "id")!] = raw;
                LeafId = previousLeaf;
                Entries = previousEntries;
                throw;
            }
            return;
        }

        // 追加路径只在校验通过后改动状态，回滚时删除新加条目即可，不复制整张索引。
        var added = Validate(entries);
        var previousCount = _rawEntries.Count;
        var previousLeafAppend = LeafId;
        var previousEntriesAppend = Entries;
        try
        {
            Append(added);
            LeafId = newLeaf;
            Rebuild();
        }
        catch
        {
            if (_rawEntries.Count > previousCount)
                _rawEntries.RemoveRange(previousCount, _rawEntries.Count - previousCount);
            foreach (var (id, _) in added) _rawById.Remove(id);
            LeafId = previousLeafAppend;
            Entries = previousEntriesAppend;
            throw;
        }
    }

    private List<(string Id, JsonElement Entry)> Validate(IEnumerable<JsonElement> entries)
    {
        var added = new List<(string, JsonElement)>();
        var batchIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object || String(entry, "id") is not { Length: > 0 } id ||
                String(entry, "type") is null)
                throw new RpcException("get_entries contains an invalid entry.");
            if (_rawById.ContainsKey(id) || !batchIds.Add(id))
                throw new RpcException($"get_entries repeated entry {id}.");
            added.Add((id, entry.Clone()));
        }
        return added;
    }

    private void Append(List<(string Id, JsonElement Entry)> added)
    {
        foreach (var (id, entry) in added)
        {
            _rawEntries.Add(entry);
            _rawById[id] = entry;
        }
    }

    private void Rebuild()
    {
        // 每个条目的 id/parentId/type 只解析一次，后续父链回溯与边界判断复用这些数组。
        var count = _rawEntries.Count;
        var ids = new string[count];
        var parents = new string?[count];
        var types = new string[count];
        var indexById = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var raw = _rawEntries[i];
            ids[i] = String(raw, "id")!;
            parents[i] = String(raw, "parentId");
            types[i] = String(raw, "type")!;
            indexById[ids[i]] = i;
        }

        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        var cursor = LeafId;
        while (cursor is not null)
        {
            if (!indexById.TryGetValue(cursor, out var index))
                throw new RpcException("Session leaf references an unknown entry.");
            if (!activeIds.Add(cursor)) throw new RpcException("Session entry tree contains a cycle.");
            cursor = parents[index];
        }

        var firstKeptIndex = 0;
        for (var i = 0; i < count; i++)
        {
            if (!activeIds.Contains(ids[i]) || types[i] != "compaction") continue;
            var kept = String(_rawEntries[i], "firstKeptEntryId");
            if (kept is null) firstKeptIndex = -1;
            else if (kept == ids[i]) firstKeptIndex = i;
            else if (!indexById.TryGetValue(kept, out firstKeptIndex)) firstKeptIndex = -1;
            if (firstKeptIndex < 0) throw new RpcException("Compaction references an unknown retained entry.");
        }

        var result = new List<HistoryEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var raw = _rawEntries[i];
            var id = ids[i];
            var type = types[i];
            PiMessage? message = null;
            if (type == "message" && raw.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.Object &&
                String(value, "role") is "user" or "assistant" or "toolResult" or "custom") message = PiProtocol.ParseMessage(value);
            var active = activeIds.Contains(id);
            result.Add(new HistoryEntry(id, parents[i], type, message,
                String(raw, "summary"), active, active && i < firstKeptIndex, raw));
        }
        Entries = result;
    }

    private static string? String(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;
}
