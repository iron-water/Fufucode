using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.SkinLiquidGlass;

/// <summary>Liquid Glass 皮肤：皮肤资源与宿主主题令牌同源，插件只负责在安装/启用后登记可用性。</summary>
public sealed class SkinLiquidGlassPluginPackage : IPluginPackage
{
    public string Id => "skin.liquidglass";

    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public PluginSkinContribution? CreateSkinContribution(PluginPackageContext context) =>
        new(
            StorageKey: "liquidglass",
            DisplayName: "Liquid Glass",
            Summary: "iOS 26 液态玻璃",
            Description: "iOS 26 风格：SKSL 自绘液态玻璃（折射/色散/边缘高光），跟随系统强调色。",
            TokenDictionaryUri: "avares://Fufucode.App/Styles/Skins/LiquidGlass/LiquidGlassTokens.axaml",
            ScopedStylesUri: "avares://Fufucode.App/Styles/Skins/LiquidGlass/LiquidGlassStyles.axaml");
}
