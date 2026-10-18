using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fufucode.App.Infrastructure.Browser;

/// <summary>一条真实的下载记录；路径来自 WebView2 的下载结果，不做臆造。</summary>
public sealed record BrowserDownloadEntry(
    string FileName,
    string FullPath,
    long TotalBytes,
    string State,
    DateTimeOffset StartedAt);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<BrowserDownloadEntry>))]
internal partial class BrowserDownloadHistoryJsonContext : JsonSerializerContext;

/// <summary>
/// 下载历史（<c>&lt;dataRoot&gt;/browser-downloads.json</c>）：只记录真实发生的下载，
/// 默认最多保留 <see cref="MaxEntries"/> 条，最新在前。写入失败只记录错误，不影响下载本身。
/// </summary>
public sealed class BrowserDownloadHistory
{
    public const int MaxEntries = 200;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<BrowserDownloadEntry> _entries;

    public BrowserDownloadHistory(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var root = System.IO.Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(root);
        _path = System.IO.Path.Combine(root, "browser-downloads.json");
        _entries = Load();
    }

    public event Action? Changed;

    public string Path => _path;
    public string? LastLoadError { get; private set; }

    public IReadOnlyList<BrowserDownloadEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }

    public void Add(BrowserDownloadEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Insert(0, entry with
            {
                FileName = string.IsNullOrWhiteSpace(entry.FileName) ? "(未知文件)" : entry.FileName.Trim(),
                TotalBytes = entry.TotalBytes < 0 ? 0 : entry.TotalBytes,
                State = string.IsNullOrWhiteSpace(entry.State) ? "unknown" : entry.State.Trim(),
            });
            if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            Persist();
        }
        Changed?.Invoke();
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

    public void RemoveMissing()
    {
        lock (_gate)
        {
            var before = _entries.Count;
            _entries.RemoveAll(entry => entry.FullPath.Length > 0 && !File.Exists(entry.FullPath));
            if (_entries.Count == before) return;
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>按扩展名推断下载默认文件名；目录由调用方决定。目录名本身不在这里臆造。</summary>
    public string Summary => Entries.Count == 0 ? "暂无下载记录" : $"最近 {Entries.Count} 条下载记录";

    private void Persist()
    {
        try
        {
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries,
                    BrowserDownloadHistoryJsonContext.Default.ListBrowserDownloadEntry));
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

    private List<BrowserDownloadEntry> Load()
    {
        LastLoadError = null;
        if (!File.Exists(_path)) return new List<BrowserDownloadEntry>();
        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize(json, BrowserDownloadHistoryJsonContext.Default.ListBrowserDownloadEntry);
            if (loaded is null) throw new JsonException("下载历史为空。");
            return loaded.Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.FileName))
                .Take(MaxEntries).ToList();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastLoadError = error.GetType().Name + ": " + error.Message;
            return new List<BrowserDownloadEntry>();
        }
    }
}
