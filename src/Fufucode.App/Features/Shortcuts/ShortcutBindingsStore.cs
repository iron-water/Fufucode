using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fufucode.App.Features.Shortcuts;

/// <summary>单个动作的用户绑定记录。序列化进 keybindings.json 的绑定表。</summary>
public sealed record ShortcutBindingEntry(string ActionId, string[] Gestures);

/// <summary>
/// keybindings.json 的内存模型。只存用户改动过的动作：
/// 未出现的动作 = 沿用产品默认。保存以版本号为界，未知版本拒绝加载。
/// </summary>
public sealed record ShortcutBindingsDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>动作 ID → 用户绑定（空数组 = 用户显式禁用）。</summary>
    public IReadOnlyDictionary<string, string[]> Bindings { get; init; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>解析文本；结构或版本不合法时抛 <see cref="FormatException"/>，原文件不动。</summary>
    public static ShortcutBindingsDocument Parse(string text)
    {
        var root = JsonNode.Parse(text) as JsonObject ?? throw new FormatException("快捷键配置必须是 JSON 对象");

        // 版本必须是数字字面量且等于当前版本；字符串、缺失或错误类型一律视为损坏。
        if (root["version"] is not JsonValue versionValue || !versionValue.TryGetValue<int>(out var version))
            throw new FormatException("快捷键配置缺少合法的 version 数字字段");
        if (version != CurrentVersion) throw new FormatException($"不支持快捷键配置版本 {version}，原文件未改动");

        // bindings 必须是对象；数组、字符串、null 等结构一律视为损坏，不得静默当作空绑定。
        if (root["bindings"] is not JsonObject raw)
            throw new FormatException("快捷键配置的 bindings 必须是「动作 ID → 手势数组」的对象");

        var bindings = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var pair in raw)
        {
            if (pair.Value is not JsonArray array) throw new FormatException($"动作 {pair.Key} 的绑定必须是数组");
            var gestures = new List<string>();
            foreach (var item in array)
            {
                if (item is not JsonValue value || !value.TryGetValue<string>(out var gesture))
                    throw new FormatException($"动作 {pair.Key} 的绑定含有非字符串条目");
                if (string.IsNullOrWhiteSpace(gesture))
                    throw new FormatException($"动作 {pair.Key} 的绑定含有空手势");
                // 手势必须可解析，否则损坏配置会被当作可用绑定写回文件。
                if (ShortcutGesture.Parse(gesture) is null)
                    throw new FormatException($"动作 {pair.Key} 的绑定「{gesture}」不是可识别的手势");
                gestures.Add(gesture);
            }
            bindings[pair.Key] = [.. gestures];
        }

        return new ShortcutBindingsDocument { Version = version, Bindings = bindings };
    }
}

/// <summary>加载结果：加载失败时给出可见原因，并禁止自动覆盖原文件。</summary>
public sealed record ShortcutLoadResult(ShortcutBindingsDocument Document, string? Error, bool Writable)
{
    public bool Loaded => Error is null;
    public bool IsCorrupt => Error is not null;
}

/// <summary>
/// 命令绑定持久化（保守路径）：全局文件 <c>{dataRoot}/keybindings.json</c>。
/// 配置损坏时回退默认并报告，且**不覆盖损坏文件**——由用户决定是否修复。
/// 写入走 临时文件 → .bak → 原子替换，失败不破坏原文件。
/// </summary>
public sealed class ShortcutBindingsStore
{
    public const string FileName = "keybindings.json";

    public string Path { get; }

    private ShortcutBindingsDocument _document = new();
    private bool _writable = true;

    public ShortcutBindingsStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        Path = System.IO.Path.Combine(dataRoot, FileName);
        Reload();
    }

    /// <summary>当前内存模型；加载失败时为默认空模型（等价于全部沿用产品默认）。</summary>
    public ShortcutBindingsDocument Current => _document;

    /// <summary>上次加载失败原因；null 表示正常。</summary>
    public string? LoadError { get; private set; }

    /// <summary>文件是否可安全覆盖；加载失败后为 false，直到 <see cref="Reload"/> 成功。</summary>
    public bool Writable => _writable;

    public ShortcutLoadResult Reload()
    {
        LoadError = null;
        _writable = true;
        try
        {
            if (!File.Exists(Path)) { _document = new(); return new(_document, null, true); }
            _document = ShortcutBindingsDocument.Parse(File.ReadAllText(Path));
            return new(_document, null, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            _document = new();
            LoadError = Path + "：" + error.GetBaseException().Message;
            _writable = false;
            return new(_document, LoadError, false);
        }
    }

    /// <summary>
    /// 写入一份新绑定表。加载失败（<see cref="Writable"/> 为 false）时抛异常，
    /// 明确要求先修复原文件，绝不静默覆盖。
    /// </summary>
    public void Save(ShortcutBindingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!_writable) throw new InvalidOperationException("快捷键配置加载失败，禁止自动覆盖原文件；请修复文件后重新加载");
        // 写盘前按同一套解析规则校验：内存里的绑定若不可解析，拒绝写出损坏文件。
        foreach (var pair in document.Bindings)
            foreach (var gesture in pair.Value)
                if (ShortcutGesture.Parse(gesture) is null)
                    throw new InvalidOperationException($"动作 {pair.Key} 的绑定「{gesture}」不是可识别的手势，拒绝写入");
        var normalized = document with { Version = ShortcutBindingsDocument.CurrentVersion };
        var node = JsonNode.Parse(normalized.Serialize())!.AsObject();
        Write(Path, node);
        _document = normalized;
        LoadError = null;
    }

    /// <summary>写回一份客户端构造的新表（便捷入口）。</summary>
    public void Save(IReadOnlyDictionary<string, string[]> bindings) =>
        Save(new ShortcutBindingsDocument { Bindings = bindings });

    private static void Write(string path, JsonObject node)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, node.ToJsonString(ShortcutBindingsDocument.JsonOptions));
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
