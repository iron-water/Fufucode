using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.Views;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

internal static class SessionOrganizationSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool condition, string name) { if (condition) pass("Sessions: " + name); else fail("Sessions: " + name); }
        Check(WorkbenchActionDefaults.Icon("speech.start") == "Speaker" && WorkbenchActionDefaults.Icon("speech.pause") == "Pause" &&
            WorkbenchActionDefaults.Icon("speech.resume") == "Play", "speech controls use speaker, pause and play semantics instead of a lightbulb");
        Check(new[] { "Speaker", "Pause", "Play" }.All(name => Application.Current!.TryGetResource("Icon." + name, null, out var resource) &&
            resource is Avalonia.Media.Geometry), "all speech action icons resolve to actual Fluent geometry assets");
        var scratch = Path.Combine(Path.GetTempPath(), "fufucode-organization", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(scratch, "source");
        var destination = Path.Combine(scratch, "new folder");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        var store = new DesktopMetadataStore(new AppDataLayout(Path.Combine(scratch, "data")));
        var project = store.GetOrCreateProject(source);
        var other = store.GetOrCreateProject(destination);
        var session = store.CreateSession(project, "Original");
        var second = store.CreateSession(project, "Other");
        var organization = new SessionOrganization(store);
        Check(store.ListProjects().Count == 2 && organization.Placement(session.Id).ProjectId == project.Id,
            "registered projects list and default session placement use real project identity");
        var section = organization.AddSection("研究");
        organization.RenameSection(section.Id, "实现");
        Check(new SessionOrganization(store).State.Sections.Single() == section with { Name = "实现" }, "section rename survives reload with stable ID");
        organization.EditProject(project, "Renamed", destination, section.Id);
        var restored = new SessionOrganization(store);
        Check(restored.Project(project) is { Name: "Renamed" } settings && settings.Folder == destination && settings.SectionId == section.Id,
            "project name, folder and section survive reload");
        Check(store.GetProject(project.Id).Path == source && store.GetSession(session.Id).ProjectId == project.Id,
            "editing future project folder does not mutate an existing session working directory");
        organization.PlaceSession(session.Id, other.Id, null);
        Check(new SessionOrganization(store).Placement(session.Id).ProjectId == other.Id && organization.Placement(second.Id).ProjectId == project.Id,
            "project menu changes only the targeted session placement");
        organization.PlaceSession(session.Id, null, section.Id);
        Check(new SessionOrganization(store).Placement(session.Id) == new SessionPlacement(null, section.Id), "section menu persists target placement");
        organization.PlaceSession(session.Id, null, null);
        Check(new SessionOrganization(store).Placement(session.Id) == new SessionPlacement(), "no-project menu persists ungrouped placement");
        var before = store.GetPreference("sessions:organization:v1");
        try { organization.PlaceSession(session.Id, "missing-project", null); Check(false, "invalid project rejected"); }
        catch (System.Collections.Generic.KeyNotFoundException) { Check(store.GetPreference("sessions:organization:v1") == before, "invalid project rejected without writing preferences"); }
        try { organization.EditProject(project, "Bad", Path.Combine(scratch, "missing"), null); Check(false, "missing folder rejected"); }
        catch (DirectoryNotFoundException) { Check(store.GetPreference("sessions:organization:v1") == before, "missing project folder rejected without changing persisted state"); }
        organization.SetActivity(true); organization.SetPriority("scheduled");
        Check(new SessionOrganization(store).State is { ActivityView: true, Priority: "scheduled" }, "activity view and nested priority choice persist");
        foreach (var invalid in new[] { "{broken", "{\"Sections\":null}", "{\"Projects\":{\"id\":null}}", "{\"Priority\":\"unknown\"}" })
        {
            store.SetPreference("sessions:organization:v1", invalid);
            var fallback = new SessionOrganization(store);
            Check(fallback.RestoreError is not null && fallback.State.Sections.Count == 0 &&
                store.GetPreference("sessions:organization:v1") == invalid, "corrupt grouping degrades without replacing original preferences: " + invalid);
        }
        foreach (var id in new[] { session.Id, second.Id }) SessionPresentation.Save(store, id, new());
        SessionPresentation.Save(store, session.Id, new(Pinned: true, Unread: true));
        Check(SessionPresentation.Load(store, session.Id) is { Pinned: true, Unread: true } && !SessionPresentation.Load(store, second.Id).Pinned,
            "pin and unread metadata target only the requested row");
        try
        {
            // Existing files satisfy runtime discovery, but must never be executed: the project guard is earlier.
            var assembly = typeof(MainWindow).Assembly.Location;
            await RealSessionHost.OpenAsync(new(AppSessionMode.Real, destination, session.Id,
                assembly, assembly, Path.Combine(scratch, "data")), default);
            Check(false, "explicit session project mismatch rejected before Pi starts");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("不匹配", StringComparison.Ordinal))
        { Check(true, "explicit session project mismatch rejected before Pi starts"); }
        var window = new MainWindow { Width = 1100, Height = 760 };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.RenderOverview();
            var view = (OverviewView)window.PageForTesting("overview");
            Check(new[] { "置顶", "分区", "项目", "会话" }.All(label => view.Body.Children.OfType<TextBlock>().Any(t => t.Text == label)),
                "default management page exposes all four top-level categories");
            var options = view.Toolbar.Children.OfType<Grid>().Single().Children.OfType<Button>().Single(b => b.ContextMenu is not null);
            var submenu = options.ContextMenu!.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "显示");
            Check(submenu.Items.OfType<MenuItem>().Select(i => i.Header).SequenceEqual(new[] { "优先事项部分", "置顶", "定时任务" }),
                "priority display choices are real nested menu items");
            var mode = view.Toolbar.Children.OfType<Grid>().Single().Children.OfType<ToggleSwitch>().Single();
            mode.IsChecked = true;
            Check(view.Body.Children.OfType<TextBlock>().Any(t => t.Text == "优先级"), "activity switch changes the rendered management view");
        }
        finally { window.Close(); }
    }
}
