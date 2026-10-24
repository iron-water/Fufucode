using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Fufucode.App.Features.Plugins;

namespace Fufucode.App.SelfTest;

internal static class LocalPluginIntegrationSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool condition, string name) { if (condition) pass("LocalPlugins: " + name); else fail("LocalPlugins: " + name); }
        var previous = Program.SelfTestDataRoot;
        var root = Path.Combine(Path.GetTempPath(), "fufucode-plugins-integration-" + Guid.NewGuid().ToString("N"));
        Program.SelfTestDataRoot = Path.Combine(root, "data");
        var source = Path.Combine(root, "本地 插件源");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "index.js"), "export default () => {}; // integration fixture, never executed\n");
        File.WriteAllText(Path.Combine(source, "fufucode-plugin.json"), JsonSerializer.Serialize(new
        {
            id = "m.integration.plugin", displayName = "本地版本测试", version = "1.0.0", backend = "pi",
            entryRelativePath = "index.js", supportedHostVersions = new[] { "0.87.1" },
        }));
        var original = File.ReadAllBytes(Path.Combine(source, "index.js"));
        var window = new MainWindow { Width = 1100, Height = 800 };
        window.Show();
        try
        {
            var plugins = window.LocalPluginViewModelForTesting;
            using var surface = new LocalPluginTestSurface(plugins);
            var section = surface.Section;
            Check(window.LocalPluginsForTesting.Root == Path.Combine(Program.SelfTestDataRoot, "plugins", "local"),
                "shared composition uses the isolated AppDataLayout root");
            Check(section.IsAttached && ReferenceEquals(section.DataContext, plugins),
                "test host embeds the actual local version view model");
            Check(plugins.CanPickDirectory && !plugins.HasHost, "real picker is wired without inventing a Pi host");
            await Task.Delay(60); surface.Window.UpdateLayout();
            Check(section.IsAttached && ReferenceEquals(TopLevel.GetTopLevel(section), surface.Window),
                "test host exposes the section in a real window");
            var applyButton = section.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Content?.ToString() == "应用所选版本");
            var applyText = new TextBlock { Text = "应用所选版本", FontSize = applyButton.FontSize };
            applyText.Measure(new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));
            Check(applyButton.Bounds.Width >= applyText.DesiredSize.Width +
                  applyButton.Padding.Left + applyButton.Padding.Right,
                "actual shared theme allows the whole apply label instead of an icon-sized clipped button");
            await plugins.RefreshCommand.ExecuteAsync(null);
            Check(plugins.HasSnapshot && !plugins.HasPlugins && !plugins.CanApply,
                "fresh catalog is empty and does not offer an unimplemented apply");
            plugins.SourceDirectory = source;
            await plugins.PreviewCommand.ExecuteAsync(null);
            Check(plugins.CanImport && plugins.HasPreview && !plugins.HasPreviewError,
                "offline preview allows a valid local copy with an unknown host");
            await plugins.ImportCommand.ExecuteAsync(null);
            Check(plugins.Plugins.Count == 1 && plugins.Versions.Count == 1 && plugins.SelectedVersion is not null,
                "the embedded compiled list shows the imported fixed version");
            Check(section.FindControl<ListBox>("PluginList")!.ItemCount == 1 &&
                  section.FindControl<ListBox>("VersionList")!.ItemCount == 1,
                "both actual compiled list bindings render the catalog");
            Check(!plugins.CanApply && !plugins.CanRollback && !plugins.SelectedPlugin!.StatusIsSuccess,
                "import never masquerades as actual loading or rollback capability");
            plugins.SourceDirectory = Path.Combine(root, "different missing source");
            Check(!plugins.CanImport && !plugins.HasPreview,
                "editing the import source invalidates the prior successful preview immediately");
            plugins.SourceDirectory = source;
            await plugins.PreviewCommand.ExecuteAsync(null);
            Check(ReferenceEquals(section.ViewModel, plugins) && !plugins.IsDisposed && plugins.Versions.Count == 1,
                "the live local catalog and selection survive command round-trips");
            await plugins.RemoveCommand.ExecuteAsync(null);
            Check(plugins.Versions.Count == 1 && plugins.SelectedPlugin!.HasPending,
                "unknown host removal is pending and keeps the fixed copy");
            Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(source, "index.js"))),
                "import and pending removal leave user source bytes intact");
            var reopen = await new LocalPluginService(new Fufucode.Core.AppDataLayout(Program.SelfTestDataRoot)).ListAsync();
            Check(reopen.Items.Count == 1 && reopen.Items[0].Versions.Count == 1,
                "catalog persists for a new service instance on the same data root");

            using var failing = new LocalPluginViewModel(window.LocalPluginsForTesting, new TestPicker
            {
                Directory = _ => throw new IOException("picker directory unavailable"),
                Entry = (_, _) => throw new IOException("picker entry unavailable"),
            });
            var escaped = false;
            try { await failing.PickDirectoryCommand.ExecuteAsync(null); } catch (IOException) { escaped = true; }
            Check(!escaped && failing.StatusText.Contains("picker directory unavailable", StringComparison.Ordinal),
                "native directory picker failure becomes a visible diagnostic instead of an unhandled command");
            failing.SourceDirectory = source;
            escaped = false;
            try { await failing.PickEntryCommand.ExecuteAsync(null); } catch (IOException) { escaped = true; }
            Check(!escaped && failing.StatusText.Contains("picker entry unavailable", StringComparison.Ordinal),
                "native entry picker failure becomes a visible diagnostic");

            var late = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var closing = new LocalPluginViewModel(window.LocalPluginsForTesting,
                new TestPicker { Directory = _ => late.Task });
            var choosing = closing.PickDirectoryCommand.ExecuteAsync(null);
            closing.Dispose(); late.SetResult(source);
            try { await choosing; } catch (OperationCanceledException) { }
            Check(closing.SourceDirectory.Length == 0, "late picker result cannot mutate a released page");

            var entryResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var changedSource = new LocalPluginViewModel(window.LocalPluginsForTesting,
                new TestPicker { Entry = (_, _) => entryResult.Task });
            changedSource.SourceDirectory = source;
            var entryChoice = changedSource.PickEntryCommand.ExecuteAsync(null);
            Check(changedSource.IsBusy && !changedSource.PickDirectoryCommand.CanExecute(null),
                "an active entry picker disables a competing directory picker");
            changedSource.SourceDirectory = Path.Combine(root, "new source");
            entryResult.SetResult(Path.Combine(source, "index.js"));
            await entryChoice;
            Check(changedSource.EntryRelativePath.Length == 0 && !changedSource.IsBusy,
                "late entry choice cannot attach an old source path to the newly edited source");

            using var cancelledPicker = new LocalPluginViewModel(window.LocalPluginsForTesting,
                new TestPicker { Directory = _ => Task.FromCanceled<string?>(new CancellationToken(true)) });
            await cancelledPicker.PickDirectoryCommand.ExecuteAsync(null);
            Check(!cancelledPicker.IsBusy && cancelledPicker.SourceDirectory.Length == 0 &&
                  cancelledPicker.StatusText.Contains("取消", StringComparison.Ordinal),
                "picker cancellation restores commands without changing the source");

            var notifications = new List<string?>();
            plugins.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            plugins.SourceDirectory = Path.Combine(root, "missing");
            await plugins.PreviewCommand.ExecuteAsync(null);
            Check(plugins.HasPreviewError && notifications.Contains(nameof(plugins.HasPreviewError)) &&
                  notifications.Contains(nameof(plugins.HasPreview)),
                "invalid preview notifies derived visibility bindings so the failure is visible");
        }
        finally
        {
            window.Close(); await Task.Delay(150);
            Check(window.LocalPluginViewModelForTesting.IsDisposed, "final shell close releases the embedded view model");
            Program.SelfTestDataRoot = previous;
            Console.WriteLine("LocalPlugins evidence root: " + root);
        }
    }

    private sealed class TestPicker : ILocalPluginPicker
    {
        public Func<CancellationToken, Task<string?>> Directory { get; init; } = _ => Task.FromResult<string?>(null);
        public Func<string, CancellationToken, Task<string?>> Entry { get; init; } = (_, _) => Task.FromResult<string?>(null);
        public Task<string?> PickPluginDirectoryAsync(CancellationToken cancellationToken = default) => Directory(cancellationToken);
        public Task<string?> PickEntryFileAsync(string rootDirectory, CancellationToken cancellationToken = default) => Entry(rootDirectory, cancellationToken);
    }
}
