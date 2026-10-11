using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fufucode.App.Infrastructure;

/// <summary>动作可放置的位置。TopCenter 是顶栏中间的宽敞区（用户可自选功能放这里）。</summary>
public enum UiActionPosition { Hidden, TopLeft, TopRight, MenuLeft, MenuRight, HeaderMenu, StatusLeft, StatusRight, TopCenter }
public sealed record UiActionPlacement(UiActionPosition Position, int Order = 0);

public sealed record WorkbenchAppearance
{
    public int Version { get; init; } = 1;
    public string Theme { get; init; } = "system";
    public string Accent { get; init; } = "system";
    public Avalonia.Media.Color EffectiveAccent => Accent == "system"
        ? Avalonia.Application.Current?.PlatformSettings?.GetColorValues().AccentColor1 ?? Avalonia.Media.Color.Parse("#0078D4")
        : Avalonia.Media.Color.Parse(Accent);
    public string Surface { get; init; } = "fluent";
    public double FontSize { get; init; } = 13;
    public string Density { get; init; } = "cozy";
    public string Radius { get; init; } = "mid";
    public string ReadingWidth { get; init; } = "standard";
    public string MessageActions { get; init; } = "hover";
    public string Composer { get; init; } = "wide";
    /// <summary>底部状态栏（连接/Shell/活动/Token 细条）。默认关闭，需要时在外观 → 前端功能排布里开启。</summary>
    public bool ShowStatusBar { get; init; }
    public bool ShowSessionHeader { get; init; } = false;
    public bool ShowAvatars { get; init; } = true;
    public bool ShowUserNames { get; init; } = true;
    public bool ShowAssistantNames { get; init; } = true;
    public bool ShowMessageTimes { get; init; } = true;
    /// <summary>会话刻度：常开启，属对话区固定结构，不提供开关。</summary>
    public bool ShowRuler { get; init; } = true;
    public bool ShowChangeSummary { get; init; } = true;
    public bool ShowArtifactSummary { get; init; } = true;
    public bool AutoReadReplies { get; init; }
    public string UserName { get; init; } = "你";
    public string AssistantName { get; init; } = "Fufucode";
    public bool UseNamesInPrompt { get; init; } = true;
    public Dictionary<string, UiActionPlacement> Actions { get; init; } = new(StringComparer.Ordinal);
    public WorkbenchLayoutState? Layout { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);
    public static WorkbenchAppearance Parse(string text)
    {
        var root = JsonNode.Parse(text) as JsonObject ?? throw new FormatException("UI 配置必须是 JSON 对象");
        if (!root.ContainsKey("readingWidth") && root["readWidth"] is JsonValue width)
            root["readingWidth"] = width.GetValue<string>() == "std" ? "standard" : width.GetValue<string>();
        if (!root.ContainsKey("messageActions") && root["msgActions"] is { } actions) root["messageActions"] = actions.DeepClone();
        if (root["layout"] is JsonObject layout && !layout.ContainsKey("pages") && layout.ContainsKey("left"))
        {
            var sizes = layout["sizes"] as JsonObject;
            root["layout"] = new JsonObject
            {
                ["pages"] = new JsonObject { ["left"] = layout["left"]?.DeepClone(), ["right"] = layout["right"]?.DeepClone(), ["bottom"] = layout["bottom"]?.DeepClone() },
                ["selected"] = layout["selected"]?.DeepClone(), ["hidden"] = layout["hidden"]?.DeepClone(),
                ["leftWidth"] = sizes?["left"]?.DeepClone() ?? JsonValue.Create(232),
                ["rightWidth"] = sizes?["right"]?.DeepClone() ?? JsonValue.Create(320),
                ["bottomHeight"] = sizes?["bottom"]?.DeepClone() ?? JsonValue.Create(240)
            };
        }
        var value = JsonSerializer.Deserialize<WorkbenchAppearance>(root.ToJsonString(), JsonOptions)
            ?? throw new FormatException("UI 配置必须是 JSON 对象");
        if (value.Version != 1) throw new FormatException($"不支持 UI 配置版本 {value.Version}，原文件未改动");
        return value.Normalize();
    }
    public WorkbenchAppearance Normalize()
    {
        static string Choose(string? value, string fallback, params string[] values) => values.Contains(value) ? value! : fallback;
        static string Name(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[..Math.Min(value.Trim().Length, 80)];
        return this with
        {
            Version = 1,
            Theme = Choose(Theme, "system", "system", "light", "dark"),
            Accent = Accent == "system" || Accent is { Length: 7 } && Accent[0] == '#' && Accent.Skip(1).All(Uri.IsHexDigit) ? Accent : "system",
            Surface = Choose(Surface, "fluent", "fluent", "glass", "flat", "classic"),
            FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 11, 24) : 13,
            Density = Choose(Density, "cozy", "compact", "cozy", "airy"),
            Radius = Choose(Radius, "mid", "none", "mid", "high"),
            ReadingWidth = Choose(ReadingWidth, "standard", "narrow", "standard", "free"),
            MessageActions = Choose(MessageActions, "hover", "show", "hover", "hide"),
            Composer = Choose(Composer, "wide", "wide", "reading", "hidden"),
            UserName = Name(UserName, "你"), AssistantName = Name(AssistantName, "Fufucode"),
            Layout = Layout is null ? null : WorkbenchLayoutState.Restore(Layout.Serialize(), WorkbenchLayoutState.KnownPages),
            Actions = (Actions ?? new()).Where(p => !string.IsNullOrWhiteSpace(p.Key) && p.Value is not null && Enum.IsDefined(p.Value.Position))
                .ToDictionary(p => p.Key, p => p.Value with { Order = Math.Clamp(p.Value.Order, -1000, 1000) }, StringComparer.Ordinal)
        };
    }
    public double ReadWidth => ReadingWidth switch { "narrow" => 560, "free" => double.PositiveInfinity, _ => 768 };
    public double Corner => Radius switch { "none" => 0, "high" => 8, _ => 4 };
    public double RowHeight => Density switch { "compact" => 32, "airy" => 48, _ => 40 };
    /// <summary>当前生效布局里某停靠区是否为隐藏。供设置控件取值使用。</summary>
    public bool LayoutHidden(DockZone zone) => (Layout ?? new()).Hidden.TryGetValue(zone, out var hidden) && hidden;
    /// <summary>某页面当前所在停靠区的序号文本；未打开时为 "3"（隐藏）。</summary>
    public string LayoutPageZone(string pageId) => (Layout ?? new()).Find(pageId) is { } zone ? ((int)zone).ToString() : "3";

    public static WorkbenchAppearance Preset(string name) => name switch
    {
        "compact" => new() { Density = "compact", Radius = "none", ReadingWidth = "free", FontSize = 12 },
        "reading" => new() { Density = "airy", ReadingWidth = "narrow", Composer = "reading", MessageActions = "hide", ShowStatusBar = false },
        _ => new()
    };
}

/// <summary>Project JSON is a sparse overlay; unknown keys survive ordinary edits.</summary>
public sealed class WorkbenchAppearanceStore
{
    public string GlobalPath { get; }
    public string? ProjectPath { get; private set; }
    public string? LoadError { get; private set; }
    public WorkbenchAppearance Current { get; private set; } = new();
    private JsonObject _global = new(), _project = new();
    private bool _globalWritable = true, _projectWritable = true;

    public WorkbenchAppearanceStore(string dataRoot) { GlobalPath = Path.Combine(dataRoot, "ui.json"); Reload(); }
    public void AttachProject(string? project)
    {
        ProjectPath = project is null ? null : Path.Combine(Path.GetFullPath(project), ".fufucode", "ui.json");
        Reload();
    }
    public void Reload()
    {
        LoadError = null;
        _global = Read(GlobalPath, out _globalWritable);
        _project = ProjectPath is null ? new() : Read(ProjectPath, out _projectWritable);
        Current = Merge(_global, _project);
    }
    private JsonObject Read(string path, out bool writable)
    {
        writable = true;
        try
        {
            if (!File.Exists(path)) return new();
            var obj = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new FormatException("根节点不是对象");
            WorkbenchAppearance.Parse(obj.ToJsonString());
            return obj;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException)
        { writable = false; LoadError = (LoadError is null ? "" : LoadError + "\n") + path + "：" + error.Message; return new(); }
    }
    public static WorkbenchAppearance Merge(JsonObject global, JsonObject project)
    {
        var merged = (JsonObject)global.DeepClone();
        Overlay(merged, project);
        return WorkbenchAppearance.Parse(merged.ToJsonString());
    }
    private static void Overlay(JsonObject target, JsonObject source)
    {
        foreach (var pair in source)
            if (pair.Value is JsonObject nested && target[pair.Key] is JsonObject existing) Overlay(existing, nested);
            else target[pair.Key] = pair.Value?.DeepClone();
    }
    public void Save(WorkbenchAppearance next, bool projectScope)
    {
        next = next.Normalize();
        if (projectScope && ProjectPath is null) throw new InvalidOperationException("当前没有项目，不能保存项目覆盖");
        if (!(projectScope ? _projectWritable : _globalWritable)) throw new InvalidOperationException("配置加载失败，禁止自动覆盖原文件；修复文件后重新加载");
        var baseline = Current;
        var before = JsonNode.Parse(baseline.Serialize())!.AsObject();
        var after = JsonNode.Parse(next.Serialize())!.AsObject();
        var target = (JsonObject)(projectScope ? _project : _global).DeepClone();
        foreach (var pair in after)
        {
            if (JsonNode.DeepEquals(before[pair.Key], pair.Value)) continue;
            if (pair.Key == "actions" && pair.Value is JsonObject actions)
            {
                var placements = target["actions"] as JsonObject ?? new();
                var previous = before["actions"] as JsonObject ?? new();
                foreach (var placement in actions)
                    if (!JsonNode.DeepEquals(previous[placement.Key], placement.Value)) placements[placement.Key] = placement.Value?.DeepClone();
                foreach (var removed in previous.Select(p => p.Key).Except(actions.Select(p => p.Key))) placements.Remove(removed);
                target["actions"] = placements;
            }
            else target[pair.Key] = pair.Value?.DeepClone();
        }
        target["version"] = 1;
        Write(projectScope ? ProjectPath! : GlobalPath, target);
        if (projectScope) _project = target; else _global = target;
        Current = Merge(_global, _project);
    }
    public void Replace(string json, bool projectScope)
    {
        var obj = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("配置必须是对象");
        WorkbenchAppearance.Parse(json);
        if (projectScope && ProjectPath is null) throw new InvalidOperationException("当前没有项目");
        if (!(projectScope ? _projectWritable : _globalWritable)) throw new InvalidOperationException("原配置不可覆盖，请先修复原文件");
        Write(projectScope ? ProjectPath! : GlobalPath, obj);
        Reload();
    }
    public void Reset(bool projectScope)
    {
        // Reset only the explicitly selected UI scope, never product/session data.
        Replace(new WorkbenchAppearance().Serialize(), projectScope);
    }
    public void ClearProjectOverrides()
    {
        Replace("{\"version\":1}", true);
    }
    public string ScopeJson(bool projectScope) => (projectScope ? _project : _global).ToJsonString(WorkbenchAppearance.JsonOptions);
    private static void Write(string path, JsonObject node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, node.ToJsonString(WorkbenchAppearance.JsonOptions));
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
