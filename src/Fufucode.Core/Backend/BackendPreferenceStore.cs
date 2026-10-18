using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fufucode.Core.Backend;

/// <summary>全局后端偏好。默认内核为 Pi，与方案 B 的「全局默认」一致。</summary>
public sealed record BackendPreference(int Version = 1, EngineId DefaultBackend = EngineId.NativeCSharp);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(BackendPreference))]
internal partial class BackendPreferenceJsonContext : JsonSerializerContext;

/// <summary>
/// 全局默认后端的原子持久化存取。未知或损坏文件回退到默认（NativeCSharp），
/// 但保留原始文件供诊断，不静默覆盖用户数据。
/// </summary>
public sealed class BackendPreferenceStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private BackendPreference _current;

    public BackendPreferenceStore(AppDataLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        layout.EnsureCreated();
        _path = Path.Combine(layout.Root, "backend.json");
        _current = Load();
    }

    public event Action<BackendPreference>? Changed;

    public string? LastLoadError { get; private set; }

    public BackendPreference Current { get { lock (_gate) return _current; } }

    public EngineId DefaultBackend { get { lock (_gate) return _current.DefaultBackend; } }

    public void SetDefault(EngineId backend)
    {
        if (!Enum.IsDefined(backend)) throw new ArgumentOutOfRangeException(nameof(backend));
        Update(current => current with { DefaultBackend = backend });
    }

    public BackendPreference Update(Func<BackendPreference, BackendPreference> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        BackendPreference next;
        lock (_gate)
        {
            next = change(_current) with { Version = 1 };
            if (!Enum.IsDefined(next.DefaultBackend))
                throw new ArgumentException("Invalid default backend.", nameof(change));
            if (next == _current) return _current;
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(next, BackendPreferenceJsonContext.Default.BackendPreference));
                File.Move(temp, _path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            _current = next;
            LastLoadError = null;
        }

        Changed?.Invoke(next);
        return next;
    }

    private BackendPreference Load()
    {
        if (!File.Exists(_path)) return new BackendPreference();
        try
        {
            var json = File.ReadAllText(_path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Root must be an object.");
            var version = document.RootElement.TryGetProperty("version", out var value) ? value.GetInt32() : 0;
            if (version > 1) throw new InvalidOperationException("Backend preference was written by a newer app version.");
            var loaded = JsonSerializer.Deserialize(json, BackendPreferenceJsonContext.Default.BackendPreference)
                ?? throw new JsonException("Backend preference is empty.");
            if (!Enum.IsDefined(loaded.DefaultBackend))
                throw new JsonException("Unknown default backend.");
            return loaded;
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException or ArgumentException)
        {
            LastLoadError = error.GetType().Name;
            return new BackendPreference();
        }
    }
}
