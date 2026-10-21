using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.SkinLuna;

/// <summary>Windows XP Luna 皮肤：皮肤资源与宿主主题令牌同源，插件只负责在安装/启用后登记可用性。</summary>
public sealed class SkinLunaPluginPackage : IPluginPackage
{
    public string Id => "skin.luna";

    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public PluginSkinContribution? CreateSkinContribution(PluginPackageContext context) =>
        new(
            StorageKey: "luna",
            DisplayName: "Luna",
            Summary: "XP 经典圆润高对比",
            Description: "Windows XP Luna Blue 风格：圆润控件、经典蓝标题栏与高对比选择态。",
            TokenDictionaryUri: "avares://Fufucode.App/Styles/Skins/Luna.axaml",
            ScopedStylesUri: "avares://Fufucode.App/Styles/Skins/LunaStyles.axaml");
}
