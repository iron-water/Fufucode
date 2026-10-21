using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fufucode.App.Features.Archive;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

/// <summary>归档项目/会话的存储与恢复语义（不启动界面与后端进程）。</summary>
internal static class ArchiveSelfTest
{
    public static Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string name) { if (value) pass("Archive: " + name); else fail("Archive: " + name); }
        var root = Path.Combine(Path.GetTempPath(), "fufucode-archive", Guid.NewGuid().ToString("N"));
        var firstPath = Path.Combine(root, "first");
        var secondPath = Path.Combine(root, "second");
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);
        try
        {
            var store = new DesktopMetadataStore(new AppDataLayout(Path.Combine(root, "data")));
            var first = store.GetOrCreateProject(firstPath);
            var second = store.GetOrCreateProject(secondPath);
            var a1 = store.CreateSession(first, "A1");
            var a2 = store.CreateSession(first, "A2");
            var b1 = store.CreateSession(second, "B1");

            Check(!SessionArchive.IsArchived(store, a1) && !ProjectArchive.IsArchived(store, second.Id),
                "new projects and sessions start visible");

            SessionPresentation.Save(store, a1.Id, new SessionPresentation(Archived: true));
            Check(SessionArchive.IsArchived(store, a1) && !SessionArchive.IsArchived(store, a2),
                "archiving one session leaves its siblings visible");

            ProjectArchive.SetArchived(store, second.Id, true);
            Check(ProjectArchive.IsArchived(store, second.Id) && SessionArchive.IsArchived(store, b1) &&
                ProjectArchive.IsArchivedPath(store, secondPath),
                "project archive hides its sessions and resolves by path");
            Check(!ProjectArchive.IsArchivedPath(store, Path.Combine(root, "deleted-project")),
                "missing recent project path is not archived and does not throw");

            SessionRecord? openedSession = null;
            string? openedProject = null;
            var model = new ArchiveViewModel(store,
                record => { openedSession = record; return Task.CompletedTask; },
                path => { openedProject = path; return Task.CompletedTask; });

            var projects = model.LoadProjects();
            var sessions = model.LoadSessions();
            Check(projects.Count == 1 && projects[0].Id == second.Id && projects[0].SessionCount == 1,
                "archive page lists the archived project with its session count");
            Check(sessions.Count == 1 && sessions[0].Id == a1.Id && sessions[0].ProjectArchived == false,
                "archive page lists explicitly archived sessions only");

            model.RestoreSession(sessions[0]);
            Check(!SessionArchive.IsArchived(store, a1) && model.LoadSessions().Count == 0,
                "restoring a session returns it to the visible list");

            model.OpenProjectAsync(projects[0]).GetAwaiter().GetResult();
            Check(!ProjectArchive.IsArchived(store, second.Id) && openedProject == secondPath &&
                SessionArchive.IsArchived(store, store.GetSession(b1.Id)) == false,
                "opening an archived project restores it before switching folders");

            ProjectArchive.SetArchived(store, second.Id, true);
            SessionPresentation.Save(store, b1.Id, new SessionPresentation(Archived: true));
            var both = model.LoadSessions().Single(entry => entry.Id == b1.Id);
            model.RestoreSession(both);
            Check(!ProjectArchive.IsArchived(store, second.Id) && !SessionArchive.IsArchived(store, b1),
                "restoring a session inside an archived project restores the project too");

            SessionPresentation.Save(store, a2.Id, new SessionPresentation(Archived: true));
            var archivedA2 = model.LoadSessions().Single(entry => entry.Id == a2.Id);
            model.OpenSessionAsync(archivedA2).GetAwaiter().GetResult();
            Check(openedSession?.Id == a2.Id && !SessionArchive.IsArchived(store, a2),
                "opening an archived session restores it and activates that session id");

            var page = new ArchivePage(model);
            page.Refresh();
            Check(page.Content is Avalonia.Controls.StackPanel { Children.Count: >= 2 },
                "archive page renders project and session sections without a host window");

            var dialogInput = new Avalonia.Controls.TextBox();
            _ = AppDialog.Field("名称", dialogInput);
            Check(dialogInput.Classes.Contains("dialogInput"),
                "dialog fields use the shared non-black input style");
            var footer = AppDialog.Footer(new Avalonia.Controls.Button { Content = "保存" });
            Check(footer.Child is Avalonia.Controls.StackPanel
                {
                    HorizontalAlignment: Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment: Avalonia.Layout.VerticalAlignment.Center,
                }, "dialog action row is centered");

            // 设置页接线：归档分类可见；默认后端选择位于后端二级页且列出全部六类后端。
            var settings = new SettingsPageViewModel();
            settings.AttachNavPreference(Path.Combine(root, "nav"));
            Check(!settings.FilteredCategories.Any(category => category.Id == "archived") &&
                settings.FilteredCategories.Any(category => category.Id == "storage"),
                "archive lives under storage instead of being a top-level category");
            settings.SelectCategory("storage");
            Check(settings.ShowStorage && !settings.ShowArchive,
                "storage root shows its hub before entering the archive subpage");
            settings.ArchiveContent = page;
            settings.OpenArchivePage();
            Check(settings.ShowArchive && !settings.ShowStorage && !settings.ShowArchiveEmpty &&
                settings.CategoryTitle == "储存" && settings.PageTitle == "已归档" &&
                settings.Breadcrumb == "储存 › 已归档",
                "storage archive subpage renders with breadcrumb 储存 › 已归档");
            settings.SelectCategory("marketplace");
            settings.OpenPage("marketplace.backend");
            Check(settings.ShowBackendCard && settings.BackendEngineChoices.Count == 6 &&
                settings.BackendEngineChoices.Any(choice => choice.StartsWith("Pi", StringComparison.Ordinal)),
                "default backend selector lives on the backend secondary page and lists all six engines");
            _ = new SettingsPage { DataContext = settings };
        }
        catch (Exception error)
        {
            fail("Archive: unexpected exception: " + error);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }
}
