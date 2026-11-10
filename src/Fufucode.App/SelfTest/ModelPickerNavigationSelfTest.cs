using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

internal static class ModelPickerNavigationSelfTest
{
    internal static async Task RunAsync(MainWindow window, Action<string> pass, Action<string> fail)
    {
        void Check(bool ok, string text) { if (ok) pass("Model picker: " + text); else fail("Model picker: " + text); }
        var root = Path.GetFullPath("tmp/model-picker-navigation");
        Directory.CreateDirectory(root);
        var page = window.GetVisualDescendants().OfType<ChatPage>().First();
        var vm = (MainViewModel)page.DataContext!;
        var original = vm.Settings.AttachedContext;
        var current = "fixture/gpt-a";
        var switches = 0;
        var store = new AppSettingsStore(new AppDataLayout(Path.Combine(root, Guid.NewGuid().ToString("N"))));
        var ids = Enumerable.Range(0, 28).Select(i => "fixture/gpt-" + (char)('a' + i)).ToArray();
        Task<RpcResponse> Models(System.Threading.CancellationToken _) => Task.FromResult(new RpcResponse("fixture", "models", true,
            JsonSerializer.SerializeToElement(new { models = ids.Select(id => new { id }) }), null));
        var context = new SettingsPageViewModel.Context(store, false, () => current, Models,
            null, store.DataRoot, null, () => null, () => null, () => [], () => null,
            () => root, _ => Task.CompletedTask, _ => Task.CompletedTask, () => "fixture", () => "fixture",
            SetModel: (_, id, _) => { switches++; current = id; return Task.FromResult(new RpcResponse("fixture", "set_model", true, null, null)); },
            UsesEndpointModelIds: true);
        var button = page.FindControl<Button>("ModelButton")!;
        var flyout = (Flyout)button.Flyout!;
        var surface = (Control)flyout.Content!;
        var search = page.FindControl<TextBox>("ModelPickerSearchBox")!;
        try
        {
            vm.Settings.Attach(context);
            flyout.ShowAt(button);
            await Ready();
            search.Focus(); search.Text = "gpt";
            await Ready();
            var choices = vm.Settings.FilteredModelChoices;
            Check(choices.Count == 28, "search fixture exposes every matching row");
            var border = surface.GetVisualDescendants().OfType<ModelChoiceBorder>().First();
            Check(border.BorderThickness == new Thickness(1), "model outline uses the same one-DIP thickness as file/session rows");
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Check(choices.Count(item => item.IsOutlined) == 1 && choices[0].IsNavigationSelected && switches == 0,
                "Down in search moves a single outline without switching the runtime model");
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            await Ready();
            var outlined = surface.GetVisualDescendants().OfType<ModelChoiceBorder>()
                .Where(row => row.Classes.Contains("confirmed")).ToArray();
            Check(choices[1].IsNavigationSelected && !choices[0].IsOutlined && outlined.Length == 1 &&
                outlined[0].DataContext == choices[1] && outlined[0].BorderBrush is ISolidColorBrush brush &&
                window.TryFindResource("AppAccent", out var accent) && accent is ISolidColorBrush accentBrush && brush.Color == accentBrush.Color,
                "second Down renders exactly one theme-colored outline on the next result");
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Check(choices[0].IsNavigationSelected, "Up moves outline back while focus stays in search");
            Check(search.IsKeyboardFocusWithin, "navigation does not steal search focus");
            for (var i = 0; i < 30; i++) window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            await Ready();
            Check(choices[^1].IsNavigationSelected && surface.GetVisualDescendants().OfType<ScrollViewer>().Any(s => s.Offset.Y > 0),
                "navigating beyond viewport scrolls the selected row into view and stops at list end");
            search.Text = "gpt-b";
            await Ready();
            Check(vm.Settings.FilteredModelChoices.All(item => !item.IsNavigationActive), "editing search clears stale navigation after result rebuild");
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            var expected = vm.Settings.FilteredModelChoices.Single(item => item.IsNavigationSelected).Value;
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Ready();
            Check(current == expected && switches == 1 && vm.Settings.ConfirmedModel == expected,
                "Enter applies exactly the keyboard-selected result and retains actual backend confirmation");
            Check(vm.Settings.FilteredModelChoices.All(item => !item.IsNavigationActive), "Enter restores confirmed-model outline after navigation");
            search.Text = "gpt";
            await Ready();
            var presenter = search.GetVisualDescendants().OfType<TextPresenter>().First();
            presenter.PreeditText = "ni";
            var down = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = search };
            search.RaiseEvent(down);
            var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = search };
            search.RaiseEvent(enter);
            Check(switches == 1 && vm.Settings.FilteredModelChoices.All(item => !item.IsNavigationActive),
                "IME composition leaves candidate Down/Enter to input method without selecting a model");
            presenter.PreeditText = null;
            await Task.Delay(90);
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Check(vm.Settings.FilteredModelChoices[^1].IsNavigationSelected, "Up from a fresh search starts at the last visible result");
            flyout.Hide();
            await Ready();
            Check(vm.Settings.FilteredModelChoices.All(item => !item.IsNavigationActive), "closing picker releases transient outline state");
            flyout.ShowAt(button); await Ready();
            Check(string.IsNullOrEmpty(search.Text) && vm.Settings.FilteredModelChoices.All(item => !item.IsNavigationActive),
                "reopening clears old query and keyboard state");
        }
        finally
        {
            flyout.Hide();
            if (original is not null) vm.Settings.Attach(original);
        }
        async Task Ready()
        {
            await Task.Delay(120);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
