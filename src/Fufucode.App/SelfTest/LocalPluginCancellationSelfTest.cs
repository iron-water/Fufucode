using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

internal static class LocalPluginCancellationSelfTest
{
    internal static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool ok, string name) { if (ok) pass("Plugin cancel: " + name); else fail("Plugin cancel: " + name); }
        var previous = Program.SelfTestDataRoot;
        var root = Path.Combine(Path.GetTempPath(), "fufucode-plugin-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var scenario in new[] { "before", "during", "close", "trust", "late-cancel", "late-close", "late-page-switch" })
            {
                var project = Path.Combine(root, scenario, "project"); Directory.CreateDirectory(project);
                var layout = new AppDataLayout(Path.Combine(root, scenario, "data")); layout.EnsureCreated();
                File.WriteAllText(Path.Combine(layout.AgentDirectory, "models.json"), """
                {"providers":{"proof":{"baseUrl":"http://127.0.0.1:9/v1","api":"openai-completions","apiKey":"offline","models":[{"id":"devin/swe-2","name":"offline","reasoning":false,"input":["text"],"contextWindow":10000,"maxTokens":1000,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}]}}}
                """);
                new AppSettingsStore(layout).Update(settings => settings with { SelectedModel = "proof/devin/swe-2" });
                Program.SelfTestDataRoot = layout.Root;
                var options = new AppOptions(AppSessionMode.Real, project, null, null, null, layout.Root, BackendId.Pi);
                RealSessionHost? host = null;
                var window = new MainWindow(options) { Width = 1000, Height = 720 };
                window.OpenHostForTesting = async token => host = await RealSessionHost.OpenAsync(options, token);
                window.Show();
                try
                {
                    await Until(() => host is not null && !window.FindControl<Border>("StartupOverlay")!.IsVisible);
                    var sessionId = host!.Current.Session.Id;
                    var connection = host.Current.Coordinator;
                    var generation = connection.Snapshot.Generation;
                    ((MainViewModel)window.DataContext!).Input.DraftText = "取消仍保留草稿 中文";
                    await ((MainViewModel)window.DataContext!).NavigateCommand.ExecuteAsync(AppPage.Extensions);
                    var entered = Path.Combine(layout.Root, "entered");
                    var candidate = Path.Combine(layout.Root, "cancel-candidate.js");
                    if (scenario.StartsWith("late-", StringComparison.Ordinal))
                    {
                        File.WriteAllText(candidate, "export default function(pi) {}\n");
                        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        host.ExtensionsResumedForTesting = () => { resumed.TrySetResult(); return release.Task; };
                        using var lateCancel = new CancellationTokenSource();
                        var lateApply = window.ApplyExtensionsForTestingAsync(new[] { candidate }, lateCancel.Token);
                        try
                        {
                            await resumed.Task.WaitAsync(TimeSpan.FromSeconds(35));
                            Check(host.HasLiveSession && !ReferenceEquals(host.Current.Coordinator, connection) &&
                                host.Current.Coordinator.Snapshot.Connection == ConnectionState.Connected &&
                                host.Current.Session.Id == sessionId && host.EnabledExtensionPaths.Count == 0,
                                scenario + ": actual candidate Pi has resumed before persisted selection is committed");
                            if (scenario == "late-page-switch")
                                await ((MainViewModel)window.DataContext!).NavigateCommand.ExecuteAsync(AppPage.Chat);
                            else if (scenario == "late-close") window.Close(); else lateCancel.Cancel();
                        }
                        finally { release.TrySetResult(); }
                        if (scenario == "late-page-switch")
                        {
                            var success = await lateApply.WaitAsync(TimeSpan.FromSeconds(20));
                            Check(success is null && host.HasLiveSession && host.Current.Session.Id == sessionId &&
                                ((MainViewModel)window.DataContext!).CurrentPage == AppPage.Chat,
                                "user navigation during actual resumed Pi maintenance wins over the original extension page");
                            Check(((MainViewModel)window.DataContext!).Input.DraftText == "取消仍保留草稿 中文" &&
                                !window.FindControl<Border>("StartupOverlay")!.IsVisible,
                                "late page navigation still restores the original draft and completes the live workspace");
                            await ((MainViewModel)window.DataContext!).NavigateCommand.ExecuteAsync(AppPage.Extensions);
                            Check(ReferenceEquals(window.FindControl<ContentControl>("PageHost")!.Content, window.ExtensionsPageForTesting),
                                "after maintenance user can explicitly return to the actual extension page");
                            continue;
                        }
                        var cancelledLate = false;
                        try { await lateApply.WaitAsync(TimeSpan.FromSeconds(20)); }
                        catch (OperationCanceledException) { cancelledLate = true; }
                        catch (ObjectDisposedException) when (scenario == "late-close") { cancelledLate = true; }
                        Check(cancelledLate && host.EnabledExtensionPaths.Count == 0 && host.ActiveExtensionPaths.Count == 0 &&
                            host.Manager.UserExtensionPaths.Count == 0,
                            scenario + ": late cancellation cannot publish candidate runtime or persisted selection");
                        Check(!host.HasLiveSession, scenario + ": uncommitted candidate connection is released instead of reported live");
                        if (scenario == "late-close")
                        {
                            await Until(() => !window.IsVisible);
                            Check(window.LocalPluginViewModelForTesting.IsDisposed,
                                "late close finishes and releases the original plugin view model");
                        }
                        else
                        {
                            Check(window.FindControl<Button>("StartupRetryButton")!.IsVisible &&
                                !window.FindControl<ProgressBar>("StartupProgress")!.IsVisible,
                                "late cancellation exposes explicit recovery without an endless progress overlay");
                            if (host.Manager.UserExtensionPaths.Count == 0)
                            {
                                host.ExtensionsResumedForTesting = null;
                                window.FindControl<Button>("StartupRetryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                await Until(() => host.HasLiveSession && !window.FindControl<Border>("StartupOverlay")!.IsVisible);
                                Check(host.Current.Session.Id == sessionId && host.ActiveExtensionPaths.Count == 0 &&
                                    ((MainViewModel)window.DataContext!).Input.DraftText == "取消仍保留草稿 中文",
                                    "late cancellation explicit recovery retains the original session and draft");
                            }
                        }
                        continue;
                    }
                    File.WriteAllText(candidate, "import fs from 'node:fs'; export default async function(pi) { fs.writeFileSync(" +
                        JsonSerializer.Serialize(entered) + ", 'entered'); await new Promise(resolve => { const timer = setInterval(() => { if(fs.existsSync(" +
                        JsonSerializer.Serialize(entered + ".release") + ")) { clearInterval(timer); resolve(); } }, 20); }); }\n");
                    if (scenario == "before")
                    {
                        var cancelled = false;
                        try { await host.ApplyExtensionsAndRestartAsync(new[] { candidate }, new CancellationToken(true)); }
                        catch (OperationCanceledException) { cancelled = true; }
                        Check(cancelled && host.HasLiveSession && ReferenceEquals(host.Current.Coordinator, connection) &&
                            host.Current.Coordinator.Snapshot.Generation == generation,
                            "already-cancelled apply preserves the original live Pi connection");
                        Check(host.Manager.UserExtensionPaths.Count == 0 && host.EnabledExtensionPaths.Count == 0 && !File.Exists(entered),
                            "already-cancelled apply leaves runtime and persisted extension selection untouched");
                        Check(!connection.IsInIdleMaintenance, "pre-call cancellation leaves no maintenance reservation");
                        var shellCancelled = false;
                        try { await window.ApplyExtensionsForTestingAsync(new[] { candidate }, new CancellationToken(true)); }
                        catch (OperationCanceledException) { shellCancelled = true; }
                        Check(shellCancelled && host.HasLiveSession && ReferenceEquals(host.Current.Coordinator, connection) &&
                            !window.FindControl<Border>("StartupOverlay")!.IsVisible,
                            "already-cancelled shell request returns to the original live workspace");
                        Check(((MainViewModel)window.DataContext!).Session.Connection == Fufucode.App.Abstractions.SessionConnection.Connected &&
                            ((MainViewModel)window.DataContext!).Input.DraftText == "取消仍保留草稿 中文",
                            "pre-call cancellation preserves connected chat and draft in the actual shell");
                        Check(((MainViewModel)window.DataContext!).CurrentPage == AppPage.Extensions &&
                            ReferenceEquals(window.FindControl<ContentControl>("PageHost")!.Content, window.ExtensionsPageForTesting),
                            "pre-call cancellation preserves the actual visible extension page without navigation");
                        continue;
                    }
                    if (scenario == "trust")
                    {
                        Directory.CreateDirectory(Path.Combine(project, ".pi"));
                        File.WriteAllText(Path.Combine(project, ".pi", "settings.json"), "{}\n");
                        var asked = 0;
                        window.AskTrustForTesting = _ => { asked++; return Task.FromResult<ProjectTrustDecision?>(null); };
                        var declined = false;
                        try { await window.ApplyExtensionsForTestingAsync(new[] { candidate }, CancellationToken.None); }
                        catch (ProjectTrustDeclinedException) { declined = true; }
                        Check(declined && asked == 1 && host.HasLiveSession && ReferenceEquals(host.Current.Coordinator, connection),
                            "real trust decline retains original connection and asks exactly once");
                        Check(host.Manager.UserExtensionPaths.Count == 0 && !File.Exists(entered) &&
                            !window.FindControl<Border>("StartupOverlay")!.IsVisible,
                            "real trust decline never initializes the candidate or leaves a blocking overlay");
                        Check(((MainViewModel)window.DataContext!).CurrentPage == AppPage.Extensions,
                            "trust decline leaves the actual extension page selected for its diagnostic");
                        continue;
                    }
                    using var cancel = new CancellationTokenSource();
                    var applying = window.ApplyExtensionsForTestingAsync(new[] { candidate }, cancel.Token);
                    await Until(() => File.Exists(entered));
                    Check(!host.HasLiveSession && window.FindControl<ProgressBar>("StartupProgress")!.IsVisible,
                        scenario + ": candidate initialization is genuinely in flight after releasing the old process");
                    var duplicate = await window.ApplyExtensionsForTestingAsync(Array.Empty<string>(), CancellationToken.None);
                    Check(duplicate is not null && !host.HasLiveSession && host.Manager.UserExtensionPaths.SequenceEqual(new[] { candidate }),
                        scenario + ": duplicate shell apply cannot start a second restart or replace the in-flight candidate");
                    if (scenario == "close") window.Close(); else cancel.Cancel();
                    var wasCancelled = false;
                    try { await applying.WaitAsync(TimeSpan.FromSeconds(20)); }
                    catch (OperationCanceledException) { wasCancelled = true; }
                    Check(wasCancelled && host.Manager.UserExtensionPaths.Count == 0 && host.EnabledExtensionPaths.Count == 0,
                        scenario + ": cancellation restores old runtime selection and never persists the candidate");
                    Check(File.ReadAllText(candidate).Contains("await new Promise"),
                        scenario + ": cancellation retains the candidate source bytes");
                    if (scenario == "close")
                    {
                        await Until(() => window.LocalPluginViewModelForTesting.IsDisposed);
                        Check(!host.HasLiveSession && window.LocalPluginViewModelForTesting.IsDisposed,
                            "closing in-flight apply leaves no resurrected session or live plugin view model");
                    }
                    else
                    {
                        Check(window.FindControl<Border>("StartupOverlay")!.IsVisible &&
                            !window.FindControl<ProgressBar>("StartupProgress")!.IsVisible &&
                            window.FindControl<Button>("StartupRetryButton")!.IsVisible,
                            "cancel after release ends progress and exposes explicit recovery");
                        // A failed baseline keeps the loader installed; skip retry rather than hang on that same candidate.
                        if (host.Manager.UserExtensionPaths.Count != 0) continue;
                        window.FindControl<Button>("StartupRetryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Until(() => host.HasLiveSession && !window.FindControl<Border>("StartupOverlay")!.IsVisible);
                        Check(host.Current.Session.Id == sessionId && host.ActiveExtensionPaths.Count == 0 &&
                            ((MainViewModel)window.DataContext!).Input.DraftText == "取消仍保留草稿 中文",
                            "explicit recovery resumes the original session/draft with old extension selection");
                    }
                }
                finally { window.Close(); await Task.Delay(150); }
            }
        }
        finally { Program.SelfTestDataRoot = previous; Console.WriteLine("plugin cancellation evidence root: " + root); }
        static async Task Until(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(35);
            while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("plugin cancellation state not reached"); await Task.Delay(20); }
        }
    }
}
