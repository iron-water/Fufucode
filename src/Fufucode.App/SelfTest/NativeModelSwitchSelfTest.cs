using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Fufucode.App.Features.Skins;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.App.SelfTest;

internal static class NativeModelSwitchSelfTest
{
    internal static async Task<int> RunAsync(MainWindow main)
    {
        var root = Path.GetFullPath("tmp/model-switch-evidence");
        Directory.CreateDirectory(root);
        var failures = new List<string>();
        void Check(bool value, string message)
        {
            Console.WriteLine((value ? "PASS " : "FAIL ") + message);
            if (!value) failures.Add(message);
        }
        var store = new AppSettingsStore(new AppDataLayout(Path.Combine(root, "settings")));
        store.Update(current => current with
        {
            SelectedModel = "auto/gemini-fixture",
            Endpoint = new CompatibleEndpoint("fixture-gateway", "http://127.0.0.1:1/v1", "auto/gemini-fixture", "FUFUCODE_NO_KEY",
                ["auto/gemini-fixture", "auto/other-fixture", "plain-fixture", "auto/rejected-fixture", "auto/unconfirmed-fixture"]),
        });
        var recorder = new RecordingModelClient();
        var turnActive = false;
        await using var chat = new NativeCSharpChatSession(recorder, modelId: "auto/gemini-fixture");
        await using var coordinator = new NativeSessionCoordinator(chat, settings: store);
        await coordinator.StartAsync();
        var vm = new MainViewModel(chat, new RecordingClipboardService(), SynchronousRefreshScheduler.Instance);
        var context = new SettingsPageViewModel.Context(store, false, () => chat.CurrentModel,
            coordinator.QueryAvailableModelsAsync, null, store.DataRoot, null, () => null, () => null, () => [], () => null,
            () => root, _ => Task.CompletedTask, _ => Task.CompletedTask, () => "fixture", () => "fixture",
            IsTurnActive: () => turnActive, SetModel: coordinator.SetModelAsync, QueryState: coordinator.QueryStateAsync,
            ModelApplied: model => vm.StatusBar.SetModelOverride(model), UsesEndpointModelIds: true);
        vm.Settings.Attach(context);
        var page = new ChatPage { DataContext = vm };
        var window = new Window { Width = 1000, Height = 800, Content = page, Title = "Model switching native regression" };
        using var skin = SkinLease.Apply(window, SkinCatalog.Resolve("fluent"), ThemeVariant.Dark);
        window.Show(main);
        page.ApplyAppearance(new WorkbenchAppearance());
        try
        {
            await Ready();
            var modelButton = page.FindControl<Button>("ModelButton")!;
            modelButton.Flyout!.ShowAt(modelButton);
            await Ready();
            ClickModel("auto/other-fixture");
            await Ready();
            Check(chat.CurrentModel == "auto/other-fixture" && vm.Settings.CurrentModelText == "auto/other-fixture",
                "gateway model keeps its full routed ID without an extra backend prefix");
            Check(store.Current.SelectedModel == "auto/other-fixture" && store.Current.Endpoint!.ModelId == "auto/other-fixture",
                "confirmed model updates both session default and endpoint startup model");
            await chat.SendAsync("local fixture only");
            for (var attempt = 0; attempt < 100 && recorder.LastModel is null; attempt++) await Task.Delay(20);
            Check(recorder.LastModel == "auto/other-fixture", "next actual engine request uses the full selected model ID");
            var popup = Popup();
            var confirmed = popup.GetVisualDescendants().OfType<Border>().Single(control => control.Classes.Contains("modelChoiceOutline") && control.Classes.Contains("confirmed"));
            Check(confirmed.DataContext is ModelPickerItem { Value: "auto/other-fixture", IsConfirmed: true } &&
                Math.Abs(confirmed.BorderThickness.Left * popup.RenderScaling - 1) < 0.001 &&
                confirmed.BorderBrush is ISolidColorBrush brush && brush.Color.G > brush.Color.R,
                "backend-confirmed model row gets a one-screen-pixel green outline");
            CheckOutlinePixels(popup, confirmed);
            Check(!popup.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible &&
                text.Text?.StartsWith("已保存", StringComparison.Ordinal) == true), "successful switch adds no explanatory status text to the model picker");
            Save(popup, "model-confirmed.png");
            vm.Settings.ModelPickerFilter = "other";
            vm.Settings.ModelPickerFilter = "";
            await Ready();
            Check(vm.Settings.FilteredModelChoices.Single(item => item.Value == "auto/other-fixture").IsConfirmed,
                "confirmed outline survives filtering and rebuilding model rows");
            ClickModel("plain-fixture");
            await Ready();
            Check(chat.CurrentModel == "plain-fixture" && vm.Settings.ModelsState == SettingSaveState.Applied,
                "endpoint IDs without a slash can also be selected");
            vm.Settings.Attach(context with { SetModel = (_, _, _) => Task.FromResult(new RpcResponse("fixture", "set_model", false, null, "fixture model unavailable")) });
            await Ready();
            ClickModel("auto/rejected-fixture");
            await Ready();
            Check(chat.CurrentModel == "plain-fixture" && store.Current.SelectedModel == "plain-fixture" &&
                vm.Settings.ModelsError == "fixture model unavailable" &&
                !vm.Settings.FilteredModelChoices.Single(item => item.Value == "auto/rejected-fixture").IsConfirmed,
                "rejected switch keeps current model and saved defaults and reports its reason");
            Save(Popup(), "model-rejected.png");
            vm.Settings.Attach(context with { SetModel = (_, _, _) => Task.FromResult(new RpcResponse("fixture", "set_model", true, null, null)) });
            await Ready();
            ClickModel("auto/unconfirmed-fixture");
            await Ready();
            Check(vm.Settings.ModelsState == SettingSaveState.Failed && store.Current.SelectedModel == "plain-fixture" &&
                vm.Settings.ModelsError?.Contains("模型未切换", StringComparison.Ordinal) == true,
                "success response with unchanged runtime does not get a success outline or overwrite defaults");
            vm.Settings.Attach(context);
            turnActive = true;
            await Ready();
            ClickModel("auto/other-fixture");
            await Ready();
            Check(chat.CurrentModel == "plain-fixture" && vm.Settings.ModelsState == SettingSaveState.Saved &&
                vm.Settings.ConfirmedModel == "plain-fixture", "saved-for-next-session choice is not marked as currently applied");
            turnActive = false;
            ClickModel("auto/other-fixture");
            await Ready();
            Check(chat.CurrentModel == "auto/other-fixture" && vm.Settings.ModelsState == SettingSaveState.Applied &&
                vm.Settings.ConfirmedModel == "auto/other-fixture", "same saved model can be clicked again to apply after the active turn");
            Check(new AppSettingsStore(new AppDataLayout(Path.Combine(root, "settings"))).Current.Endpoint?.ModelId == "auto/other-fixture",
                "a reloaded settings store retains the confirmed startup model");
        }
        finally { modelClose(); window.Close(); }
        File.WriteAllLines(Path.Combine(root, "failures.txt"), failures);
        Console.WriteLine("Native model-switch failures: " + failures.Count + "; evidence: " + root);
        return failures.Count == 0 ? 0 : 1;

        void modelClose() => page.FindControl<Button>("ModelButton")?.Flyout?.Hide();
        async Task Ready() { await Task.Delay(250); window.UpdateLayout(); }
        TopLevel Popup() => window.OpenedPopups.Select(popup => popup.Child).OfType<Control>().Select(TopLevel.GetTopLevel).First(root => root is not null)!;
        void ClickModel(string value)
        {
            var button = Popup().GetVisualDescendants().OfType<Button>().Single(control => control.Classes.Contains("modelChoiceRow") &&
                control.DataContext is ModelPickerItem item && item.Value == value);
            button.Command!.Execute(button.CommandParameter);
        }
        void Save(Visual visual, string name)
        {
            var scale = TopLevel.GetTopLevel(visual)?.RenderScaling ?? 1;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(visual.Bounds.Width * scale),
                (int)Math.Ceiling(visual.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
            bitmap.Render(visual); bitmap.Save(Path.Combine(root, name), PngBitmapEncoderOptions.Default);
        }
        void CheckOutlinePixels(TopLevel popup, Border border)
        {
            var scale = popup.RenderScaling;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(popup.Bounds.Width * scale),
                (int)Math.Ceiling(popup.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
            bitmap.Render(popup);
            using var readable = new WriteableBitmap(bitmap.PixelSize, bitmap.Dpi, Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using var pixels = readable.Lock();
            bitmap.CopyPixels(pixels);
            var origin = border.TranslatePoint(default, popup)!.Value;
            var x = (int)Math.Round((origin.X + border.Bounds.Width / 2) * scale);
            var y = (int)Math.Round(origin.Y * scale);
            var greenRows = 0;
            for (var row = y - 2; row <= y + 3; row++)
            {
                var pixel = Marshal.ReadInt32(pixels.Address, row * pixels.RowBytes + x * 4);
                var blue = pixel & 255; var green = (pixel >> 8) & 255; var red = (pixel >> 16) & 255;
                if (green > red + 20 && green > blue + 20) greenRows++;
            }
            Console.WriteLine($"MODEL OUTLINE scale={scale} stroke={border.BorderThickness.Left} greenPixelRows={greenRows}");
            Check(greenRows == 1, "rendered confirmed outline is exactly one green screen-pixel row");
        }
    }

    private sealed class RecordingModelClient : IPureModelClient
    {
        public string? LastModel { get; private set; }
        public Task<PureModelResponse> GenerateAsync(PureModelRequest request, CancellationToken cancellationToken = default)
        {
            LastModel = request.ModelId;
            return Task.FromResult(PureModelResponse.FromText("local fixture response"));
        }
    }
}
