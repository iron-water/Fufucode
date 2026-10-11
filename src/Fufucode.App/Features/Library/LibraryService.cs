using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core;

namespace Fufucode.App.Features.Library;

/// <summary>
/// 资料库模块的服务层（N2 阶段一 + 阶段二）。
/// 职责：收藏（引用/副本）、来源与版本追踪、标签、预览文本、全文索引与检索、导入导出、显式聊天引用载荷。
/// 只写资料库自有目录（&lt;root&gt;/library），不修改现有元数据、宿主或聊天会话。
/// 网页首版只保存链接或用户提供的快照，不抓取网络内容；删除条目绝不删除用户源文件。
/// </summary>
public sealed class LibraryService : IDisposable
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly LibraryStore _store;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private bool _disposed;

    public LibraryService(string libraryRoot) => _store = new LibraryStore(libraryRoot);

    /// <summary>接线用：资料库目录位于应用数据根之下，与 desktop.db 分离。</summary>
    public LibraryService(AppDataLayout layout) : this(Path.Combine(layout.Root, "library"))
    {
    }

    public string Root => _store.Root;
    public string DatabasePath => _store.DatabasePath;

    public IReadOnlyList<LibraryEntry> ListEntries() => _store.ListEntries();
    public IReadOnlyList<LibraryTagCount> ListTags() => _store.ListTags();
    public LibraryEntry? GetEntry(string id) => _store.GetEntry(id);
    public IReadOnlyList<LibraryVersion> ListVersions(string id) => _store.ListVersions(id);

    /// <summary>已提取的预览文本（未索引或不可读时返回 null）。</summary>
    public string? GetPreviewText(string id) => _store.GetEntryText(id);

    public async Task<LibraryImportOutcome> ImportAsync(LibraryImportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return request.Kind switch
            {
                LibraryEntryKind.WebPage => await ImportWebPageAsync(request, cancellationToken).ConfigureAwait(false),
                LibraryEntryKind.SessionSnippet or LibraryEntryKind.Text =>
                    await ImportInlineTextAsync(request, cancellationToken).ConfigureAwait(false),
                _ => await ImportFileAsync(request, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new LibraryImportOutcome(LibraryImportStatus.Failed, null, request.Title ?? "(未命名)",
                error.GetType().Name + "：" + error.Message);
        }
    }

    public async Task<IReadOnlyList<LibraryImportOutcome>> ImportManyAsync(
        IEnumerable<LibraryImportRequest> requests,
        IProgress<LibraryImportOutcome>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outcomes = new List<LibraryImportOutcome>();
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await ImportAsync(request, cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);
            progress?.Report(outcome);
        }
        return outcomes;
    }

    private async Task<LibraryImportOutcome> ImportFileAsync(LibraryImportRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return new LibraryImportOutcome(LibraryImportStatus.Failed, null, request.Title ?? "(未命名)", "缺少文件路径");
        var path = Path.GetFullPath(request.Path);
        if (!File.Exists(path))
            return new LibraryImportOutcome(LibraryImportStatus.Failed, null, request.Title ?? Path.GetFileName(path),
                "文件不存在：" + path);

        var hash = await ComputeHashAsync(path, cancellationToken).ConfigureAwait(false);
        var duplicate = _store.FindEntryIdByHash(hash);
        if (duplicate is not null)
        {
            var existing = _store.GetEntry(duplicate);
            return new LibraryImportOutcome(LibraryImportStatus.Duplicate, duplicate, existing?.Title ?? Path.GetFileName(path),
                "内容相同，已收藏过同名条目", duplicate);
        }

        var id = NewId();
        var info = new FileInfo(path);
        var title = string.IsNullOrWhiteSpace(request.Title) ? Path.GetFileName(path) : request.Title!.Trim();
        var captured = DateTimeOffset.UtcNow;
        string? storedPath = null;
        if (request.StorageMode == LibraryStorageMode.Copy)
            storedPath = await Task.Run(() => CopyIntoLibrary(id, 1, path, title), cancellationToken).ConfigureAwait(false);

        var entry = new LibraryEntry(id, title, request.Kind, request.StorageMode, path, null, storedPath,
            hash, info.Length, info.LastWriteTimeUtc, captured, captured,
            LibraryEntryStatus.Ok, captured, request.Note, LibraryTextState.Pending, null,
            NormalizeTags(request.Tags), 1);
        _store.InsertEntry(entry);
        _store.AddVersion(id, new LibraryVersion(1, hash, info.Length, info.LastWriteTimeUtc, captured, storedPath, "import"));
        await IndexEntryAsync(id, cancellationToken).ConfigureAwait(false);
        return new LibraryImportOutcome(LibraryImportStatus.Imported, id, title,
            request.StorageMode == LibraryStorageMode.Copy ? "已收藏为副本" : "已收藏为引用（不复制内容）");
    }

    private async Task<LibraryImportOutcome> ImportInlineTextAsync(LibraryImportRequest request, CancellationToken cancellationToken)
    {
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? "文本片段 " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : request.Title!.Trim();
        string? text = request.Text;
        if (text is null && !string.IsNullOrWhiteSpace(request.Path))
        {
            // 资料包（以及任何只给文件路径的调用方）按文本原样读回，
            // 保证导出/再导入后原文、来源大小与内容哈希一致。
            var path = Path.GetFullPath(request.Path!);
            if (!File.Exists(path))
                return new LibraryImportOutcome(LibraryImportStatus.Failed, null, title,
                    "文本内容文件不存在，未创建条目：" + path);
            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        if (string.IsNullOrEmpty(text))
            return new LibraryImportOutcome(LibraryImportStatus.Failed, null, title, "缺少文本内容，未创建条目");
        var content = text;
        var hash = HashText(content);
        var duplicate = _store.FindEntryIdByHash(hash);
        if (duplicate is not null)
        {
            var existing = _store.GetEntry(duplicate);
            return new LibraryImportOutcome(LibraryImportStatus.Duplicate, duplicate, existing?.Title ?? title,
                "内容相同，已收藏过相同片段", duplicate);
        }

        var id = NewId();
        var captured = DateTimeOffset.UtcNow;
        var storedPath = await Task.Run(() => WriteInlineCopy(id, 1, title, content), cancellationToken).ConfigureAwait(false);
        var kind = request.Kind;
        var entry = new LibraryEntry(id, title, kind, LibraryStorageMode.Copy, null, null, storedPath,
            hash, Encoding.UTF8.GetByteCount(content), null, captured, captured,
            LibraryEntryStatus.Ok, captured, request.Note, LibraryTextState.Pending, null,
            NormalizeTags(request.Tags), 1);
        _store.InsertEntry(entry);
        _store.AddVersion(id, new LibraryVersion(1, hash, entry.SourceSize, null, captured, storedPath, "import"));
        await IndexEntryAsync(id, cancellationToken).ConfigureAwait(false);
        return new LibraryImportOutcome(LibraryImportStatus.Imported, id, title, "已收藏文本片段");
    }

    private async Task<LibraryImportOutcome> ImportWebPageAsync(LibraryImportRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Url) && string.IsNullOrWhiteSpace(request.Path))
            return new LibraryImportOutcome(LibraryImportStatus.Failed, null, request.Title ?? "(未命名)", "缺少网页链接或快照文件");

        var url = string.IsNullOrWhiteSpace(request.Url) ? null : request.Url!.Trim();
        var snapshot = string.IsNullOrWhiteSpace(request.Path) ? null : Path.GetFullPath(request.Path!);
        if (snapshot is not null && !File.Exists(snapshot))
            return new LibraryImportOutcome(LibraryImportStatus.Failed, null, request.Title ?? "(未命名)", "快照文件不存在：" + snapshot);

        string hash;
        long size = 0;
        DateTimeOffset? modified = null;
        if (snapshot is not null)
        {
            hash = await ComputeHashAsync(snapshot, cancellationToken).ConfigureAwait(false);
            var info = new FileInfo(snapshot);
            size = info.Length;
            modified = info.LastWriteTimeUtc;
        }
        else
        {
            // 仅链接：以 URL 文本作为身份，绝不冒充已抓取的网页内容。
            hash = HashText("url:" + url);
        }

        var duplicate = _store.FindEntryIdByHash(hash);
        if (duplicate is not null)
        {
            var existing = _store.GetEntry(duplicate);
            return new LibraryImportOutcome(LibraryImportStatus.Duplicate, duplicate, existing?.Title ?? "(未命名)",
                "内容相同，已收藏过该链接或快照", duplicate);
        }

        var id = NewId();
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? (url is null ? Path.GetFileName(snapshot!) : url)
            : request.Title!.Trim();
        var captured = DateTimeOffset.UtcNow;
        var mode = snapshot is null ? LibraryStorageMode.Reference : LibraryStorageMode.Copy;
        var storedPath = snapshot is null ? null
            : await Task.Run(() => CopyIntoLibrary(id, 1, snapshot, title), cancellationToken).ConfigureAwait(false);
        var entry = new LibraryEntry(id, title, LibraryEntryKind.WebPage, mode, snapshot, url, storedPath,
            hash, size, modified, captured, captured, LibraryEntryStatus.Ok, captured, request.Note,
            LibraryTextState.Pending, null, NormalizeTags(request.Tags), 1);
        _store.InsertEntry(entry);
        _store.AddVersion(id, new LibraryVersion(1, hash, size, modified, captured, storedPath, "import"));
        await IndexEntryAsync(id, cancellationToken).ConfigureAwait(false);
        return new LibraryImportOutcome(LibraryImportStatus.Imported, id, title,
            snapshot is null ? "已收藏链接（未抓取网页内容）" : "已收藏网页快照副本");
    }

    /// <summary>索引单条目：提取文本、写预览、建 token。失败只影响索引状态，原件保留。</summary>
    public async Task IndexEntryAsync(string id, CancellationToken cancellationToken = default)
    {
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = _store.GetEntry(id);
            if (entry is null) return;
            var contentPath = entry.StoredPath ?? entry.SourcePath;
            if (contentPath is null)
            {
                _store.SetEntryText(id, string.Empty);
                _store.ReplaceEntryTokens(id, Array.Empty<string>());
                _store.UpdateEntryState(id, null, entry.Note, entry.ContentHash, entry.SourceSize,
                    entry.SourceModifiedUtc, entry.StoredPath, entry.Status,
                    entry.LastCheckedUtc ?? entry.AddedUtc, LibraryTextState.Unsupported,
                    "仅保存链接，未抓取网页内容，无法索引");
                return;
            }

            var extraction = await Task.Run(() => LibraryTextExtractor.Extract(contentPath), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            switch (extraction.Kind)
            {
                case LibraryExtractionKind.Text:
                    _store.SetEntryText(id, extraction.Text);
                    _store.ReplaceEntryTokens(id, LibraryTokens.Tokenize(extraction.Text));
                    _store.UpdateEntryState(id, null, entry.Note, entry.ContentHash, entry.SourceSize,
                        entry.SourceModifiedUtc, entry.StoredPath, entry.Status, entry.LastCheckedUtc ?? entry.AddedUtc,
                        LibraryTextState.Indexed, extraction.Note);
                    break;
                case LibraryExtractionKind.Unsupported:
                    _store.SetEntryText(id, string.Empty);
                    _store.ReplaceEntryTokens(id, Array.Empty<string>());
                    _store.UpdateEntryState(id, null, entry.Note, entry.ContentHash, entry.SourceSize,
                        entry.SourceModifiedUtc, entry.StoredPath, entry.Status, entry.LastCheckedUtc ?? entry.AddedUtc,
                        LibraryTextState.Unsupported, extraction.Note);
                    break;
                default:
                    _store.ReplaceEntryTokens(id, Array.Empty<string>());
                    _store.UpdateEntryState(id, null, entry.Note, entry.ContentHash, entry.SourceSize,
                        entry.SourceModifiedUtc, entry.StoredPath, entry.Status, entry.LastCheckedUtc ?? entry.AddedUtc,
                        LibraryTextState.Failed, extraction.Note);
                    break;
            }
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public async Task<LibraryIndexProgress> IndexPendingAsync(
        IProgress<LibraryIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pending = _store.PendingIndexEntries();
        var completed = 0;
        foreach (var (id, title) in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await IndexEntryAsync(id, cancellationToken).ConfigureAwait(false);
            completed++;
            progress?.Report(new LibraryIndexProgress(completed, pending.Count, title));
        }
        return new LibraryIndexProgress(pending.Count, pending.Count, "待索引条目处理完毕");
    }

    /// <summary>全量重建索引（不改变条目与副本，只重算文本与 token）。</summary>
    public async Task<LibraryIndexProgress> RebuildIndexAsync(
        IProgress<LibraryIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var all = _store.AllEntryIds();
        var completed = 0;
        foreach (var (id, title) in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await IndexEntryAsync(id, cancellationToken).ConfigureAwait(false);
            completed++;
            progress?.Report(new LibraryIndexProgress(completed, all.Count, title));
        }
        return new LibraryIndexProgress(all.Count, all.Count, "索引重建完成");
    }

    /// <summary>
    /// 检查来源状态。acceptChange=false 时只标记变化，不覆盖已记录版本（不冒充原内容）；
    /// acceptChange=true 时记录新版本并在副本模式下保存新内容快照，随后重索引。
    /// </summary>
    public async Task<LibrarySourceState> RefreshStatusAsync(string id, bool acceptChange,
        CancellationToken cancellationToken = default)
    {
        var entry = _store.GetEntry(id) ?? throw new InvalidOperationException("资料条目不存在：" + id);
        var now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(entry.SourcePath))
        {
            var state = new LibrarySourceState(entry.SourceUrl is not null, false, entry.SourceSize, null,
                entry.SourceUrl is null ? "条目没有外部来源" : "仅保存链接，不联网检查");
            return state;
        }

        var path = entry.SourcePath!;
        if (!File.Exists(path))
        {
            _store.UpdateEntryState(id, null, entry.Note, entry.ContentHash, entry.SourceSize,
                entry.SourceModifiedUtc, entry.StoredPath, LibraryEntryStatus.Missing, now, entry.TextState, entry.TextError);
            return new LibrarySourceState(false, false, entry.SourceSize, null,
                entry.HasLocalCopy ? "来源文件已不存在；库内副本仍可读" : "来源文件已不存在，内容不可读");
        }

        var info = new FileInfo(path);
        var modified = info.LastWriteTimeUtc;
        var unchanged = info.Length == entry.SourceSize &&
            entry.SourceModifiedUtc is { } recorded && modified == recorded;
        if (unchanged)
        {
            _store.UpdateEntryState(id, null, entry.Note, entry.ContentHash, entry.SourceSize,
                entry.SourceModifiedUtc, entry.StoredPath, LibraryEntryStatus.Ok, now, entry.TextState, entry.TextError);
            return new LibrarySourceState(true, false, info.Length, modified, "来源未变化");
        }

        var hash = await ComputeHashAsync(path, cancellationToken).ConfigureAwait(false);
        if (hash == entry.ContentHash)
        {
            // 只改了时间戳/长度元数据但内容一致，更新记录即可。
            _store.UpdateEntryState(id, null, entry.Note, hash, info.Length, modified, entry.StoredPath,
                LibraryEntryStatus.Ok, now, entry.TextState, entry.TextError);
            return new LibrarySourceState(true, false, info.Length, modified, "时间戳变化但内容哈希一致");
        }

        if (!acceptChange)
        {
            _store.UpdateEntryState(id, null, entry.Note, entry.ContentHash, entry.SourceSize,
                entry.SourceModifiedUtc, entry.StoredPath, LibraryEntryStatus.Changed, now, entry.TextState, entry.TextError);
            return new LibrarySourceState(true, true, info.Length, modified, "来源内容已变化，尚未同步到资料库");
        }

        var nextVersion = entry.VersionCount + 1;
        var storedPath = entry.StorageMode == LibraryStorageMode.Copy
            ? await Task.Run(() => CopyIntoLibrary(id, nextVersion, path, entry.Title), cancellationToken).ConfigureAwait(false)
            : entry.StoredPath;
        _store.AddVersion(id, new LibraryVersion(nextVersion, hash, info.Length, modified, now, storedPath, "accepted-change"));
        _store.UpdateEntryState(id, null, entry.Note, hash, info.Length, modified, storedPath,
            LibraryEntryStatus.Ok, now, LibraryTextState.Pending, null);
        await IndexEntryAsync(id, cancellationToken).ConfigureAwait(false);
        return new LibrarySourceState(true, true, info.Length, modified, "已同步来源变化并记录新版本 v" + nextVersion);
    }

    public async Task<IReadOnlyList<LibrarySourceState>> RefreshAllAsync(bool acceptChange,
        CancellationToken cancellationToken = default)
    {
        var results = new List<LibrarySourceState>();
        foreach (var entry in _store.ListEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RefreshStatusAsync(entry.Id, acceptChange, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    public void AddTag(string id, string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        _store.AddTag(id, tag);
    }

    public void RemoveTag(string id, string tag) => _store.RemoveTag(id, tag);

    /// <summary>全文检索：token 命中排序 + 标签过滤；返回带片段的结果。</summary>
    public IReadOnlyList<LibrarySearchHit> Search(string? query, IEnumerable<string>? tags = null, int limit = 100)
    {
        var required = tags?.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim())
            .Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        var entries = _store.ListEntries();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var tokens = LibraryTokens.TokenizeQuery(query);
            if (tokens.Count == 0) return Array.Empty<LibrarySearchHit>();
            var scored = _store.SearchTokens(tokens, Math.Max(limit * 4, 200));
            var byId = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
            var hits = new List<LibrarySearchHit>();
            foreach (var (entryId, score) in scored)
            {
                if (!byId.TryGetValue(entryId, out var entry)) continue;
                if (!MatchesTags(entry, required)) continue;
                hits.Add(new LibrarySearchHit(entry, score, BuildSnippet(entryId, tokens)));
                if (hits.Count >= limit) break;
            }
            return hits;
        }

        return entries.Where(entry => MatchesTags(entry, required))
            .Take(limit)
            .Select(entry => new LibrarySearchHit(entry, 0, null))
            .ToList();
    }

    private static bool MatchesTags(LibraryEntry entry, IReadOnlyList<string> required) =>
        required.Count == 0 || required.All(tag => entry.Tags.Contains(tag, StringComparer.Ordinal));

    private string? BuildSnippet(string id, IReadOnlyList<string> tokens, int radius = 60)
    {
        var text = _store.GetEntryText(id);
        if (string.IsNullOrEmpty(text)) return null;
        var position = -1;
        foreach (var token in tokens)
        {
            var index = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (position < 0 || index < position)) position = index;
        }
        if (position < 0) return null;
        var start = Math.Max(0, position - radius);
        var length = Math.Min(text.Length - start, radius * 2);
        var snippet = text.Substring(start, length).Replace("\r", string.Empty).Replace('\n', ' ');
        return (start > 0 ? "…" : string.Empty) + snippet + (start + length < text.Length ? "…" : string.Empty);
    }

    /// <summary>
    /// 显式聊天引用载荷：聊天接线（G1 后由 Codex 接入输入框）只消费本方法结果，
    /// 不自动把整个资料库送给模型。
    /// </summary>
    public LibraryCitation? BuildCitation(string id, string? query = null, int maxChars = 600)
    {
        var entry = _store.GetEntry(id);
        if (entry is null) return null;
        var text = _store.GetEntryText(id) ?? string.Empty;
        var snippet = text;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var tokens = LibraryTokens.TokenizeQuery(query);
            snippet = BuildSnippet(id, tokens, Math.Max(60, maxChars / 2)) ?? text;
        }
        if (snippet.Length > maxChars) snippet = snippet[..maxChars] + "…";
        if (snippet.Length == 0)
            snippet = "（无可用文本：" + (entry.TextError ?? entry.TextStateText) + "）";
        return new LibraryCitation(entry.Id, entry.Title, entry.SourceDisplay, entry.ContentHash, snippet, DateTimeOffset.UtcNow);
    }

    /// <summary>导出条目（元数据 + 可读内容）到目录，生成 manifest.json 以便再导入。</summary>
    public async Task<LibraryExportResult> ExportAsync(IEnumerable<string> entryIds, string destination,
        CancellationToken cancellationToken = default)
    {
        var ids = entryIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) throw new InvalidOperationException("没有选择要导出的资料条目。");
        var target = Path.GetFullPath(destination);
        Directory.CreateDirectory(target);
        var warnings = new List<string>();
        var manifest = new List<BundleItem>();
        var contentFiles = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = _store.GetEntry(id);
            if (entry is null)
            {
                warnings.Add("条目不存在，已跳过：" + id);
                continue;
            }
            var entryDirectory = Path.Combine(target, entry.Id);
            Directory.CreateDirectory(entryDirectory);
            string? contentFile = null;
            var source = ResolveContentPath(entry);
            if (source is not null && File.Exists(source))
            {
                contentFile = "content" + Path.GetExtension(entry.SourcePath ?? source);
                await Task.Run(() => File.Copy(source, Path.Combine(entryDirectory, contentFile), overwrite: true), cancellationToken).ConfigureAwait(false);
                contentFiles++;
            }
            else
            {
                warnings.Add($"《{entry.Title}》没有可导出的内容（{entry.SourceDisplay}）");
            }
            var versions = _store.ListVersions(id).Select(version => new BundleVersion(
                version.Number, version.ContentHash, version.Size, version.SourceModifiedUtc, version.CapturedUtc, version.Origin)).ToList();
            var item = new BundleItem(entry.Id, entry.Title, (int)entry.Kind, (int)entry.StorageMode, entry.SourcePath,
                entry.SourceUrl, entry.ContentHash, entry.SourceSize, entry.AddedUtc, entry.Note,
                entry.Tags.ToList(), contentFile, versions);
            manifest.Add(item);
            await File.WriteAllTextAsync(Path.Combine(entryDirectory, "entry.json"),
                JsonSerializer.Serialize(item, ManifestJson), cancellationToken).ConfigureAwait(false);
        }

        var bundle = new Bundle(1, DateTimeOffset.UtcNow, manifest);
        var manifestPath = Path.Combine(target, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(bundle, ManifestJson), cancellationToken)
            .ConfigureAwait(false);
        return new LibraryExportResult(manifestPath, manifest.Count, contentFiles, warnings);
    }

    /// <summary>导入之前导出的资料包。有内容文件时按副本导入，否则按原来源引用导入。</summary>
    public async Task<LibraryBundleImportResult> ImportBundleAsync(string bundleDirectory,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(Path.GetFullPath(bundleDirectory), "manifest.json");
        if (!File.Exists(manifestPath))
            return new LibraryBundleImportResult(0, 0, 1, new[] { "找不到 manifest.json：" + manifestPath });
        var bundle = JsonSerializer.Deserialize<Bundle>(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        if (bundle is null || bundle.FormatVersion != 1)
            return new LibraryBundleImportResult(0, 0, 1, new[] { "不支持的资料包格式" });

        var imported = 0;
        var duplicates = 0;
        var failed = 0;
        var messages = new List<string>();
        foreach (var item in bundle.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contentPath = item.ContentFile is null ? null : Path.Combine(Path.GetFullPath(bundleDirectory), item.Id, item.ContentFile);
            var request = contentPath is not null && File.Exists(contentPath)
                ? new LibraryImportRequest((LibraryEntryKind)item.Kind, LibraryStorageMode.Copy, contentPath, item.SourceUrl,
                    null, item.Title, item.Note, item.Tags)
                : new LibraryImportRequest((LibraryEntryKind)item.Kind, LibraryStorageMode.Reference, item.SourcePath, item.SourceUrl,
                    null, item.Title, item.Note, item.Tags);
            var outcome = await ImportAsync(request, cancellationToken).ConfigureAwait(false);
            switch (outcome.Status)
            {
                case LibraryImportStatus.Imported: imported++; break;
                case LibraryImportStatus.Duplicate: duplicates++; break;
                default: failed++; break;
            }
            if (!outcome.IsImported) messages.Add($"《{item.Title}》：{outcome.Message}");
        }
        return new LibraryBundleImportResult(imported, duplicates, failed, messages);
    }

    /// <summary>
    /// 删除条目：默认只删库内记录与副本，绝不删除用户源文件。
    /// deleteCopies=false 时保留副本文件（用于排障），条目记录仍被删除。
    /// </summary>
    public bool DeleteEntry(string id, bool deleteCopies = true)
    {
        var entry = _store.GetEntry(id);
        if (entry is null) return false;
        var copyDirectory = _store.CopyVersionDirectory(id, 1);
        var entryCopyRoot = Path.GetDirectoryName(copyDirectory);
        var removed = _store.DeleteEntry(id);
        if (removed && deleteCopies && entryCopyRoot is not null && Directory.Exists(entryCopyRoot))
        {
            try { Directory.Delete(entryCopyRoot, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 副本清理失败不回滚条目删除，也不触碰源文件。
            }
        }
        return removed;
    }

    private string? ResolveContentPath(LibraryEntry entry) =>
        entry.StoredPath is not null && File.Exists(entry.StoredPath) ? entry.StoredPath
        : entry.SourcePath is not null && File.Exists(entry.SourcePath) ? entry.SourcePath
        : null;

    private string CopyIntoLibrary(string entryId, int version, string sourcePath, string title)
    {
        var directory = _store.CopyVersionDirectory(entryId, version);
        Directory.CreateDirectory(directory);
        var fileName = SanitizeFileName(Path.GetFileName(sourcePath));
        if (fileName.Length == 0) fileName = "content";
        var destination = Path.Combine(directory, fileName);
        File.Copy(sourcePath, destination, overwrite: true);
        return destination;
    }

    private string WriteInlineCopy(string entryId, int version, string title, string text)
    {
        var directory = _store.CopyVersionDirectory(entryId, version);
        Directory.CreateDirectory(directory);
        var fileName = SanitizeFileName(title);
        if (fileName.Length == 0) fileName = "snippet";
        if (Path.GetExtension(fileName).Length == 0) fileName += ".md";
        var destination = Path.Combine(directory, fileName);
        File.WriteAllText(destination, text, new UTF8Encoding(false));
        return destination;
    }

    internal static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
            builder.Append(invalid.Contains(character) ? '_' : character);
        return builder.ToString().Trim().TrimEnd('.');
    }

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags) =>
        tags is null ? Array.Empty<string>()
            : tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim())
                .Distinct(StringComparer.Ordinal).ToArray();

    private static string NewId() => Guid.NewGuid().ToString("N");

    internal static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string HashText(string text)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
        _indexGate.Dispose();
    }

    private sealed record Bundle(int FormatVersion, DateTimeOffset ExportedUtc, List<BundleItem> Entries);

    private sealed record BundleItem(
        string Id, string Title, int Kind, int StorageMode, string? SourcePath, string? SourceUrl,
        string? ContentHash, long Size, DateTimeOffset AddedUtc, string? Note, List<string> Tags,
        string? ContentFile, List<BundleVersion> Versions);

    private sealed record BundleVersion(
        int Number, string ContentHash, long Size, DateTimeOffset? SourceModifiedUtc,
        DateTimeOffset CapturedUtc, string Origin);
}
