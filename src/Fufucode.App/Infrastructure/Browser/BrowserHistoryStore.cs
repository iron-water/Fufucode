using System;
using System.Collections.Generic;
using System.IO;
using IOPath = System.IO.Path;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fufucode.App.Infrastructure.Browser;

/// <summary>一条真实浏览记录：同一地址只保留一条，记录最近访问时间与累计次数。</summary>
public sealed record BrowserHistoryEntry(string Address, string Title, DateTimeOffset LastVisitedAt, int VisitCount);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<BrowserHistoryEntry>))]
internal partial class BrowserHistoryJsonContext : JsonSerializerContext;

/// <summary>
/// 浏览历史落盘（<c>&lt;dataRoot&gt;/browser-history.json</c>）。
/// 只记录导航真实成功完成的 http/https 页面，不记录 file:、about:blank 或失败页。
/// </summary>
public sealed class BrowserHistoryStore
{
    public const int MaxEntries = 2000;
    private static readonly Dictionary<string, BrowserHistoryStore> Shared = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<BrowserHistoryEntry> _entries;

    public BrowserHistoryStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var root = IOPath.GetFullPath(dataRoot);
        Directory.CreateDirectory(root);
        _path = IOPath.Combine(root, "browser-history.json");
        _entries = Load();
    }

    public static BrowserHistoryStore ForRoot(string dataRoot)
    {
        var key = IOPath.GetFullPath(dataRoot);
        lock (Shared)
        {
            if (!Shared.TryGetValue(key, out var store)) Shared[key] = store = new BrowserHistoryStore(key);
            return store;
        }
    }

    public event Action? Changed;

    public string Path => _path;
    public string? LastLoadError { get; private set; }

    public IReadOnlyList<BrowserHistoryEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }

    /// <summary>记录一次真实成功导航；同一地址移到最前并累计次数。</summary>
    public void Record(string? address, string? title)
    {
        if (!TryNormalize(address, out var normalized)) return;
        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(entry => Same(entry.Address, normalized));
            if (existing is not null) _entries.Remove(existing);
            var resolvedTitle = string.IsNullOrWhiteSpace(title) ? existing?.Title ?? string.Empty : title.Trim();
            _entries.Insert(0, new BrowserHistoryEntry(normalized, resolvedTitle, DateTimeOffset.Now,
                (existing?.VisitCount ?? 0) + 1));
            if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>导航完成后标题才到：补全同一地址的最新标题，不新增记录。</summary>
    public void UpdateTitle(string? address, string? title)
    {
        if (!TryNormalize(address, out var normalized) || string.IsNullOrWhiteSpace(title)) return;
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => Same(entry.Address, normalized));
            if (index < 0 || _entries[index].Title == title.Trim()) return;
            _entries[index] = _entries[index] with { Title = title.Trim() };
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>地址栏下拉用：按标题/地址匹配，最近访问优先；query 为空时返回最近记录。</summary>
    public IReadOnlyList<BrowserHistoryEntry> Search(string? query, int limit = 8)
    {
        var text = (query ?? string.Empty).Trim();
        lock (_gate)
        {
            var source = text.Length == 0
                ? _entries
                : _entries.Where(entry =>
                    entry.Address.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    entry.Title.Contains(text, StringComparison.OrdinalIgnoreCase));
            return source.Take(Math.Max(1, limit)).ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return;
            _entries.Clear();
            Persist();
        }
        Changed?.Invoke();
    }

    public void Remove(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        lock (_gate)
        {
            if (_entries.RemoveAll(entry => Same(entry.Address, address)) == 0) return;
            Persist();
        }
        Changed?.Invoke();
    }

    private static bool TryNormalize(string? address, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        normalized = uri.AbsoluteUri;
        return true;
    }

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private void Persist()
    {
        try
        {
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries, BrowserHistoryJsonContext.Default.ListBrowserHistoryEntry));
                File.Move(temp, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            LastLoadError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            LastLoadError = error.GetType().Name + ": " + error.Message;
        }
    }

    private List<BrowserHistoryEntry> Load()
    {
        LastLoadError = null;
        if (!File.Exists(_path)) return new List<BrowserHistoryEntry>();
        try
        {
            var loaded = JsonSerializer.Deserialize(File.ReadAllText(_path),
                BrowserHistoryJsonContext.Default.ListBrowserHistoryEntry);
            if (loaded is null) throw new JsonException("历史为空。");
            return loaded.Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Address))
                .Take(MaxEntries).ToList();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastLoadError = error.GetType().Name + ": " + error.Message;
            return new List<BrowserHistoryEntry>();
        }
    }
}
