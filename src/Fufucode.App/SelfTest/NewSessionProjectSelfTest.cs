using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App.SelfTest;

internal static class NewSessionProjectSelfTest
{
    internal static async Task RunAsync(Action<string> checkPass, Action<string> checkFail)
    {
        void Check(bool value, string name) { if (value) checkPass("New session project: " + name); else checkFail("New session project: " + name); }
        var root = Path.GetFullPath("tmp/new-session-project");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var layout = new AppDataLayout(Path.Combine(root, Guid.NewGuid().ToString("N")));
        layout.EnsureCreated();
        var manager = new PersistentSessionManager(string.Empty, string.Empty, layout);
        manager.SetProjectTrustDecision(project, ProjectTrustDecision.DoNotTrust);
        var scratch = ScratchWorkspace.Ensure(layout);
        manager.SetProjectTrustDecision(scratch, ProjectTrustDecision.DoNotTrust);
        var options = new AppOptions(AppSessionMode.Real, project, null, null, null, layout.Root, BackendId.NativeCSharp) with { NewSession = true };
        var window = new MainWindow(options);
        window.Show();
        try
        {
            for (var attempt = 0; attempt < 200 && window.CurrentSessionId is null; attempt++) await Task.Delay(50);
            Check(window.CurrentSessionId is not null, "isolated native session opens without a model request");
            await Invoke("NewChatAsync");
            var metadata = new DesktopMetadataStore(layout);
            var id = window.CurrentSessionId!;
            var record = metadata.GetSession(id);
            Check(RealSessionHost.PathEquals(metadata.GetProject(record.ProjectId).Path, scratch), "caption new chat defaults to no-project workspace");
            var target = metadata.GetOrCreateProject(project);
            await Invoke("MoveCurrentSessionToProjectAsync", target.Id);
            metadata = new DesktopMetadataStore(layout);
            Check(window.CurrentSessionId == id && metadata.GetSession(id).ProjectId == target.Id, "project selection preserves the new session identity and updates ownership");
            var vm = (MainViewModel)window.DataContext!;
            Check(vm.ProjectRootPath is { } actualProject && RealSessionHost.PathEquals(actualProject, project), "chat view-model execution context follows the selected project");
            SessionPresence.MarkEstablished(metadata, id);
            try { await Invoke("MoveCurrentSessionToProjectAsync", (object?)null); Check(false, "established session rejects project reassignment"); }
            catch (InvalidOperationException) { Check(true, "established session rejects project reassignment"); }
        }
        finally { window.Close(); }
        async Task Invoke(string name, params object?[] arguments)
        {
            try { await (Task)typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments)!; }
            catch (TargetInvocationException error) when (error.InnerException is not null) { throw error.InnerException; }
        }
    }
}
