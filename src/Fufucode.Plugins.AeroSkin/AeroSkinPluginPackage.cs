using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.AeroSkin;

public sealed class AeroSkinPluginPackage : IPluginPackage
{
    public string Id => "skin.aero";
    public string Version => "1.0.0";

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public PluginSkinContribution? CreateSkinContribution(PluginPackageContext context) =>
        new(
            StorageKey: "aero",
            DisplayName: "Windows Aero (玻璃风格)",
            Summary: "拟物玻璃、高光浮雕与光泽控制台风格（Windows Vista/7 视觉复刻）。",
            Description: "采用仿玻璃透明质感、高亮边缘反光与质感边框；在支持合成器的系统下呈现深度立体光效。",
            TokenDictionaryUri: "avares://Fufucode.Plugins.AeroSkin/Themes/AeroTokens.axaml",
            ScopedStylesUri: "avares://Fufucode.Plugins.AeroSkin/Themes/AeroStyles.axaml");
}
