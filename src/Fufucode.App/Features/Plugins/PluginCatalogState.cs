using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Fufucode.App.Features.Plugins;

/// <summary>本产品本地插件目录的持久状态（plugins/local/catalog.json）。</summary>
public sealed record PluginCatalogState
{
    /// <summary>目录状态文件格式版本。未来版本文件一律拒绝，不静默重建。</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>并发/重开保护代次：每次独占写入自增，写入方必须带读到的代次。</summary>
    public long Generation { get; init; }

    public IReadOnlyList<PluginRecord> Plugins { get; init; } = [];
}

public sealed record PluginRecord
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Backend { get; init; } = "unknown";
    public string StorageKey { get; init; } = string.Empty;
    public IReadOnlyList<PluginVersionRecord> Versions { get; init; } = [];
    public string? SelectedVersionId { get; init; }
    public string? PreviousVersionId { get; init; }
    public string? PendingApplyVersionId { get; init; }
    /// <summary>待移除的具体版本（不是整个插件）：pending 必须绑定目标。</summary>
    public string? PendingRemoveVersionId { get; init; }
    public bool PendingRollback { get; init; }
    /// <summary>
    /// 跨 await 操作身份：每次接受应用请求时换成新值，完成提交时核对。
    /// 旧操作的结果不得覆盖更新的 pending/选定/回退目标。
    /// </summary>
    public string? OperationToken { get; init; }
    /// <summary>
    /// 仍在途的宿主回调候选（持久化）。宿主回调尚未返回时，其候选文件不得被删除；
    /// 跨服务实例/进程可见，任一实例都必须保护这些候选。
    /// </summary>
    public IReadOnlyList<InFlightCandidate> InFlight { get; init; } = [];
    /// <summary>最近一次应用回执：宿主只读核对用，本模块不据此自动重发。</summary>
    public ApplyReceipt? LastApplyReceipt { get; init; }
    /// <summary>
    /// 回执历史（有界）。原身份在这里保留：迟到/取消/异常的操作只按自己的 token+version 记历史，
    /// 不会把自己的结果写到另一个请求的名下，也不会顶掉已确认的新回执。
    /// </summary>
    public IReadOnlyList<ApplyReceipt> RecentReceipts { get; init; } = [];
    /// <summary>Metadata-only registration endings. Never manufacture an application receipt.</summary>
    public IReadOnlyList<PluginRegistrationReconciliation> Reconciliations { get; init; } = [];
    /// <summary>插件级声明（兼容旧数据与展示）；应用门禁一律按版本记录判定。</summary>
    public IReadOnlyList<string> SupportedHostVersions { get; init; } = [];
    public string? LoadError { get; init; }
    public DateTimeOffset? LoadErrorAtUtc { get; init; }
    public IReadOnlyList<PluginRemovalRecord> Removals { get; init; } = [];
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record PluginVersionRecord
{
    public string VersionId { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string DirectoryName { get; init; } = string.Empty;
    public string EntryRelativePath { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public DateTimeOffset ImportedAtUtc { get; init; }
    public string SourceKind { get; init; } = "localDirectory";
    public string? SourceRoot { get; init; }
    /// <summary>
    /// 该固定版本导入时冻结的后端与宿主版本声明。兼容判定只读这里，
    /// 后续导入别的版本不得改写旧版本的门禁。
    /// </summary>
    public string Backend { get; init; } = string.Empty;
    public IReadOnlyList<string> SupportedHostVersions { get; init; } = [];
    /// <summary>托管副本与导入摘要不一致时的诊断（例如导入后被改写）。</summary>
    public string? IntegrityError { get; init; }
    public DateTimeOffset? IntegrityCheckedAtUtc { get; init; }
}

/// <summary>在途宿主回调的候选登记。</summary>
public sealed record InFlightCandidate
{
    public string VersionId { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; }
}

/// <summary>应用回执：记录本次操作的身份与结果类别，供宿主只读核对。</summary>
public sealed record ApplyReceipt
{
    public string VersionId { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
    /// <summary>confirmed / failed / threw / cancelled_unknown / superseded / integrity_rejected</summary>
    public string Outcome { get; init; } = string.Empty;
    public string? Message { get; init; }
    public DateTimeOffset AtUtc { get; init; }
}

public sealed record PluginRegistrationReconciliation(InFlightCandidate Request,
    PluginCallbackCompletion OriginalReturn, PluginHostContext ObservedContext, DateTimeOffset AtUtc);

public sealed record PluginRemovalRecord
{
    public string VersionId { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public DateTimeOffset RemovedAtUtc { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>读取结果：损坏或未来版本文件时保持原样，由调用方显式决定。</summary>
public sealed record PluginCatalogLoadResult(PluginCatalogState? State, bool IsCorrupt, string? Reason)
{
    public bool HasState => State is not null;
}

/// <summary>
/// 目录状态的原子读写。写入必须带读取时看到的 generation：代次不匹配即拒绝，
/// 防止另一个进程或另一个 VM 实例用旧状态覆盖新状态。
/// </summary>
public static class PluginCatalogStore
{
    public const string CatalogFileName = "catalog.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static PluginCatalogLoadResult Load(string directory)
    {
        var path = Path.Combine(directory, CatalogFileName);
        if (!File.Exists(path))
        {
            // 首次使用：允许建立空目录状态，但这不是"损坏重建"。
            return new PluginCatalogLoadResult(new PluginCatalogState { Generation = 0, Plugins = [] }, false, null);
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new PluginCatalogLoadResult(null, true, $"目录状态无法读取：{error.GetBaseException().Message}");
        }

        PluginCatalogState? state;
        try
        {
            state = JsonSerializer.Deserialize<PluginCatalogState>(text);
        }
        catch (JsonException error)
        {
            return new PluginCatalogLoadResult(null, true, $"目录状态 JSON 损坏：{error.Message}");
        }

        if (state is null)
            return new PluginCatalogLoadResult(null, true, "目录状态为空且无法解析。");
        if (state.SchemaVersion != PluginCatalogState.CurrentSchemaVersion)
        {
            return new PluginCatalogLoadResult(null, true,
                $"目录状态版本 {state.SchemaVersion} 不是本产品支持的 {PluginCatalogState.CurrentSchemaVersion}：未读取、未重建。");
        }

        return new PluginCatalogLoadResult(state, false, null);
    }

    /// <summary>原子写入：临时文件 + File.Move(overwrite)。不直接改写目标文件。</summary>
    public static void Save(string directory, PluginCatalogState state)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, CatalogFileName);
        var temp = Path.Combine(directory, CatalogFileName + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Json), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public static PluginCatalogState ToState(IEnumerable<PluginRecord> plugins, long generation) =>
        new() { SchemaVersion = PluginCatalogState.CurrentSchemaVersion, Generation = generation, Plugins = plugins.ToArray() };
}
