using System.Diagnostics;
using System.Threading.Channels;
using Fufucode.Core.Security;

namespace Fufucode.Fui.Tests;

internal static partial class WorkbenchFlowTests
{
    public static async Task RunAsync()
    {
        var root = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("FUFUCODE_TEST_ROOT") ?? Path.GetTempPath(), "fui-flow-" + Guid.NewGuid().ToString("N")));
        try
        {
            var project = root.CreateSubdirectory("project").FullName;
            var data = root.CreateSubdirectory("data").FullName;
            var sub = Directory.CreateDirectory(Path.Combine(project, "src"));
            var path = Path.Combine(sub.FullName, "hello world.cs");
            await File.WriteAllTextAsync(path, "first fixture");
            var files = new ProjectFiles(project, data);
            Check(files.List(".", default).Items.Single().Value == "src/", "root browser is scoped to this project");
            Check(files.List("src/", default).Items.Any(c => c.Value == "src/hello world.cs"), "paths with spaces selectable");
            var first = await files.ReadAsync("src/hello world.cs", default);
            Check((await files.PrepareAsync("review", [first], default)).Text.Contains("[项目文件：src/hello world.cs]\nfirst fixture"), "actual selected content submitted");
            await File.WriteAllTextAsync(path, "other fixture");
            await Reject<IOException>(() => files.PrepareAsync("review", [first], default));
            await Reject<UnauthorizedAccessException>(() => files.ReadAsync("../outside", default));
            await Reject<ArgumentException>(() => files.ReadAsync(path, default));
            await File.WriteAllTextAsync(Path.Combine(project, ".env"), "fixture=1");
            await Reject<UnauthorizedAccessException>(() => files.ReadAsync(".env", default));
            await File.WriteAllBytesAsync(Path.Combine(project, "binary.dat"), [0, 1]);
            await Reject<InvalidDataException>(() => files.ReadAsync("binary.dat", default));
            await File.WriteAllBytesAsync(Path.Combine(project, "large.dat"), new byte[ProjectFiles.MaxFileBytes + 1]);
            await Reject<IOException>(() => files.ReadAsync("large.dat", default));
            var permissions = new SecurityPolicyStore(data);
            var original = permissions.Current;
            var snapshot = await files.ReadAsync("src/hello world.cs", default);
            permissions.Update(s => s with { FileRules = [new("**/*.cs", SecurityAction.Deny), .. s.FileRules] });
            Check(files.List("src/", default).Items.All(c => !c.Value.EndsWith(".cs")), "denied entry not offered");
            await Reject<UnauthorizedAccessException>(() => files.PrepareAsync("review", [snapshot], default));
            permissions.Update(_ => original);
            await CheckGitPreviewAsync(project, files);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var s = new Session(project, "first"); var ui = new TuiController(s, cts.Token, data);
            void Type(string text) { foreach (var c in text) ui.Key(new(c, (ConsoleKey)0, false, false, false), 12, 25); }
            void Key(ConsoleKey k, bool ctrl = false) => ui.Key(new('\0', k, false, false, ctrl), 12, 25);
            async Task Until(Func<TuiView, bool> ok)
            {
                do { ui.Update(); if (ok(ui.View())) return; await Task.Delay(10, cts.Token); } while (true);
            }
            try
            {
                Type("/mod"); Check(ui.View().Overlay!.Items.Any(c => c.Value == "model"), "command discovery");
                Key(ConsoleKey.Tab); Check(ui.View().Draft == "/model", "Tab completes but does not execute");
                Key(ConsoleKey.Enter); await Until(v => v.Overlay?.Title == "切换模型");
                Type("second"); Key(ConsoleKey.Enter); await Until(v => v.Lines.Any(l => l.Text == "model=second"));
                Check(s.Sent.Count == 0, "navigation does not send a prompt");
                Type("/attach src/hello world.cs"); Key(ConsoleKey.Enter); await Until(v => v.Status.Contains("附件 1"));
                Type("saved draft"); Key(ConsoleKey.P, true); Type("new"); Key(ConsoleKey.Enter);
                await Until(v => v.Session == "child"); Check(ui.View().Draft == "" && !ui.View().Status.Contains("附件"), "new session has its own context");
                Type("/resume first"); Key(ConsoleKey.Enter); await Until(v => v.Session == "first");
                Check(ui.View().Draft == "saved draft" && ui.View().Status.Contains("附件 1"), "switch restores unsent draft plus files");
                Key(ConsoleKey.Enter); await Until(v => v.Draft == "" && !v.Busy);
                Check(s.Created.Last().Sent.Single().Contains("[项目文件：src/hello world.cs]\nother fixture"), "attached file sent to correct session only");
                Type("//unknown"); Key(ConsoleKey.Enter); await Until(_ => s.Created.Last().Sent.Count == 2);
                Check(s.Created.Last().Sent.Last() == "/unknown", "literal slash escape");
                Type("/bad-command"); Key(ConsoleKey.Enter); Check(s.Created.Last().Sent.Count == 2, "unknown workbench command not sent");
                Key(ConsoleKey.C, true); Type("/tools"); Key(ConsoleKey.Enter); Check(ui.View().ExpandTools, "tool fold command reaches view");
                Type("/thoughts"); Key(ConsoleKey.Enter); Check(!ui.View().ShowThinking, "reasoning visibility toggles");
                Type("/find model=second"); Key(ConsoleKey.Enter); Check(ui.View().Overlay is not null, "search opens picker"); Key(ConsoleKey.Escape);
            }
            finally { cts.Cancel(); await ui.FinishAsync(); }
            Check(s.Created.All(x => x.Disposals == 1), "all acquired sessions disposed exactly once");
            await ApprovalOverlapAsync(project, data);
        }
        finally
        {
            // Git marks loose objects read-only on Windows. Clear those flags only inside this fixture.
            var objects = Path.Combine(root.FullName, "project", ".git", "objects");
            if (OperatingSystem.IsWindows() && Directory.Exists(objects))
                foreach (var obj in new DirectoryInfo(objects).EnumerateFiles("*", SearchOption.AllDirectories))
                    obj.IsReadOnly = false;
            root.Delete(true);
        }
    }

    private static void Check(bool passed, string label) { if (!passed) throw new InvalidDataException(label); }
    private static async Task CheckGitPreviewAsync(string project, ProjectFiles files)
    {
        async Task Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var proc = Process.Start(start) ?? throw new IOException("git did not start");
            await proc.WaitForExitAsync();
            Check(proc.ExitCode == 0, "Git fixture " + args[0]);
        }
        const string file = "例 [1].txt";
        var full = Path.Combine(project, file);
        await Git("init", "-q");
        await File.WriteAllTextAsync(full, "tracked\n");
        await Git("--literal-pathspecs", "add", "--", file);
        await Git("-c", "user.name=FUI", "-c", "user.email=fui@example.invalid", "-c", "commit.gpgsign=false",
            "-c", "core.hooksPath=.no-hooks", "commit", "-qm", "fixture");
        await File.WriteAllTextAsync(full, "changed\n");
        // Use another matching path to exercise literal pathspecs as well as UTF-8 attribute output.
        await File.WriteAllTextAsync(Path.Combine(project, "例 1.txt"), "other\n");
        Check((await files.DiffAsync(file, default)).Contains("+changed"), "tracked literal file diff");
        await File.WriteAllTextAsync(Path.Combine(project, ".gitattributes"), "*.txt filter=fixture\n");
        await Git("config", "--local", "filter.fixture.required", "true");
        await Git("config", "--local", "filter.fixture.clean", "exit 1");
        try { await files.DiffAsync(file, default); throw new InvalidDataException("filtered diff was offered"); }
        catch (IOException e) when (e.Message.Contains("Git 过滤器", StringComparison.Ordinal)) { }
    }
    private static async Task Reject<T>(Func<Task> f) where T : Exception
    { try { await f(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class Session : ITuiSession, IFuiWorkbenchSession
    {
        public Session(string project, string id, List<Session>? created = null) { Project = project; Id = id; Created = created ?? []; Created.Add(this); }
        public string Project { get; }
        public string Id { get; }
        public string? Model => "fake";
        public List<Session> Created { get; }
        public List<string> Sent { get; } = [];
        public int Disposals { get; private set; }
        public IReadOnlyList<TuiLine> History => [];
        private readonly Channel<TuiEvent> _events = Channel.CreateUnbounded<TuiEvent>();
        public ChannelReader<TuiEvent> Events => _events.Reader;
        public void Emit(TuiEvent item) => _events.Writer.TryWrite(item);
        public Func<CancellationToken, Task<IReadOnlyList<FuiChoice>>>? ReadChoices { get; set; }
        public Func<Task<FuiActionResult>>? Action { get; set; }
        public bool RejectSend { get; set; }
        public Task SendAsync(string text, CancellationToken ct)
        {
            if (RejectSend) return Task.FromException(new InvalidOperationException("测试发送拒绝"));
            Sent.Add(text);
            Emit(new TuiEvent.Status("已完成", false));
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id, bool allow, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<FuiChoice>> ChoicesAsync(string action, CancellationToken ct) => ReadChoices?.Invoke(ct) ?? Task.FromResult<IReadOnlyList<FuiChoice>>([new("one", "one"), new("second", "second")]);
        public Task<FuiActionResult> ExecuteAsync(string action, string argument, CancellationToken ct) => Action?.Invoke() ?? Task.FromResult(action switch
        { "new" => new(Replacement: new Session(Project, "child", Created)), "resume" => new(Replacement: new Session(Project, argument, Created)), _ => new FuiActionResult(action + "=" + argument) });
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
