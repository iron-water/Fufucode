using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Security.Cryptography;
using Fufucode.Core.Security;

namespace Fufucode.Core.Backend;

/// <summary>One immutable handoff. No lookups via model-supplied paths or mutable worktrees.</summary>
internal sealed class SubagentEvidence
{
    private const int PageChars = 4096;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Dictionary<string, string> _sections;
    private readonly SecurityToolDispatcher? _security;
    public string Id { get; }
    public string? CandidateBase { get; }
    private readonly string? _patchPath;
    private readonly string? _patchSha256;
    public string Header { get; }
    public long Size { get; }

    public SubagentEvidence(SubagentResult result, SecurityToolDispatcher? security)
    {
        _security = security;
        Id = result.JobId!;
        _patchPath = result.PatchPath;
        _patchSha256 = result.PatchSha256;
        CandidateBase = result.PatchSha256 is not null ? result.BaseHead : null;
        var commands = new StringBuilder();
        var trace = new StringBuilder();
        var commandCount = 0;
        foreach (var step in result.ToolTrace ?? [])
        {
            trace.Append(step.Tool).AppendLine(step.Success ? ": ok" : ": failed");
            if (SubagentRunner.IsReadEvidence(step.Tool)) trace.AppendLine(step.ArgumentsJson).AppendLine(step.Summary);
            if (step.Tool != "run_command") continue;
            commands.Append('#').Append(++commandCount).Append(step.Success ? " ok " : " failed ")
                .AppendLine(step.ArgumentsJson).AppendLine(step.Summary);
        }
        _sections = new(StringComparer.Ordinal)
        {
            ["report"] = result.Content.Length != 0 ? result.Content : result.Error ?? "",
            ["commands"] = commands.ToString(), ["trace"] = trace.ToString(),
            ["files"] = JsonSerializer.Serialize(result.ChangedFiles ?? [], Json)
        };
        if (result.EvidencePatch is { } patch) _sections["patch"] = patch;
        var header = new StringBuilder().Append("[子代理 ").Append(result.AgentName).Append(" · ")
            .Append(result.Mode == SubagentMode.Patch ? "patch" : "read").AppendLine("]")
            .Append("jobId: ").AppendLine(Id)
            .Append("status: ").AppendLine(!result.Success ? "failed" : result.Truncated ? "incomplete" : "completed");
        if (result.Truncated) header.AppendLine("子代理达到轮次上限，结论可能不完整。");
        if (result.ParentWorkspaceDirty) header.AppendLine("父工作区有未提交改动；候选基于提交，不含这些改动。");
        if (result.BaseHead is { } head) header.Append("baseHead: ").AppendLine(head);
        if (result.PatchPath is { } path)
        {
            header.Append("patchSha256: ").AppendLine(result.PatchSha256).Append("patchPath: ").AppendLine(path)
                .AppendLine("候选补丁，未应用、未验收。");
            if (result.EvidencePatch is null) header.AppendLine("非 UTF-8 补丁：使用原始补丁文件核对，未生成改写后的文本。");
        }
        else if (result.Mode == SubagentMode.Patch && result.ChangedFiles is { Count: 0 }) header.AppendLine("未产生候选补丁。");
        header.Append("工具调用: ").Append(result.ToolCalls).Append("；命令实录: ").Append(commandCount)
            .Append("（执行状态不代表验收）");
        if (result.DurationMs is { } ms) header.Append("；耗时: ").Append(ms).Append(" ms");
        Header = header.ToString();
        Size = Header.Length + _sections.Values.Sum(s => (long)s.Length);
    }

    public PureToolResult Card(string callId, SubagentResult result)
    {
        var text = new StringBuilder(Header);
        if (result.Error is { Length: > 0 } error) text.Append("\n错误：").Append(Preview(callId, error));
        if (result.Content.Length != 0) text.Append("\n模型报告：").Append(Preview(callId, result.Content));
        return new(callId, result.Success, text.ToString());
    }

    private PureToolResult Filter(string id, string text)
    {
        var result = new PureToolResult(id, true, text);
        return _security?.FilterOutput(result) ?? result;
    }

    private string Preview(string id, string text)
    {
        var safe = Filter(id, text).Content;
        var end = End(safe, 0, 1536);
        return end == safe.Length ? safe : safe[..end] + "…（按 jobId 读取 report 全文）";
    }

    private static int End(string text, int offset, int length)
    {
        var end = offset + Math.Min(length, text.Length - offset);
        return end < text.Length && end > offset && char.IsSurrogatePair(text, end - 1) ? end - 1 : end;
    }

    // Retain the read handle across a pinned verification run. Never use a caller-provided path/commit.
    public async Task<FileStream> OpenCandidateAsync(CancellationToken ct)
    {
        if (_patchPath is null || _patchSha256 is null || (File.GetAttributes(_patchPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("没有可复核的原始补丁文件。");
        var stream = File.Open(_patchPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            if (Convert.ToHexStringLower(hash) != _patchSha256) throw new IOException("候选补丁的 SHA-256 已改变。");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public PureToolResult Page(string callId, JsonElement args)
    {
        var section = args.TryGetProperty("section", out var s) ? s.ValueKind == JsonValueKind.String ? s.GetString() : null : "report";
        if (section is null || !_sections.TryGetValue(section, out var body)) return new(callId, false, "该任务没有所请求的证据段。");
        var safe = Filter(callId, body); // Filter before slicing, including on reads after a live policy change.
        if (!safe.Success) return safe;
        var content = safe.Content;
        var offset = 0;
        if (args.TryGetProperty("offset", out var o) && (o.ValueKind != JsonValueKind.Number || !o.TryGetInt32(out offset) ||
            offset < 0 || offset > content.Length || (offset > 0 && offset < content.Length && char.IsSurrogatePair(content, offset - 1))))
            return new(callId, false, "offset 无效；请使用上一页的 nextOffset。");
        var end = End(content, offset, PageChars);
        return new(callId, true, JsonSerializer.Serialize(new { jobId = Id, section, offset,
            nextOffset = end < content.Length ? end : (int?)null, filtered = content != body, text = content[offset..end] }, Json));
    }
}

internal sealed record SubagentGrant(IReadOnlyDictionary<string, SubagentEvidence> Items, SubagentEvidence? Basis);

/// <summary>Owned by one parent runner, never shared by workspace or process. A running reader pins its explicit grant.</summary>
internal sealed class SubagentEvidenceStore
{
    private const int MaxRecent = 32;
    private const int HistoryChars = 4 * 1024 * 1024;
    private readonly Dictionary<string, SubagentEvidence> _recent = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private long _chars;
    internal const string Unavailable = "证据引用不存在、已过期或未授权给当前代理。";

    public void Publish(SubagentEvidence item)
    {
        lock (_recent)
        {
            // Never cut the newest result in half just because one candidate exceeds the history budget.
            while (_order.Count > 0 && (_order.Count >= MaxRecent || _chars + item.Size > HistoryChars))
                if (_recent.Remove(_order.Dequeue(), out var old)) _chars -= old.Size;
            _recent.Add(item.Id, item); _order.Enqueue(item.Id); _chars += item.Size;
        }
    }

    public SubagentEvidence? Find(string id) { lock (_recent) return _recent.GetValueOrDefault(id); }

    public static string? Id(JsonElement args) => args.TryGetProperty("jobId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    public SubagentGrant? Bind(JsonElement args, IReadOnlyDictionary<string, SubagentEvidence>? inherited)
    {
        var hasRefs = args.TryGetProperty("contextRefs", out var refs);
        var hasBase = args.TryGetProperty("baseRef", out var basis);
        if (!hasRefs && !hasBase) return null;
        if (hasRefs && refs.ValueKind != JsonValueKind.Array) throw new ArgumentException("contextRefs 必须是 jobId 数组。");
        var grant = new Dictionary<string, SubagentEvidence>(StringComparer.Ordinal);
        SubagentEvidence? candidate = null;
        lock (_recent)
        {
            SubagentEvidence Add(JsonElement value)
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } id ||
                    (inherited?.GetValueOrDefault(id) ?? _recent.GetValueOrDefault(id)) is not { } item) throw new ArgumentException(Unavailable);
                return grant[id] = item;
            }
            if (hasRefs) foreach (var value in refs.EnumerateArray()) Add(value);
            if (hasBase && (candidate = Add(basis)).CandidateBase is null) throw new ArgumentException("baseRef 必须指向含候选补丁的任务。");
        }
        return grant.Count == 0 ? null : new(grant, candidate);
    }
}
