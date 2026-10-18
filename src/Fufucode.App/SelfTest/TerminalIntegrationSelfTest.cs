using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

internal static class TerminalIntegrationSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string name) { if (value) pass("TerminalIntegration: " + name); else fail("TerminalIntegration: " + name); }
        var prior = Program.SelfTestDataRoot;
        Program.SelfTestDataRoot = Path.Combine(Path.GetTempPath(), "fufucode-terminal-integration-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow { Width = 1440, Height = 900 };
        window.Show();
        try
        {
            await Ready(); window.OpenDockPage("terminal", Infrastructure.DockZone.Bottom); await Ready();
            var view = (TerminalDockView)window.PageForTesting("terminal");
            var surface = view.FindControl<TerminalScreenControl>("TerminalSurface")!;
            var tab = window.TerminalForTesting.SelectedTab!;
            var copied = new List<string>();
            surface.ClipboardWriterForTesting = text => { copied.Add(text); return Task.CompletedTask; };
            surface.ClipboardReaderForTesting = () => Task.FromResult<string?>("clipboard-draft-only");
            Check(new[] { "terminal.copy", "terminal.paste", "terminal.find", "terminal.bottom", "terminal.copy-or-interrupt", "terminal.interrupt", "terminal.new" }
                .All(id => window.ShortcutsForTesting.Rows.Any(row => row.Id == id)), "terminal actions appear in the real shared settings catalog");
            Check(window.ActionsForTesting.GetDisabledReason("terminal.copy") is not null,
                "output-copy action reports its unavailable reason without a selection");
            surface.Focus(); await Ready(); Press(window, Key.F, RawInputModifiers.Control);
            await Ready(); Check(tab.IsSearchOpen, "default Ctrl+F opens find through the shared terminal scope");
            CloseFind(); await Ready();
            window.ShortcutsForTesting.SetBinding("terminal.find", "Ctrl+Alt+F");
            surface.Focus(); await Ready(); Press(window, Key.F, RawInputModifiers.Control); await Ready();
            Check(!tab.IsSearchOpen, "rebinding find removes the old hard-coded Ctrl+F behavior");
            Press(window, Key.F, RawInputModifiers.Control | RawInputModifiers.Alt); await Ready();
            Check(tab.IsSearchOpen, "the newly configured find gesture works");
            Check(ToolTip.GetTip(view.FindControl<Button>("FindButton")!)?.ToString()?.Contains("Ctrl+Alt+F", StringComparison.Ordinal) == true,
                "find tooltip displays the active binding");
            CloseFind(); await Ready(); window.ShortcutsForTesting.SetBinding("terminal.find", null);
            surface.Focus(); await Ready(); Press(window, Key.F, RawInputModifiers.Control); await Ready();
            Press(window, Key.F, RawInputModifiers.Control | RawInputModifiers.Alt); await Ready();
            Check(!tab.IsSearchOpen, "disabled find has no fallback default or previous binding");
            window.ShortcutsForTesting.SetBinding("terminal.copy", "Ctrl+Alt+C");
            SelectOutput(); surface.Focus(); await Ready(); Press(window, Key.C, RawInputModifiers.Control | RawInputModifiers.Shift); await Ready();
            Check(copied.Count == 0, "old copy gesture does not copy after remapping");
            Press(window, Key.C, RawInputModifiers.Control | RawInputModifiers.Alt); await Ready();
            Check(copied.Count == 1 && copied[0] == tab.OutputSelection.Text, "new copy gesture copies the frozen selected output");
            window.ShortcutsForTesting.SetBinding("terminal.copy", null);
            surface.Focus(); await Ready(); Press(window, Key.C, RawInputModifiers.Control | RawInputModifiers.Shift); await Ready();
            Press(window, Key.C, RawInputModifiers.Control | RawInputModifiers.Alt); await Ready();
            Check(copied.Count == 1, "disabled output-copy does not revive either old gesture");
            tab.InputText = "kept-draft"; var outputBefore = tab.Buffer.PlainText;
            surface.Focus(); await Ready(); Press(window, Key.V, RawInputModifiers.Control); await Ready();
            Check(tab.InputText == "kept-draft\nclipboard-draft-only" && tab.Buffer.PlainText == outputBefore,
                "shared paste enters the existing draft without writing to the terminal");
            window.ShortcutsForTesting.SetBinding("terminal.paste", null); tab.InputText = "do-not-paste";
            surface.Focus(); await Ready(); Press(window, Key.V, RawInputModifiers.Control); await Ready();
            Press(window, Key.Insert, RawInputModifiers.Shift); await Ready();
            Check(tab.InputText == "do-not-paste", "disabled paste consumes neither old paste fallback");
            var commandInput = view.FindControl<TextBox>("TerminalInput")!;
            commandInput.Focus(); commandInput.SelectAll(); await Ready();
            Check(!window.RouteShortcutForTesting(Key.C, KeyModifiers.Control), "selected command-box text retains the editor's native Ctrl+C copy");
            window.ShortcutsForTesting.SetBinding("terminal.find", "Ctrl+Alt+F");
            var floatingId = window.FloatDockPage("terminal")!; await Ready();
            var floating = window.FloatingDockWindowForTesting(floatingId)!;
            surface.Focus(); await Ready(); Press(floating, Key.F, RawInputModifiers.Control | RawInputModifiers.Alt); await Ready();
            Check(tab.IsSearchOpen && ReferenceEquals(TopLevel.GetTopLevel(surface), floating),
                "a floating terminal uses its own focus and the same configured action scope");
            CloseFind(); await Ready();
            surface.Focus(); await Ready(); Press(floating, Key.F, RawInputModifiers.Control); await Ready();
            Check(!tab.IsSearchOpen, "floating terminal does not revive the removed default find gesture");
            var count = window.TerminalForTesting.Tabs.Count;
            var outcome = await window.ActionsForTesting.ExecuteAsync("terminal.new"); await Ready();
            Check(outcome.Executed && window.TerminalForTesting.Tabs.Count == count + 1, "shared new-terminal action invokes the existing project-owned terminal factory");
            window.DockFloatingRegion(floatingId, Infrastructure.DockZone.Right); await Ready();
            Check(ReferenceEquals(view, window.PageForTesting("terminal")), "public shortcut wiring does not replace the terminal across redocking");
            void SelectOutput() { tab.OutputSelection.Begin(tab.Snapshot, new(0, 0)); tab.OutputSelection.Extend(tab.Snapshot, new(0, 3)); }
            void CloseFind() => view.FindControl<Button>("CloseFindButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally { window.Close(); await Task.Delay(120); Program.SelfTestDataRoot = prior; }
        async Task Ready() { await Task.Delay(60); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        static void Press(Window target, Key key, RawInputModifiers modifiers) => target.KeyPress(key, modifiers, PhysicalKey.None, null);
    }
}
