using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.Views;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

internal static class SessionManagementRealSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string name) { if (value) pass("Real sessions: " + name); else fail("Real sessions: " + name); }
        var root = Path.Combine(Path.GetTempPath(), "fufucode-real-menus", Guid.NewGuid().ToString("N"));
        var projectPath = Path.Combine(root, "source repo");
        var secondPath = Path.Combine(root, "另一个项目");
        Directory.CreateDirectory(projectPath); Directory.CreateDirectory(secondPath);
        var layout = new AppDataLayout(Path.Combine(root, "data"));
        var store = new DesktopMetadataStore(layout);
        var project = store.GetOrCreateProject(projectPath);
        var other = store.GetOrCreateProject(secondPath);
        var first = store.CreateSession(project, "First");
        var second = store.CreateSession(project, "Second");
        var options = new AppOptions(AppSessionMode.Real, projectPath, first.Id, null, null, layout.Root);
        var (node, cli) = RealSessionBootstrap.ResolveRuntime(options);
        var sdk = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cli)!, "..", "core", "session-manager.js"));
        foreach (var record in new[] { first, second })
        {
            var start = new ProcessStartInfo(node) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = projectPath };
            start.Environment["PI_CODING_AGENT_DIR"] = layout.AgentDirectory;
            foreach (var arg in new[] { "--input-type=module", "-e", """
                const { SessionManager } = await import(process.argv[1]);
                const s = SessionManager.create(process.argv[2], process.argv[3], {id:process.argv[4]});
                s.appendMessage({role:'user',content:[{type:'text',text:'Private menu seed'}],timestamp:Date.now()});
                s.appendMessage({role:'assistant',content:[{type:'text',text:'Private fixture reply'}],api:'openai-completions',
                    provider:'fixture',model:'fixture',usage:{input:1,output:1,cacheRead:0,cacheWrite:0,totalTokens:2,
                    cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}},stopReason:'stop',timestamp:Date.now()});
                console.log(JSON.stringify({file:s.getSessionFile()}));
                """, new Uri(sdk).AbsoluteUri, projectPath, layout.ProjectSessionsDirectory(project.Id), record.Id })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            if (process.ExitCode != 0) throw new Exception(await errors);
            using var seed = JsonDocument.Parse(await output);
            store.UpdateSessionFile(record.Id, seed.RootElement.GetProperty("file").GetString()!);
        }
        var window = new MainWindow(options) { Width = 1100, Height = 760 };
        window.Show();
        try
        {
            await Until(() => window.CurrentSessionId == first.Id);
            var view = (OverviewView)window.PageForTesting("overview");
            var sidebar = ((ViewModels.MainViewModel)window.DataContext!).Sidebar;
            Check(sidebar.NewSessionBackends.Select(option => option.Key)
                    .SequenceEqual(new[] { "fumo", "pi", "omp", "codex", "dsh", "claude" }),
                "overview new-session dropdown exposes all six backends");
            window.RenderOverview();
            var toolbarGrid = view.Toolbar.Children.OfType<Grid>().First();
            var addButton = toolbarGrid.Children.OfType<Button>()
                .Single(button => ToolTip.GetTip(button) as string == "新建分区或项目");
            Check(addButton.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header)
                    .SequenceEqual(new object[] { "新建分区", "添加项目" }),
                "overview plus menu offers new section and add project");
            Button Row(string id) => view.GetVisualDescendants().OfType<Button>()
                .First(b => AutomationProperties.GetAutomationId(b) == "session.row." + id);
            MenuItem Menu(string id, string title) => Row(id).ContextMenu!.Items.OfType<MenuItem>().Single(m => (string?)m.Header == title);
            void Invoke(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var row = Row(second.Id);
            Check(new[] { "重命名", "置顶", "标为未读", "项目", "分区", "分叉", "复制", "归档" }
                .All(title => row.ContextMenu!.Items.OfType<MenuItem>().Any(m => (string?)m.Header == title)),
                "persisted real rows expose all required context actions");
            var tail = view.GetVisualDescendants().OfType<StackPanel>().Single(stack =>
                AutomationProperties.GetAutomationId(stack) == "session.tail." + second.Id);
            Check(tail.Children.OfType<Button>().Select(AutomationProperties.GetName).SequenceEqual(new[] { "置顶", "归档" }),
                "row hover contains only pin and archive, not rename");
            Check(Menu(second.Id, "分叉").Items.OfType<MenuItem>().Select(m => m.Header)
                .SequenceEqual(new[] { "创建聊天分支", "在新工作树中创建分支" }), "fork submenu contains both real implementations");
            Check(Menu(second.Id, "复制").Items.OfType<MenuItem>().Select(m => m.Header)
                .SequenceEqual(new[] { "复制深度链接", "复制为 Markdown", "复制工作目录" }), "copy submenu contains the three required actions");
            Invoke(Menu(second.Id, "置顶"));
            Check(SessionPresentation.Load(store, second.Id).Pinned && !SessionPresentation.Load(store, first.Id).Pinned &&
                window.CurrentSessionId == first.Id, "pin acts on the context row without switching or changing the current row");
            var organization = new SessionOrganization(store);
            var section = organization.AddSection("原分区");
            window.RenderOverview();
            Invoke(Menu(second.Id, "分区").Items.OfType<MenuItem>().Single(m => (string?)m.Header == section.Name));
            Check(new SessionOrganization(store).Placement(second.Id).SectionId == section.Id,
                "section submenu writes the target session placement");
            Invoke(Menu(second.Id, "项目").Items.OfType<MenuItem>().Single(m => (string?)m.Header == organization.Project(other).Name));
            Check(new SessionOrganization(store).Placement(second.Id).ProjectId == other.Id && store.GetSession(second.Id).ProjectId == project.Id,
                "project submenu moves display grouping without silently changing execution cwd");
            var sectionHeader = view.GetVisualDescendants().OfType<Button>().Single(button =>
                AutomationProperties.GetAutomationId(button) == "section.row." + section.Id);
            Invoke(sectionHeader.ContextMenu!.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "重命名"));
            var sectionDialog = await Dialog("重命名分区");
            ((StackPanel)sectionDialog.Content!).Children.OfType<TextBox>().Single().Text = "Renamed section";
            ((StackPanel)sectionDialog.Content!).Children.OfType<StackPanel>().Single().Children.OfType<Button>()
                .Single(b => (string?)b.Content == "保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => new SessionOrganization(store).State.Sections.Single().Name == "Renamed section");
            Check(new SessionOrganization(store).State.Sections.Single().Name == "Renamed section", "section context rename persists the edited name");
            var projectRow = view.GetVisualDescendants().OfType<Button>().Single(b =>
                AutomationProperties.GetAutomationId(b) == "project.row." + project.Id);
            Invoke(projectRow.ContextMenu!.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "编辑项目"));
            var projectDialog = await Dialog("编辑项目");
            var projectPanel = (StackPanel)projectDialog.Content!;
            projectPanel.Children.OfType<TextBox>().Single().Text = "Renamed project";
            projectPanel.Children.OfType<Grid>().Single().Children.OfType<TextBox>().Single().Text = secondPath;
            projectPanel.Children.OfType<StackPanel>().Single().Children.OfType<Button>()
                .Single(b => (string?)b.Content == "保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(new SessionOrganization(store).Project(project) is { Name: "Renamed project" } edited && edited.Folder == secondPath &&
                store.GetProject(project.Id).Path == projectPath, "project editor persists name and future folder while retaining current execution cwd");
            var copy = Menu(second.Id, "复制");
            Invoke(copy.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "复制深度链接"));
            await UntilAsync(async () => SessionDeepLink.TryParse(await window.Clipboard!.TryGetTextAsync() ?? "", out var id, out var path) &&
                id == second.Id && path == projectPath);
            Check(true, "copy deep link contains the targeted ID and actual working directory");
            Invoke(Menu(second.Id, "复制").Items.OfType<MenuItem>().Single(m => (string?)m.Header == "复制工作目录"));
            await UntilAsync(async () => await window.Clipboard!.TryGetTextAsync() == projectPath);
            Check(window.CurrentSessionId == first.Id, "copy working directory does not change the active conversation");
            Invoke(Menu(second.Id, "重命名"));
            var dialog = await Dialog("重命名会话");
            ((StackPanel)dialog.Content!).Children.OfType<TextBox>().Single().Text = "Renamed target";
            ((StackPanel)dialog.Content!).Children.OfType<StackPanel>().Single().Children.OfType<Button>()
                .Single(b => (string?)b.Content == "保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => store.GetSession(second.Id).Name == "Renamed target");
            Check(store.GetSession(first.Id).Name == "First" && window.CurrentSessionId == second.Id,
                "rename selects and updates only its target through real Pi set_session_name");
            await window.SwitchSessionForOverviewTestingAsync(first.Id);
            window.RenderOverview();
            Invoke(Menu(second.Id, "归档"));
            Check(SessionPresentation.Load(store, second.Id).Archived && window.CurrentSessionId == first.Id &&
                File.Exists(store.GetSession(second.Id).FilePath!), "archive of inactive target preserves history and the active conversation");
            Check(!view.GetVisualDescendants().OfType<Button>()
                .Any(b => AutomationProperties.GetAutomationId(b) == "session.row." + second.Id), "archived target leaves the visible grouped rows");
        }
        finally
        {
            foreach (var dialog in ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Windows
                .Where(w => w != window && w.Owner == window).ToArray()) dialog.Close();
            window.Close();
        }
    }

    private static async Task Until(Func<bool> predicate) => await UntilAsync(() => Task.FromResult(predicate()));
    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(60);
        while (!await predicate())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Real session UI did not reach the expected state.");
            await Task.Delay(30);
        }
    }
    private static async Task<Window> Dialog(string title)
    {
        Window? result = null;
        await Until(() => (result = ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Windows
            .FirstOrDefault(w => w.Title == title)) is not null);
        return result!;
    }
}
