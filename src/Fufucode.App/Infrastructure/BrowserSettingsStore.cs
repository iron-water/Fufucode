using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fufucode.App.Infrastructure;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BrowserSettings))]
internal partial class BrowserSettingsJsonContext : JsonSerializerContext;

/// <summary>
/// 内置浏览器设置的原子持久化：<c>&lt;dataRoot&gt;/browser-settings.json</c>。
/// 写入先落临时文件再替换，进程崩溃不会留下半份配置；未知版本或损坏文件保留原文件并回退默认值，
/// 同时通过 <see cref="LastLoadError"/> 如实报告，不静默覆盖用户数据。
/// </summary>
public sealed class BrowserSettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private BrowserSettings _current;

    public BrowserSettingsStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var root = System.IO.Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(root);
        _path = System.IO.Path.Combine(root, "browser-settings.json");
        _current = Load();
    }

    public event Action<BrowserSettings>? Changed;

    /// <summary>文件路径；供设置页显示与诊断使用。</summary>
    public string Path => _path;

    /// <summary>上次加载失败原因；null 表示文件缺失或读取成功。</summary>
    public string? LastLoadError { get; private set; }

    /// <summary>文件当前是否存在（自测/诊断断言用）。</summary>
    public bool Exists => File.Exists(_path);

    public BrowserSettings Current { get { lock (_gate) return _current; } }

    /// <summary>原子写入一份新配置；返回实际生效值。序列化失败不改变内存与磁盘上的旧值。</summary>
    public BrowserSettings Save(BrowserSettings next)
    {
        ArgumentNullException.ThrowIfNull(next);
        BrowserSettings normalized;
        string text;
        try
        {
            normalized = next.Normalize();
            text = JsonSerializer.Serialize(normalized, BrowserSettingsJsonContext.Default.BrowserSettings);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException)
        {
            LastLoadError = error.GetType().Name + ": " + error.Message;
            return Current;
        }

        lock (_gate)
        {
            if (normalized == _current)
            {
                LastLoadError = null;
                return _current;
            }
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, text);
                File.Move(temp, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            _current = normalized;
            LastLoadError = null;
        }
        Changed?.Invoke(_current);
        return _current;
    }

    public BrowserSettings Update(Func<BrowserSettings, BrowserSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Save(change(Current));
    }

    /// <summary>重新从磁盘读取（外部改动或测试夹具写文件后调用）。</summary>
    public BrowserSettings Reload()
    {
        var loaded = Load();
        lock (_gate) _current = loaded;
        Changed?.Invoke(_current);
        return _current;
    }

    private BrowserSettings Load()
    {
        LastLoadError = null;
        if (!File.Exists(_path)) return BrowserSettings.Default;
        try
        {
            var json = File.ReadAllText(_path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("根节点必须是对象。");
            var version = document.RootElement.TryGetProperty("version", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.Number
                ? versionElement.GetInt32() : BrowserSettings.CurrentVersion;
            if (version > BrowserSettings.CurrentVersion)
                throw new InvalidOperationException($"浏览器设置由更新的版本写入（{version}），保留原文件不覆盖。");
            var loaded = JsonSerializer.Deserialize(json, BrowserSettingsJsonContext.Default.BrowserSettings)
                ?? throw new JsonException("浏览器设置为空。");
            return loaded.Normalize();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            LastLoadError = error.GetType().Name + ": " + error.Message;
            return BrowserSettings.Default;
        }
    }
}
