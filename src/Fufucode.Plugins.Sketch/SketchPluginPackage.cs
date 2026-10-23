using System;
using System.Threading.Tasks;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.Sketch;

public sealed class SketchPluginPackage : IPluginPackage
{
    public string Id => "tool.sketch";
    public string Version => "1.0.0";
    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public PluginComposerContribution? CreateComposerContribution(PluginPackageContext context) =>
        new("绘制草图", ExecuteAsync);

    private static async Task ExecuteAsync(PluginComposerContext context)
    {
        if (!context.SupportsImages)
        {
            context.Notify?.Invoke("当前会话或模型未声明图片输入能力，无法发送草图。");
            return;
        }

        try
        {
            var editor = new SketchEditorWindow();
            if (!await context.ShowDialogAsync(editor) || string.IsNullOrWhiteSpace(editor.ExportedPath)) return;
            await context.AttachImageAsync(editor.ExportedPath);
        }
        catch (Exception error)
        {
            context.Notify?.Invoke("草图插件失败：" + error.GetBaseException().Message);
        }
    }
}
