using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fufucode.App.Features.SettingsExperience;

/// <summary>
/// 交互式升降级的持久化偏好：
/// <list type="bullet">
/// <item><c>Promoted</c>：从「其他」升级到一级侧栏（大功能组）的小功能 id。</item>
/// <item><c>Demoted</c>：从「大功能」降级收纳进「其他」的大功能 id。</item>
/// </list>
/// 两个集合按目录原始层级互斥：一个条目只可能出现在其中一个集合里。
/// </summary>
public sealed record SettingsNavPreference
{
    public IReadOnlyList<string> Promoted { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Demoted { get; init; } = Array.Empty<string>();

    public static SettingsNavPreference Empty { get; } = new();

    /// <summary>去掉未知 id、重复项与跨层级的非法项，保持输入顺序。</summary>
    public SettingsNavPreference Normalize()
    {
        var promoted = Clean(Promoted, SettingsNavTier.Other);
        var demoted = Clean(Demoted, SettingsNavTier.Features);
        if (promoted.Count == Promoted.Count && demoted.Count == Demoted.Count &&
            promoted.SequenceEqual(Promoted, StringComparer.Ordinal) &&
            demoted.SequenceEqual(Demoted, StringComparer.Ordinal))
            return this;
        return new SettingsNavPreference { Promoted = promoted, Demoted = demoted };
    }

    private static IReadOnlyList<string> Clean(IReadOnlyList<string> source, SettingsNavTier requiredOrigin)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(source.Count);
        foreach (var raw in source)
        {
            var id = raw?.Trim();
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            if (SettingsNavCatalog.TierOf(id) != requiredOrigin) continue;
            result.Add(id);
        }
        return result;
    }

    /// <summary>某条目的当前生效层级（目录层级叠加偏好）。</summary>
    public SettingsNavTier EffectiveTier(string id)
    {
        if (Promoted.Contains(id, StringComparer.Ordinal)) return SettingsNavTier.Features;
        if (Demoted.Contains(id, StringComparer.Ordinal)) return SettingsNavTier.Other;
        return SettingsNavCatalog.TierOf(id) ?? SettingsNavTier.Other;
    }

    /// <summary>条目能否参与升降级（基础类固定，不能移动）。</summary>
    public static bool CanMove(string id) =>
        SettingsNavCatalog.TierOf(id) is SettingsNavTier.Features or SettingsNavTier.Other;

    /// <summary>
    /// 在「其他」与「大功能」之间翻转某条目：生效层级为大功能时收纳进「其他」，
    /// 生效层级为「其他」时升级到一级侧栏。基础类或未知 id 原样返回。
    /// </summary>
    public SettingsNavPreference Toggle(string id)
    {
        var origin = SettingsNavCatalog.TierOf(id);
        if (origin is not (SettingsNavTier.Features or SettingsNavTier.Other)) return this;
        var toFeatures = EffectiveTier(id) == SettingsNavTier.Other;
        var promoted = new List<string>(Promoted);
        var demoted = new List<string>(Demoted);
        if (toFeatures)
        {
            demoted.RemoveAll(x => string.Equals(x, id, StringComparison.Ordinal));
            if (origin == SettingsNavTier.Other && !promoted.Contains(id, StringComparer.Ordinal)) promoted.Add(id);
        }
        else
        {
            promoted.RemoveAll(x => string.Equals(x, id, StringComparison.Ordinal));
            if (origin == SettingsNavTier.Features && !demoted.Contains(id, StringComparer.Ordinal)) demoted.Add(id);
        }
        return new SettingsNavPreference { Promoted = promoted, Demoted = demoted }.Normalize();
    }
}

/// <summary>
/// 升降级偏好的落盘存储：<c>&lt;dataRoot&gt;/settings-nav.json</c>。原子写入，损坏文件不覆盖。
/// dataRoot 为空（mock / 无活动会话）时退化为内存态，只读不落盘。
/// </summary>
public sealed class SettingsNavPreferenceStore
{
    public const string FileName = "settings-nav.json";

    private readonly string? _path;
    private readonly object _gate = new();

    public SettingsNavPreferenceStore(string? dataRoot)
    {
        _path = string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, FileName);
        Current = Load();
    }

    public SettingsNavPreference Current { get; private set; }
    public string? LastLoadError { get; private set; }
    public bool IsPersistent => _path is not null;

    private SettingsNavPreference Load()
    {
        if (_path is null || !File.Exists(_path)) return SettingsNavPreference.Empty;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject
                ?? throw new FormatException("设置侧栏偏好必须是 JSON 对象");
            var version = root["version"] is JsonValue value && value.TryGetValue(out int parsed) ? parsed : 1;
            if (version > 1) throw new FormatException($"不支持的侧栏偏好版本 {version}");
            return new SettingsNavPreference
            {
                Promoted = ReadIds(root["promoted"]),
                Demoted = ReadIds(root["demoted"]),
            }.Normalize();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException
            or FormatException or ArgumentException)
        {
            LastLoadError = error.GetType().Name;
            return SettingsNavPreference.Empty;
        }
    }

    private static IReadOnlyList<string> ReadIds(JsonNode? node)
    {
        if (node is not JsonArray array) return Array.Empty<string>();
        var ids = new List<string>(array.Count);
        foreach (var element in array)
            if (element is JsonValue value && value.TryGetValue(out string? id) && id is not null)
                ids.Add(id);
        return ids;
    }

    /// <summary>保存并返回规范化后的偏好；无 dataRoot 时只更新内存态。</summary>
    public SettingsNavPreference Save(SettingsNavPreference next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var normalized = next.Normalize();
        lock (_gate)
        {
            if (_path is not null && normalized != Current)
            {
                var json = new JsonObject
                {
                    ["version"] = 1,
                    ["promoted"] = new JsonArray(normalized.Promoted.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                    ["demoted"] = new JsonArray(normalized.Demoted.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(temporary, json);
                    File.Move(temporary, _path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            Current = normalized;
        }
        return Current;
    }
}
