using System;
using System.Collections.Generic;

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
public readonly record struct SkinResolution(SkinId Skin, string RequestedKey, bool FellBack, string Reason)
{
    public SkinDefinition Definition => SkinCatalog.Get(Skin);
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

    /// <summary>全部已知皮肤，顺序即设置页展示建议顺序。</summary>
    /// <remarks>WorkBuddy 皮肤已归档（它本就是手机遥控配套 App 的设计规范，
    /// DESK-1 误当桌面皮肤引入）；资源与枚举值保留以免破坏持久化键的解析，但不再对用户可见。</remarks>
    public static IReadOnlyList<SkinDefinition> All { get; } =
        new[] { FluentDefinition, MetroDefinition, Material3Definition, AeroDefinition, LunaDefinition, LiquidGlassDefinition };

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
            case "metro":
                id = SkinId.Metro;
                return true;
            case "aero":
                id = SkinId.Aero;
                return true;
            case "luna":
            case "xp":
            case "luna-blue":
                id = SkinId.Luna;
                return true;
            case "material3":
            case "material-3":
            case "m3":
                id = SkinId.Material3;
                return true;
            case "workbuddy":
            case "work-buddy":
            case "wb":
                id = SkinId.WorkBuddy;
                return true;
            case "liquidglass":
            case "liquid-glass":
            case "glass":
            case "ios":
            case "lg":
                id = SkinId.LiquidGlass;
                return true;
            default:
                id = SkinId.Fluent;
                return false;
        }
    }

    /// <summary>解析并给出可解释结果；空值与未知值都回退 fluent，原始请求原样保留在结果中。</summary>
    public static SkinResolution Resolve(string? storageKey)
    {
        var requested = storageKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(requested))
            return new SkinResolution(SkinId.Fluent, requested, true, "皮肤键为空，按默认回退 fluent。");
        if (TryParse(requested, out var id))
            return new SkinResolution(id, requested, false, string.Empty);
        return new SkinResolution(SkinId.Fluent, requested, true,
            $"未知皮肤键 \"{requested}\"：回退 fluent；请求值未被改写，持久输入保持原样。");
    }
}
