using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.App.SelfTest;

/// <summary>设置 → 模型与思考 → 提供商：协议下拉框真实渲染，预设带出原生协议，切换协议计为未保存改动。</summary>
public static class ProviderProtocolSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool condition, string name)
        { if (condition) pass("提供商协议：" + name); else fail("提供商协议：" + name); }
        var root = Path.GetFullPath("tmp/provider-protocol-ui-tests/" + Guid.NewGuid().ToString("N"));
        var evidence = Path.GetFullPath("tmp/provider-protocol-evidence");
        Directory.CreateDirectory(evidence);
        Window? window = null;
        try
        {
            var store = new AppSettingsStore(new AppDataLayout(root));
            store.Update(s => s with
            {
                Endpoint = new CompatibleEndpoint("anthropic", "https://api.anthropic.com/v1", "claude-opus-5-5",
                    "ANTHROPIC_API_KEY", Api: ModelApi.AnthropicMessages),
            });
            var vm = new SettingsPageViewModel();
            vm.Attach(new(store, true, () => null, null, null, root, null, () => null, () => null,
                () => [], () => null, () => root, _ => Task.CompletedTask, _ => Task.CompletedTask,
                () => "unavailable", () => "unavailable"));
            Check(vm.EndpointApiOptions.SequenceEqual(ModelApi.All.Select(kind => kind.DisplayName)), "选项来自 ModelApi 目录");
            Check(vm.EndpointApiIndex == 2, "已保存的 Anthropic Messages 端点回填协议");
            Check(!vm.HasEndpointDraft, "回填不算未保存改动");

            vm.SelectCategory("models");
            var openai = ProviderPreset.Find("openai")!;
            vm.ChoosePresetCommand.Execute(openai);
            Check(vm.EndpointApiIndex == 1 && vm.SelectedEndpointProvider?.Api == ModelApi.OpenAiResponses,
                "OpenAI 预设默认使用 Responses 协议");

            var page = new SettingsPage { DataContext = vm };
            window = new Window { Content = page, Width = 1280, Height = 900 };
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();
            var combo = page.GetVisualDescendants().OfType<ComboBox>()
                .Single(control => AutomationProperties.GetAutomationId(control) == "endpoint-api");
            Check(combo.IsEffectivelyVisible && combo.SelectedIndex == 1, "提供商编辑页真实渲染协议下拉框");
            Save(window, Path.Combine(evidence, "provider-protocol.png"));

            combo.SelectedIndex = 0;
            Check(vm.EndpointApiIndex == 0 && vm.HasEndpointDraft, "切换协议计为未保存改动");

            vm.ChoosePresetCommand.Execute(ProviderPreset.Find("deepseek")!);
            Check(vm.EndpointApiIndex == 0, "其他预设保持 Chat Completions");
        }
        finally { window?.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }

        static void Save(Visual target, string path)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)target.Bounds.Width), Math.Max(1, (int)target.Bounds.Height)));
            bitmap.Render(target);
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
        }
    }
}
