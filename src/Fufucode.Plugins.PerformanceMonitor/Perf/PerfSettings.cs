using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fufucode.Core.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>插件自管设置（保留期、采集模式、环形缓冲、命令文本）。</summary>
public sealed record PerfSettings
{
    public string Mode { get; init; } = "standard";
    public bool CaptureCommandText { get; init; } = true;
    public int RetentionDays { get; init; } = 7;
    public int RingCapacity { get; init; } = 200_000;
    public int SegmentMb { get; init; } = 64;

    public const string FileName = "perf-settings.json";

    [System.Text.Json.Serialization.JsonIgnore]
    public PerfMode ModeValue => Mode switch
    {
        "detailed" => PerfMode.Detailed,
        "off" => PerfMode.Off,
        _ => PerfMode.Standard,
    };

    public PerfSettings Normalize() => this with
    {
        Mode = Mode is "off" or "standard" or "detailed" ? Mode : "standard",
        RetentionDays = Math.Clamp(RetentionDays, 0, 365),
        RingCapacity = Math.Clamp(RingCapacity, 10_000, 1_000_000),
        SegmentMb = Math.Clamp(SegmentMb, 8, 1024),
    };

    public static PerfSettings Load(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, FileName);
            if (!File.Exists(path)) return new PerfSettings();
            return (JsonSerializer.Deserialize(File.ReadAllText(path), PerfSettingsJsonContext.Default.PerfSettings)
                    ?? new PerfSettings()).Normalize();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // 设置文件损坏不阻止插件工作：退回默认值。
            return new PerfSettings();
        }
    }

    public void Save(string dataRoot)
    {
        System.IO.Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, PerfSettingsJsonContext.Default.PerfSettings));
        File.Move(temp, path, overwrite: true);
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true,
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(PerfSettings))]
public partial class PerfSettingsJsonContext : JsonSerializerContext;
