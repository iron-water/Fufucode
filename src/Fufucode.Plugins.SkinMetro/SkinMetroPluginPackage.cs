using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.SkinMetro;

/// <summary>Metro 皮肤：皮肤资源与宿主主题令牌同源，插件只负责在安装/启用后登记可用性。</summary>
public sealed class SkinMetroPluginPackage : IPluginPackage
{
    public string Id => "skin.metro";

    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public PluginSkinContribution? CreateSkinContribution(PluginPackageContext context) =>
        new(
            StorageKey: "metro",
            DisplayName: "Metro",
            Summary: "Win8 直角平面色块",
            Description: "Windows 8 风格样板：直角、平面色块、整块强调色选择态（固定强调色）。",
            TokenDictionaryUri: "avares://Fufucode.App/Styles/Skins/Metro.axaml",
            ScopedStylesUri: "avares://Fufucode.App/Styles/Skins/MetroStyles.axaml");
}
