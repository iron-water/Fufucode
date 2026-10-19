using System;
using System.Collections.Generic;
using System.IO;
using IOPath = System.IO.Path;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fufucode.App.Infrastructure.Browser;

/// <summary>一条真实收藏；标题可为空（收藏时页面尚未给出标题）。</summary>
public sealed record BrowserBookmarkEntry(string Address, string Title, DateTimeOffset CreatedAt);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<BrowserBookmarkEntry>))]
internal partial class BrowserBookmarkJsonContext : JsonSerializerContext;

/// <summary>
/// 收藏落盘（<c>&lt;dataRoot&gt;/browser-bookmarks.json</c>）。与下载历史同构：
/// 原子写入、最多 500 条、读取失败保留原文件并如实报告。
/// </summary>
public sealed class BrowserBookmarkStore
{
    public const int MaxEntries = 500;
    private static readonly Dictionary<string, BrowserBookmarkStore> Shared = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<BrowserBookmarkEntry> _entries;

    public BrowserBookmarkStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var root = IOPath.GetFullPath(dataRoot);
        Directory.CreateDirectory(root);
        _path = IOPath.Combine(root, "browser-bookmarks.json");
        _entries = Load();
    }

    /// <summary>同一数据根复用同一实例，工作区浏览器与文件预览浏览器看到同一份收藏。</summary>
    public static BrowserBookmarkStore ForRoot(string dataRoot)
    {
        var key = IOPath.GetFullPath(dataRoot);
        lock (Shared)
        {
            if (!Shared.TryGetValue(key, out var store)) Shared[key] = store = new BrowserBookmarkStore(key);
            return store;
        }
    }

    public event Action? Changed;

    public string Path => _path;
    public string? LastLoadError { get; private set; }

    public IReadOnlyList<BrowserBookmarkEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }

    public bool Contains(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        lock (_gate) return _entries.Any(entry => Same(entry.Address, address));
    }

    /// <summary>切换收藏；返回切换后的状态（true = 已收藏）。无有效地址时不写入。</summary>
    public bool Toggle(string? address, string? title)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        bool added;
        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(entry => Same(entry.Address, address));
            if (existing is not null) { _entries.Remove(existing); added = false; }
            else
            {
                _entries.Insert(0, new BrowserBookmarkEntry(address.Trim(), (title ?? string.Empty).Trim(), DateTimeOffset.Now));
                if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
                added = true;
            }
            Persist();
        }
        Changed?.Invoke();
        return added;
    }

    public bool Remove(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        lock (_gate)
        {
            var removed = _entries.RemoveAll(entry => Same(entry.Address, address));
            if (removed == 0) return false;
            Persist();
        }
        Changed?.Invoke();
        return true;
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

    /// <summary>页面标题在导航完成后才到达：用真实标题补全对应收藏，地址不变。</summary>
    public void UpdateTitle(string? address, string? title)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(title)) return;
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => Same(entry.Address, address));
            if (index < 0 || _entries[index].Title == title.Trim()) return;
            _entries[index] = _entries[index] with { Title = title.Trim() };
            Persist();
        }
        Changed?.Invoke();
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
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries, BrowserBookmarkJsonContext.Default.ListBrowserBookmarkEntry));
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

    private List<BrowserBookmarkEntry> Load()
    {
        LastLoadError = null;
        if (!File.Exists(_path)) return new List<BrowserBookmarkEntry>();
        try
        {
            var loaded = JsonSerializer.Deserialize(File.ReadAllText(_path),
                BrowserBookmarkJsonContext.Default.ListBrowserBookmarkEntry);
            if (loaded is null) throw new JsonException("收藏为空。");
            return loaded.Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Address))
                .Take(MaxEntries).ToList();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastLoadError = error.GetType().Name + ": " + error.Message;
            return new List<BrowserBookmarkEntry>();
        }
    }
}
