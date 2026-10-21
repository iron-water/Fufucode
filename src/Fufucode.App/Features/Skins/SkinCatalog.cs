using System;
using System.Collections.Generic;
using System.Linq;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Features.Skins;

/// <summary>
/// 可选皮肤标识。亮/暗由 ThemeVariant 表达，不是独立皮肤；Fluent 始终是默认。
/// </summary>
public enum SkinId
{
    Fluent = 0,
    Metro = 1,
    Material3 = 2,
    Aero = 3,
    Luna = 4,
    WorkBuddy = 5,
    LiquidGlass = 6,

    /// <summary>插件提供的第三方皮肤（不内置于默认包）。</summary>
    External = 7,
}

/// <summary>
/// 单个皮肤的定义：稳定存储键、描述与真实的 Avalonia 资源/样式入口。
/// Fluent 不携带覆盖资源，恢复即“不追加任何覆盖层”，直接复用当前真实 Fluent 令牌。
/// <see cref="Summary"/> 是设置页内联的一行短标签；<see cref="Description"/> 是较长说明，
/// 只允许出现在 ToolTip / 悬浮气泡 / 折叠小帮助按钮里，不再直接铺在页面上。
/// </summary>
public sealed record SkinDefinition(
    SkinId Id,
    string StorageKey,
    string DisplayName,
    string Summary,
    string Description,
    bool FollowsSystemAccent,
    string? TokenDictionaryUri,
    string? ScopedStylesUri);

/// <summary>
/// 皮肤键解析结果。未知键回退 fluent，但保留原始请求文本与原因，不静默改写调用方持久输入。
/// </summary>
public readonly record struct SkinResolution(
    SkinId Skin,
    string RequestedKey,
    bool FellBack,
    string Reason,
    SkinDefinition? ResolvedDefinition = null)
{
    public SkinDefinition Definition => ResolvedDefinition ?? SkinCatalog.Get(Skin);
}

/// <summary>
/// 皮肤目录：唯一真源的皮肤清单与解析规则（含解释性回退），不读取/写入 ui.json。
/// </summary>
public static class SkinCatalog
{
    public const string DefaultStorageKey = "fluent";

    private static readonly SkinDefinition FluentDefinition = new(
        SkinId.Fluent, "fluent", "Fluent",
        "Win11 默认基线",
        "默认基线：复用 WinUI Fluent v2 语义令牌（跟随系统强调色），皮肤层不追加覆盖。",
        true, null, null);

    private static readonly SkinDefinition MetroDefinition = new(
        SkinId.Metro, "metro", "Metro",
        "Win8 直角平面色块",
        "Windows 8 风格样板：直角、平面色块、整块强调色选择态；固定强调色（亮 #2D89EF / 暗 #4DB2FF）。",
        false,
        "avares://Fufucode.App/Styles/Skins/Metro.axaml",
        "avares://Fufucode.App/Styles/Skins/MetroStyles.axaml");

    private static readonly SkinDefinition Material3Definition = new(
        SkinId.Material3, "material3", "Material 3",
        "胶囊形与色调分层",
        "Material 3 样板：胶囊按钮、色调分层容器、状态层叠加；固定强调色（亮 #6750A4 / 暗 #D0BCFF），字体回退系统 Segoe 族。",
        false,
        "avares://Fufucode.App/Styles/Skins/Material3.axaml",
        "avares://Fufucode.App/Styles/Skins/Material3Styles.axaml");

    private static readonly SkinDefinition AeroDefinition = new(
        SkinId.Aero, "aero", "Aero",
        "Vista 玻璃渐变",
        "Vista 风格：四段实色玻璃渐变（标题栏/按钮/页签/行态）、连体水晶窗口按钮与细边框；采用实色合成，不要求系统模糊。",
        false, Aero.AeroSkin.TokenDictionaryUri, Aero.AeroSkin.ScopedStylesUri);

    private static readonly SkinDefinition LunaDefinition = new(
        SkinId.Luna, "luna", "Luna",
        "XP 经典圆润高对比",
        "Windows XP Luna Blue 风格：3px 圆润控件、四段实色玻璃渐变按钮、XP 经典蓝标题栏与 #316AC5 高对比选择态；字体回退 Tahoma。",
        false,
        "avares://Fufucode.App/Styles/Skins/Luna.axaml",
        "avares://Fufucode.App/Styles/Skins/LunaStyles.axaml");

    private static readonly SkinDefinition LiquidGlassDefinition = new(
        SkinId.LiquidGlass, "liquidglass", "Liquid Glass",
        "iOS 26 液态玻璃",
        "iOS 26 风格：SKSL 自绘液态玻璃（折射/色散/边缘高光）覆盖标题栏与停靠 chrome，" +
        "内容控件取 iOS 连续大圆角与 tinted 语义；跟随系统强调色，无 GPU/无快照时按实色 iOS 令牌降级。",
        true,
        LiquidGlass.LiquidGlassSkin.TokenDictionaryUri,
        LiquidGlass.LiquidGlassSkin.ScopedStylesUri);

    /// <summary>默认包内置的皮肤：按用户要求只保留 Fluent。</summary>
    /// <remarks>WorkBuddy 皮肤已归档；其余皮肤改为插件市场提供，安装并启用后经
    /// <see cref="PluginSkinRegistry"/> 动态出现在这里。</remarks>
    public static IReadOnlyList<SkinDefinition> BuiltIn { get; } = new[] { FluentDefinition };

    /// <summary>
    /// 当前可用皮肤 = 内置 Fluent + 已安装且启用的插件皮肤，顺序即设置页展示顺序。
    /// 每次读取都反映插件注册表的最新状态（安装/停用/卸载即时生效）。
    /// </summary>
    public static IReadOnlyList<SkinDefinition> All
    {
        get
        {
            var result = new List<SkinDefinition>(BuiltIn);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FluentDefinition.StorageKey };
            foreach (var contribution in PluginSkinRegistry.Contributions)
            {
                if (!seen.Add(contribution.StorageKey)) continue;
                result.Add(DefinitionFor(contribution));
            }
            return result;
        }
    }

    /// <summary>把插件皮肤贡献转换为目录定义；已知皮肤沿用内置枚举以便行为接线。</summary>
    private static SkinDefinition DefinitionFor(Core.Plugins.PluginSkinContribution contribution)
    {
        var id = IdForStorageKey(contribution.StorageKey);
        if (id == SkinId.External)
        {
            return new SkinDefinition(
                SkinId.External,
                contribution.StorageKey,
                contribution.DisplayName,
                contribution.Summary,
                contribution.Description,
                false,
                contribution.TokenDictionaryUri,
                contribution.ScopedStylesUri);
        }

        var built = Get(id);
        return built with
        {
            DisplayName = contribution.DisplayName,
            Summary = contribution.Summary,
            Description = contribution.Description,
            TokenDictionaryUri = contribution.TokenDictionaryUri ?? built.TokenDictionaryUri,
            ScopedStylesUri = contribution.ScopedStylesUri ?? built.ScopedStylesUri,
        };
    }

    private static SkinId IdForStorageKey(string storageKey) => storageKey.Trim().ToLowerInvariant() switch
    {
        "metro" => SkinId.Metro,
        "material3" or "material-3" or "m3" => SkinId.Material3,
        "aero" => SkinId.Aero,
        "luna" or "xp" or "luna-blue" => SkinId.Luna,
        "liquidglass" or "liquid-glass" or "glass" or "ios" or "lg" => SkinId.LiquidGlass,
        _ => SkinId.External,
    };

    /// <summary>某皮肤当前是否可用（Fluent 恒可用；其余必须是已启用插件皮肤）。</summary>
    public static bool IsAvailable(SkinId id)
    {
        if (id == SkinId.Fluent) return true;
        if (id == SkinId.External) return PluginSkinRegistry.Contributions.Count > 0;
        return PluginSkinRegistry.Find(Get(id).StorageKey) is not null;
    }

    public static SkinDefinition Get(SkinId id) => id switch
    {
        SkinId.Metro => MetroDefinition,
        SkinId.Material3 => Material3Definition,
        SkinId.Aero => AeroDefinition,
        SkinId.Luna => LunaDefinition,
        SkinId.LiquidGlass => LiquidGlassDefinition,
        _ => FluentDefinition,
    };

    /// <summary>
    /// 解析持久化皮肤键（容忍大小写/空白与 m3、material-3、xp 别名）。
    /// 未识别值不抛异常、不改写输入，返回 fluent 回退并说明原因。
    /// </summary>
    public static bool TryParse(string? storageKey, out SkinId id)
    {
        var normalized = storageKey?.Trim().ToLowerInvariant() ?? string.Empty;
        switch (normalized)
        {
            case "fluent":
                id = SkinId.Fluent;
                return true;
            case "workbuddy":
            case "work-buddy":
            case "wb":
                id = SkinId.WorkBuddy;
                return false; // 已归档，不再可见
        }

        var resolved = IdForStorageKey(normalized);
        // 非 Fluent 皮肤只有在插件已安装且启用时才可解析；否则按未知键回退 Fluent。
        if (resolved != SkinId.External && PluginSkinRegistry.Find(Get(resolved).StorageKey) is not null)
        {
            id = resolved;
            return true;
        }

        if (resolved == SkinId.External && PluginSkinRegistry.Find(normalized) is not null)
        {
            id = SkinId.External;
            return true;
        }

        id = SkinId.Fluent;
        return false;
    }

    /// <summary>解析并给出可解释结果；空值与未知值都回退 fluent，原始请求原样保留在结果中。</summary>
    public static SkinResolution Resolve(string? storageKey)
    {
        var requested = storageKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(requested))
            return new SkinResolution(SkinId.Fluent, requested, true, "皮肤键为空，按默认回退 fluent。");
        if (TryParse(requested, out var id))
        {
            var definition = id == SkinId.External
                ? DefinitionFor(PluginSkinRegistry.Find(requested)!)
                : (SkinCatalog.All.FirstOrDefault(d => d.Id == id) ?? Get(id));
            return new SkinResolution(id, requested, false, string.Empty, definition);
        }

        var installed = PluginSkinRegistry.Find(requested);
        if (installed is not null)
            return new SkinResolution(SkinId.External, requested, false, string.Empty, DefinitionFor(installed));

        return new SkinResolution(SkinId.Fluent, requested, true,
            $"皮肤 \"{requested}\" 未安装或未知：回退 fluent；请先在插件市场安装对应皮肤插件。请求值未被改写。");
    }
}
