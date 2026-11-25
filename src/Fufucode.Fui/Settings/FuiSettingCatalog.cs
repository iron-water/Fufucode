namespace Fufucode.Fui;

internal enum FuiSettingKind
{
    Choice,
    Toggle,
    MultiSelect,
    KeyBinding
}

internal sealed record FuiSettingOption(string Value, string Label);

internal sealed record FuiSettingDefinition(
    string Key,
    string Group,
    string Label,
    FuiSettingKind Kind,
    IReadOnlyList<FuiSettingOption> Options,
    bool RestartRequired,
    Func<FuiSettings, string> Read,
    Func<FuiSettings, string, FuiSettings>? Apply = null,
    string? KeyAction = null);

internal sealed record FuiStatusSegment(string Id, string Label);

internal static class FuiStatusSegmentCatalog
{
    internal static readonly FuiStatusSegment[] All =
    [
        new("model", "模型"),
        new("project", "项目"),
        new("runtime", "运行时"),
        new("queue", "队列"),
        new("subagents", "子代理"),
        new("usage", "用量")
    ];

    internal static readonly IReadOnlyList<string> DefaultEnabled = Array.AsReadOnly(All.Select(item => item.Id).ToArray());
    internal static FuiStatusSegment? Find(string id) => All.FirstOrDefault(item => item.Id == id);

    internal static FuiSettings Toggle(FuiSettings settings, string id)
    {
        if (Find(id) is null) return settings;
        var enabled = settings.StatusSegments.Contains(id, StringComparer.Ordinal);
        var segments = enabled
            ? settings.StatusSegments.Where(item => item != id).ToArray()
            : settings.StatusSegments.Concat([id]).ToArray();
        return settings with { StatusSegments = segments };
    }
}

internal static class FuiSettingCatalog
{
    internal static readonly string[] ThemeValues = ["auto", "dark", "light", "mono"];
    internal static readonly string[] HyperlinkValues = ["auto", "on", "off"];
    internal static readonly string[] NotificationValues = ["auto", "off", "on"];

    internal static readonly IReadOnlyList<FuiSettingDefinition> Definitions = Build();

    internal static readonly string[] Groups =
        ["外观", "阅读", "终端", "状态栏", "按键"];

    internal static FuiSettingDefinition? Find(string key) =>
        Definitions.FirstOrDefault(item => item.Key == key);

    internal static FuiSettings Cycle(FuiSettings settings, FuiSettingDefinition definition)
    {
        if (definition.Apply is null || definition.Kind is FuiSettingKind.MultiSelect or FuiSettingKind.KeyBinding)
            return settings;
        var current = definition.Read(settings);
        var index = definition.Options.FindIndex(option => option.Value == current);
        var next = definition.Options[(index + 1 + definition.Options.Count) % definition.Options.Count].Value;
        return definition.Apply(settings, next);
    }

    internal static FuiSettings Set(FuiSettings settings, FuiSettingDefinition definition, string value) =>
        definition.Apply?.Invoke(settings, value) ?? settings;

    internal static string GroupValue(FuiSettings settings, FuiSettingDefinition definition) =>
        definition.Kind switch
        {
            FuiSettingKind.MultiSelect => string.Join(" · ",
                FuiStatusSegmentCatalog.All.Where(item => settings.StatusSegments.Contains(item.Id, StringComparer.Ordinal))
                    .Select(item => item.Label)),
            _ => definition.Options.FirstOrDefault(option => option.Value == definition.Read(settings)) is { } option
                ? $"{option.Label} ({option.Value})"
                : definition.Read(settings)
        };

    internal static string Description(FuiSettingDefinition definition)
    {
        var restart = definition.RestartRequired ? " · 需新进程" : "";
        return $"{definition.Group}{restart} · {FuiSettingCatalog.Detail(definition.Key)}";
    }

    internal static string Detail(string key) => key switch
    {
        "theme" => "跟随终端、深色、浅色或单色。当前帧立即重绘。",
        "show-thinking" => "显示或隐藏 transcript 中的思考块。",
        "expand-tools" => "展开或折叠工具卡片正文。",
        "hyperlinks" => "自动按终端/复用器能力处理，或强制开启/关闭 OSC 8 链接。",
        "desktop-notifications" => "回合完成或失败后的终端协议通知。环境变量可强制关闭。",
        "status-segments" => "选择底部状态栏显示的分段；搜索时会显示全部候选。",
        "compact-cards" => "使用卡片角色边框；关闭后改用紧凑行。",
        "mouse-wheel" => "滚轮滚动 transcript；关闭后仍保留点击。",
        "keys" => "命令面板、搜索 transcript、工具展开和思考显示的按键。",
        _ => key.StartsWith("key.", StringComparison.Ordinal)
            ? "安全动作按键；冲突和输入/审批保留键会被拒绝。"
            : ""
    };

    internal static IReadOnlyList<FuiSettingOption> OptionsFor(FuiSettingDefinition definition)
    {
        if (definition.Kind != FuiSettingKind.MultiSelect) return definition.Options;
        return FuiStatusSegmentCatalog.All.Select(item => new FuiSettingOption(item.Id, item.Label)).ToArray();
    }

    internal static string Help(FuiSettings settings)
    {
        var lines = new List<string>();
        foreach (var group in Groups)
        {
            var definitions = Definitions.Where(item => item.Group == group).ToArray();
            if (definitions.Length == 0) continue;
            lines.Add(group);
            lines.AddRange(definitions.Select(item => $"  {item.Label} = {GroupValue(settings, item)}"));
        }
        return string.Join('\n', lines);
    }

    private static IReadOnlyList<FuiSettingDefinition> Build()
    {
        var list = new List<FuiSettingDefinition>
        {
            new("theme", "外观", "主题", FuiSettingKind.Choice,
                Options(ThemeValues, ("auto", "自动"), ("dark", "深色"), ("light", "浅色"), ("mono", "单色")),
                false, settings => settings.Theme, (settings, value) => settings with { Theme = value }),
            new("show-thinking", "阅读", "显示思考", FuiSettingKind.Toggle,
                Options(["true", "false"], ("true", "显示"), ("false", "隐藏")),
                false, settings => settings.ShowThinking ? "true" : "false",
                (settings, value) => settings with { ShowThinking = value == "true" }),
            new("expand-tools", "阅读", "展开工具", FuiSettingKind.Toggle,
                Options(["false", "true"], ("false", "折叠"), ("true", "展开")),
                false, settings => settings.ExpandTools ? "true" : "false",
                (settings, value) => settings with { ExpandTools = value == "true" }),
            new("compact-cards", "阅读", "紧凑卡片", FuiSettingKind.Toggle,
                Options(["true", "false"], ("true", "卡片"), ("false", "紧凑行")),
                false, settings => settings.CompactCards ? "true" : "false",
                (settings, value) => settings with { CompactCards = value == "true" }),
            new("hyperlinks", "终端", "超链接", FuiSettingKind.Choice,
                Options(HyperlinkValues, ("auto", "自动"), ("on", "开启"), ("off", "关闭")),
                false, settings => settings.Hyperlinks, (settings, value) => settings with { Hyperlinks = value }),
            new("desktop-notifications", "终端", "桌面通知", FuiSettingKind.Choice,
                Options(NotificationValues, ("auto", "自动"), ("off", "关闭"), ("on", "开启")),
                false, settings => settings.DesktopNotifications,
                (settings, value) => settings with { DesktopNotifications = value }),
            new("mouse-wheel", "终端", "鼠标滚轮", FuiSettingKind.Toggle,
                Options(["true", "false"], ("true", "开启"), ("false", "关闭")),
                false, settings => settings.MouseWheel ? "true" : "false",
                (settings, value) => settings with { MouseWheel = value == "true" }),
            new("status-segments", "状态栏", "状态分段", FuiSettingKind.MultiSelect,
                FuiStatusSegmentCatalog.All.Select(item => new FuiSettingOption(item.Id, item.Label)).ToArray(),
                false, settings => string.Join(',', settings.StatusSegments),
                (settings, value) => FuiStatusSegmentCatalog.Toggle(settings, value))
        };

        foreach (var action in FuiKeyBindingCatalog.All)
        {
            list.Add(new("key." + action.Id, "按键", action.Label, FuiSettingKind.KeyBinding,
                [], false, settings => FuiKeyBindingCatalog.TryResolve(settings.Keybindings, action.Id,
                    out var chord, out _) ? chord.Format() : "无效", KeyAction: action.Id));
        }
        return list;
    }

    private static FuiSettingOption[] Options(IReadOnlyList<string> values,
        params (string Value, string Label)[] labels) =>
        values.Select(value => new FuiSettingOption(value,
            labels.FirstOrDefault(item => item.Value == value).Label ?? value)).ToArray();
}

internal static class FuiStatusLine
{
    internal sealed record Snapshot(string Project, string Model, string Runtime, int Queue,
        int RunningSubagents, int TotalSubagents, string? Usage);

    internal static string Segments(FuiSettings settings, Snapshot snapshot)
    {
        var values = new List<string>(4);
        foreach (var id in FuiStatusSegmentCatalog.All.Select(item => item.Id)
                     .Where(id => settings.StatusSegments.Contains(id, StringComparer.Ordinal)))
        {
            var value = id switch
            {
                "model" => Clip("模型 " + snapshot.Model),
                "project" => Clip("项目 " + snapshot.Project),
                "runtime" => "运行时 " + snapshot.Runtime,
                "queue" => "队列 " + snapshot.Queue,
                "subagents" => snapshot.TotalSubagents == 0
                    ? "子代理 0"
                    : $"子代理 {snapshot.RunningSubagents}/{snapshot.TotalSubagents}",
                "usage" => string.IsNullOrWhiteSpace(snapshot.Usage) ? "用量 未知" : "用量 " + snapshot.Usage,
                _ => ""
            };
            if (value.Length != 0) values.Add(value);
        }
        return string.Join(" · ", values);
    }

    private static string Clip(string value) => TerminalText.Clip(value, 28, tail: true);
}

internal static class ListExtensions
{
    internal static int FindIndex<T>(this IReadOnlyList<T> items, Func<T, bool> predicate)
    {
        for (var i = 0; i < items.Count; i++)
            if (predicate(items[i])) return i;
        return -1;
    }
}
