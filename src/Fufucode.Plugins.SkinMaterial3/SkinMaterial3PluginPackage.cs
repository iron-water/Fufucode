using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.SkinMaterial3;

/// <summary>Material 3 皮肤：皮肤资源与宿主主题令牌同源，插件只负责在安装/启用后登记可用性。</summary>
public sealed class SkinMaterial3PluginPackage : IPluginPackage
{
    public string Id => "skin.material3";

    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public PluginSkinContribution? CreateSkinContribution(PluginPackageContext context) =>
        new(
            StorageKey: "material3",
            DisplayName: "Material 3",
            Summary: "胶囊形与色调分层",
            Description: "Material 3 样板：胶囊按钮、色调分层容器、状态层叠加（固定强调色）。",
            TokenDictionaryUri: "avares://Fufucode.App/Styles/Skins/Material3.axaml",
            ScopedStylesUri: "avares://Fufucode.App/Styles/Skins/Material3Styles.axaml");
}
