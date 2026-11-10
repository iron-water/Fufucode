using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

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

    /// <summary>
    /// 按集合内容比较：record 自动生成的相等性对 <see cref="IReadOnlyList{T}"/> 只比引用，
    /// 同一份内容重读两次会永远判为“变了”（导致每次进设置页都白重建、每次保存都重写文件）。
    /// </summary>
    public bool Equals(SettingsNavPreference? other) =>
        other is not null &&
        Promoted.SequenceEqual(other.Promoted, StringComparer.Ordinal) &&
        Demoted.SequenceEqual(other.Demoted, StringComparer.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(string.Join('\u0001', Promoted), string.Join('\u0001', Demoted));

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
///
/// settings-nav.json 与 settings.json 同处数据根，会被扫描器/备份/另一实例短暂独占，
/// 因此读取失败时绝不返回空偏好（那等于把用户收纳的功能全弹回一级侧栏），
/// 而是保留上一次成功读取的结果，并由 <see cref="Reload"/> 在下次进入设置页时再试。
/// </summary>
public sealed class SettingsNavPreferenceStore
{
    public const string FileName = "settings-nav.json";

    /// <summary>读取占用类失败的尝试次数；内容错误（JSON 损坏）不重试。</summary>
    private const int ReadAttempts = 4;

    /// <summary>上次成功读取的快照：进程启动时主文件就被占用时用它，不至于把侧栏弹回平铺。</summary>
    public const string LastGoodFileName = "settings-nav.last-good.json";

    private readonly string? _path;
    private readonly string? _lastGoodPath;
    private readonly object _gate = new();
    private SettingsNavPreference _current;

    public SettingsNavPreferenceStore(string? dataRoot)
    {
        _path = string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, FileName);
        _lastGoodPath = string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, LastGoodFileName);
        _current = Load(SettingsNavPreference.Empty);
    }

    public SettingsNavPreference Current { get { lock (_gate) return _current; } }
    public string? LastLoadError { get; private set; }

    /// <summary>最近一次读取是否沿用了上一次成功结果（文件被占用/损坏时为 true）。</summary>
    public bool UsedLastGood { get; private set; }
    public bool IsPersistent => _path is not null;

    /// <summary>偏好变化（保存或重新读盘）后触发；订阅方须自行切到 UI 线程再动界面集合。</summary>
    public event Action<SettingsNavPreference>? Changed;

    /// <summary>
    /// 重新读盘：外部改动（另一实例、手工编辑）或上次读取失败时用来自愈。
    /// 返回偏好是否真的变了。
    /// </summary>
    public bool Reload()
    {
        SettingsNavPreference previous, next;
        lock (_gate)
        {
            previous = _current;
            next = Load(previous);
            if (next == previous) return false;
            _current = next;
        }
        Changed?.Invoke(next);
        return true;
    }

    /// <summary>
    /// 读盘。<paramref name="fallback"/> 是读不到时的兜底（首次加载传空偏好，
    /// 之后传当前生效值），保证“这次没读到”不会被当成“用户没有任何收纳”。
    /// </summary>
    private SettingsNavPreference Load(SettingsNavPreference fallback)
    {
        LastLoadError = null;
        UsedLastGood = false;
        if (_path is null || !File.Exists(_path)) return SettingsNavPreference.Empty;
        try
        {
            var settings = Parse(ReadJson());
            WriteLastGood(settings);
            return settings;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException
            or FormatException or ArgumentException)
        {
            LastLoadError = error.GetType().Name;
            // 先试跨进程的兜底快照（主文件启动时就被占用也能拿起上次的收纳），
            // 再退回本次进程内的上一次结果；两者都没有才真是“没有偏好”。
            if (ReadLastGood() is { } lastGood)
            {
                UsedLastGood = true;
                return lastGood;
            }
            UsedLastGood = fallback != SettingsNavPreference.Empty;
            return fallback;
        }
    }

    private SettingsNavPreference Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new FormatException("设置侧栏偏好必须是 JSON 对象");
        var version = root["version"] is JsonValue value && value.TryGetValue(out int parsed) ? parsed : 1;
        if (version > 1) throw new FormatException($"不支持的侧栏偏好版本 {version}");
        return new SettingsNavPreference
        {
            Promoted = ReadIds(root["promoted"]),
            Demoted = ReadIds(root["demoted"]),
        }.Normalize();
    }

    /// <summary>上次成功读取的快照；不存在或同样读不到时返回 null（不影响主文件的诊断记录）。</summary>
    private SettingsNavPreference? ReadLastGood()
    {
        if (_lastGoodPath is null) return null;
        try
        {
            return File.Exists(_lastGoodPath) ? Parse(File.ReadAllText(_lastGoodPath)) : null;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException
            or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>成功读取/保存后刷新兜底快照；写不成不影响主流程。</summary>
    private void WriteLastGood(SettingsNavPreference preference)
    {
        if (_lastGoodPath is null) return;
        var temporary = _lastGoodPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, Serialize(preference));
            File.Move(temporary, _lastGoodPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    private static string Serialize(SettingsNavPreference preference) =>
        new JsonObject
        {
            ["version"] = 1,
            ["promoted"] = new JsonArray(preference.Promoted.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
            ["demoted"] = new JsonArray(preference.Demoted.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>读取文件；只对占用这类瞬时失败重试，内容错误直接抛出。</summary>
    private string ReadJson()
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(_path!); }
            catch (Exception error) when (attempt < ReadAttempts &&
                                          error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(40 * attempt);
            }
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
                var json = Serialize(normalized);
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
            _current = normalized;
        }
        WriteLastGood(Current);
        Changed?.Invoke(Current);
        return Current;
    }
}
