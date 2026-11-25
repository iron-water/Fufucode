using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using static Fufucode.Fui.Tests.RuntimeTests;

namespace Fufucode.Fui.Tests;

internal static class WorkbenchSessionTests
{
    public static async Task RunAsync()
    {
        var root = Environment.GetEnvironmentVariable("FUFUCODE_TEST_ROOT") is { Length: > 0 } fixtures
            ? Directory.CreateDirectory(Path.Combine(fixtures, "workbench-" + Guid.NewGuid().ToString("N")))
            : Directory.CreateTempSubdirectory("fui-workbench-");
        var layout = new AppDataLayout(Path.Combine(root.FullName, "data"));
        var originalRoot = Environment.GetEnvironmentVariable(AppDataLayout.RootEnvironmentVariable);
        var journal = FileChangeJournal.Root;
        var env = "FUI_BENCH_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(AppDataLayout.RootEnvironmentVariable, layout.Root);
        Environment.SetEnvironmentVariable(env, "loopback-fixture");
        var active = new List<ITuiSession>();
        var plugin = new CountingPlugin();
        PluginPackageManager.RegisterPackageFactory(plugin.Id, () => plugin);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var ct = timeout.Token;
            var project = root.CreateSubdirectory("work").FullName;
            var other = root.CreateSubdirectory("other").FullName;
            var skillRoot = Directory.CreateDirectory(Path.Combine(project, ".fumo", "skills", "fixture"));
            File.WriteAllText(Path.Combine(skillRoot.FullName, "SKILL.md"), "---\nname: fui-fixture\ndescription: local demonstration skill\n---\nReturn the fixture label.");
            var payload = Directory.CreateDirectory(layout.PluginDirectory(plugin.Id));
            File.WriteAllText(Path.Combine(payload.FullName, "manifest.json"), $$"""{"id":"{{plugin.Id}}"}""");
            await using var api = new Loopback
            {
                Models = """{"data":[{"id":"fixture-model","context_length":128000},{"id":"vendor/fui-model","display_name":"Fixture Pro","context_length":128000,"supported_reasoning_levels":[{"effort":"high"}]}]}"""
            };
            new AppSettingsStore(layout).Update(s => s with { Endpoint = new("fixture", api.Url, "fixture-model", env) });
            var metadata = new DesktopMetadataStore(layout);
            var owner = metadata.GetOrCreateProject(project);
            var foreign = metadata.CreateSession(metadata.GetOrCreateProject(other));
            var external = metadata.CreateSession(owner, backend: BackendId.Omp);
            async Task<FumoTuiSession> Open(string? session = null)
            {
                var opened = await FumoTuiSession.OpenAsync(project, layout.Root, session, false, ct);
                active.Add(opened);
                return opened;
            }
            async Task<FumoTuiSession> Replace(FumoTuiSession origin, string action, string value)
            {
                var result = await origin.ExecuteAsync(action, value, ct);
                var replacement = result.Replacement as FumoTuiSession ?? throw new InvalidOperationException("action failed to return a native session");
                active.Add(replacement);
                return replacement;
            }

            var parent = await Open();
            await parent.ExecuteAsync("rename", "我的本地讨论", ct);
            Check(metadata.GetSession(parent.Id).Name == "我的本地讨论", "rename not persisted");
            var list = await parent.ChoicesAsync("resume", ct);
            Check(list.Count == 1 && list[0] is { Label: "我的本地讨论" } && list[0].Value == parent.Id && list[0].Detail!.Contains("当前"), "list leaked foreign/external session or lost name");
            var rootChoice = (await parent.ChoicesAsync("tree", ct)).Single();
            Check(rootChoice.Value == parent.Id && rootChoice.ParentSessionId is null, "tree leaked foreign/other-backend sessions or invented a parent");
            var modes = await parent.ChoicesAsync("mode", ct);
            Check(modes.Select(choice => choice.Value).ToHashSet().SetEquals(["manual", "auto", "full", "plan"]),
                "execution mode catalogue is incomplete");
            await parent.ExecuteAsync("mode", "plan", ct);
            Check(parent.ExecutionMode == "plan", "plan mode was not applied to the native session");
            Check((await parent.ChoicesAsync("capabilities", ct)).All(choice => choice.Value != "write_file"),
                "plan mode picker still advertised mutating capabilities");
            await parent.ExecuteAsync("mode", "manual", ct);
            Check(parent.ExecutionMode == "manual", "manual mode was not restored");
            await Reject<InvalidOperationException>(() => parent.ExecuteAsync("tree", foreign.Id, ct));
            await Reject<InvalidOperationException>(() => parent.ExecuteAsync("tree", external.Id, ct));
            await Reject<KeyNotFoundException>(() => parent.ExecuteAsync("tree", "absent-id", ct));
            Check((await parent.ExecuteAsync("tree", parent.Id, ct)).Replacement is null, "tree reacquired the current lease");
            await Reject<InvalidOperationException>(() => parent.ExecuteAsync("resume", foreign.Id, ct));
            await Reject<InvalidOperationException>(() => parent.ExecuteAsync("resume", external.Id, ct));
            Check((await parent.ExecuteAsync("resume", parent.Id, ct)).Replacement == null, "self-switch tried to get a second lease");
            Check((await parent.ChoicesAsync("skills", ct)).Any(c => c.Value == "skill:fui-fixture" && c.Detail!.Contains("demonstration")), "discovered skill missing");
            Check((await parent.ChoicesAsync("capabilities", ct)).Any(c => c.Value == "read_file" && c.Detail!.StartsWith("native")), "native tool missing");
            var models = await parent.ChoicesAsync("model", ct);
            Check(models.Any(c => c is { Value: "vendor/fui-model", Label: "Fixture Pro" }), "provider-prefixed catalogue id lost");
            await Reject<ArgumentException>(() => parent.ExecuteAsync("model", "invented", ct));
            await parent.ExecuteAsync("model", "vendor/fui-model", ct);
            var thinking = await parent.ChoicesAsync("thinking", ct);
            Check(thinking.Select(c => c.Value).ToHashSet().SetEquals(["off", "high"]), "unsupported provider levels listed");
            await Reject<ArgumentException>(() => parent.ExecuteAsync("thinking", "max", ct));
            await parent.ExecuteAsync("thinking", "high", ct);
            Check(new AppSettingsStore(layout).Current.Endpoint!.ModelId == "fixture-model", "session change mutated global default");
            Check((await parent.ExecuteAsync("usage", "", ct)).Notice!.Contains("输入 未提供"), "empty history fabricated usage");
            await Reject<InvalidOperationException>(() => parent.ExecuteAsync("queue", "follow", ct));

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            api.When("first", false, Frame(new { content = "live" }), release.Task, Frame(new { content = " final" }, "stop") +
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":17,\"completion_tokens\":4,\"total_tokens\":21}}\n\n");
            api.When("steered", false, Frame(new { content = "steering received" }, "stop"));
            api.When("follow", false, Frame(new { content = "follow received" }, "stop"));
            await parent.SendAsync("first", ct);
            await Until(parent, e => e is TuiEvent.Message { Kind: "assistant", Text: "live", Append: true }, ct);
            Check((await parent.ChoicesAsync("resume", ct)).Single().Detail!.Contains("运行中"), "live session not shown active");
            Check((await parent.ChoicesAsync("tree", ct)).Single().Detail!.Contains("运行中"), "tree cannot be browsed during a real turn");
            await Reject<InvalidOperationException>(() => parent.ExecuteAsync("tree", parent.Id, ct));
            var midTurn = (await parent.ExecuteAsync("usage", "", ct)).Notice!;
            Check(midTurn.Contains("运行中") && midTurn.Contains("输入 未提供"), "in-flight tokens fabricated or not labelled");
            await Reject<InvalidOperationException>(() => parent.ExecuteAsync("new", "", ct));
            await Reject<InvalidOperationException>(() => parent.ExecuteAsync("model", "fixture-model", ct));
            await parent.ExecuteAsync("queue", "follow", ct);
            await parent.ExecuteAsync("steer", "steered", ct);
            var pending = (await parent.ExecuteAsync("queue", "", ct)).Notice!;
            Check(pending.Contains("插话：steered") && pending.Contains("排队：follow"), "native pending queue not reflected");
            release.SetResult();
            var ended = await Settled(parent, ct);
            Check(ended.OfType<TuiEvent.Message>().Any(m => m.Kind == "assistant" && m.Text == "follow received"), "turn reported idle before queued execution");
            var requests = api.Choices.ToArray();
            Check(requests.Select(r => r.Prompt).SequenceEqual(["first", "steered", "follow"]) && requests.All(r => r.Model == "vendor/fui-model" && r.Effort == "high"), "wire model, effort or delivery order incorrect");
            var report = (await parent.ExecuteAsync("usage", "", ct)).Notice!;
            Check(report.Contains("已报告 17") && report.Contains("已报告 4") && report.Contains("已报告 21") && report.Contains("2 条未报告"), "partial usage fabricated or omitted");

            var path = (await parent.ExecuteAsync("export", "", ct)).Notice!;
            Check(Path.GetDirectoryName(path) == layout.ExportsDirectory && File.ReadAllText(path).Contains("follow received"), "default export missing transcript or used wrong directory");
            var named = (await parent.ExecuteAsync("export", "explicit name.md", ct)).Notice!;
            Check(named == Path.Combine(project, "explicit name.md"), "relative export not anchored to current project");
            var digest = SHA256.HashData(File.ReadAllBytes(named));
            await Reject<IOException>(() => parent.ExecuteAsync("export", "explicit name.md", ct));
            Check(digest.SequenceEqual(SHA256.HashData(File.ReadAllBytes(named))), "existing export overwritten");

            var forks = await parent.ChoicesAsync("fork", ct);
            var entry = forks.Single(c => c.Label == "first");
            Check(entry.Value.StartsWith("native_") && forks.All(f => f.Label is "first" or "follow" or "steered"), "fork picker did not use durable user entry IDs");
            await Reject<ArgumentOutOfRangeException>(() => parent.ExecuteAsync("fork", "native_missing", ct));
            var originalFile = metadata.GetSession(parent.Id).FilePath!;
            var original = SHA256.HashData(File.ReadAllBytes(originalFile));
            var branch = await Replace(parent, "fork", entry.Value);
            Check(branch.Id != parent.Id && metadata.GetSession(branch.Id).ParentSessionId == parent.Id && branch.History.Single() == new TuiLine("user", "first"), "fork lost lineage or copied future history");
            Check(branch.Model == "vendor/fui-model" && (await branch.ChoicesAsync("thinking", ct)).Single(c => c.Detail == "当前").Value == "high", "fork lost session settings");
            Check(original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(originalFile))), "fork changed parent file");
            await Reject<InvalidOperationException>(() => branch.ExecuteAsync("resume", parent.Id, ct));
            var sibling = await Replace(parent, "fork", entry.Value);
            await sibling.DisposeAsync();
            await parent.DisposeAsync();
            Check(plugin.Starts == 1 && plugin.Stops == 0, "old session shut down or replaced shared project plugins");
            api.When("child only", false, Frame(new { content = "inside child" }, "stop"));
            while (branch.Events.TryRead(out var restored))
                Check(restored is TuiEvent.Status { Text: "上次运行已中断", Busy: false }, "unexpected event before sending on branch");
            await branch.SendAsync("child only", ct);
            await Until(branch, e => e is TuiEvent.Status { Busy: true }, ct);
            await Settled(branch, ct);
            Check(original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(originalFile))), "child mutated parent during its turn");
            var childEntry = (await branch.ChoicesAsync("fork", ct)).Single(c => c.Label == "child only");
            var grandchild = await Replace(branch, "fork", childEntry.Value);
            var family = (await grandchild.ChoicesAsync("tree", ct)).ToDictionary(c => c.Value);
            Check(family.Count == 4 && family[parent.Id].ParentSessionId is null &&
                family[branch.Id].ParentSessionId == parent.Id && family[sibling.Id].ParentSessionId == parent.Id &&
                family[grandchild.Id].ParentSessionId == branch.Id, "persisted native fork relationships or project scope lost");
            await NavigateNativeTreeAsync(grandchild, branch.Id, parent.Id, sibling.Id, layout.Root, ct);
            var reopened = await Replace(branch, "tree", grandchild.Id);
            Check(reopened.Id == grandchild.Id && reopened.History.Any(l => l == new TuiLine("user", "child only")),
                "switch did not release the prior lease or reopened the wrong transcript");
            await reopened.DisposeAsync();
            var again = await Replace(branch, "resume", parent.Id);
            await branch.DisposeAsync();
            Check(again.History.Any(l => l.Text == "follow received") && again.History.All(l => l.Text != "inside child"), "resume returned unrelated history");

            api.Tool("cancel", "never-created.txt");
            await again.SendAsync("cancel", ct);
            await Until(again, e => e is TuiEvent.Approval, ct);
            await again.ExecuteAsync("queue", "cancelled-follow", ct);
            await again.StopAsync(ct);
            await Settled(again, ct);
            Check((await again.ExecuteAsync("queue", "", ct)).Notice!.Contains("已取消：cancelled-follow") && !File.Exists(Path.Combine(project, "never-created.txt")), "stopped queue disappeared or unanswered write ran");
            var fresh = await Replace(again, "new", "");
            await again.DisposeAsync();
            Check(fresh.Model == "fixture-model" && fresh.History.Count == 0 && plugin.Starts == 1 && plugin.Stops == 0, "fresh session inherited history/model or duplicated plugin host");
            await fresh.DisposeAsync();
            Check(plugin.Stops == 1, "last session did not stop the plugin exactly once");

            // A persisted read-only parent is never promoted to manual by native fork.
            var stored = JsonNode.Parse(File.ReadAllText(originalFile))!;
            stored["executionMode"] = "plan";
            File.WriteAllText(originalFile, stored.ToJsonString());
            var plan = await Open(parent.Id);
            var planBranch = await Replace(plan, "fork", entry.Value);
            Check(JsonNode.Parse(File.ReadAllText(metadata.GetSession(planBranch.Id).FilePath!))!["executionMode"]!.ToString() == "plan" &&
                (await planBranch.ChoicesAsync("capabilities", ct)).All(c => c.Value != "write_file"), "read-only fork gained write authority");
        }
        finally
        {
            foreach (var session in active) await session.DisposeAsync();
            PluginPackageManager.UnregisterPackageFactory(plugin.Id);
            Environment.SetEnvironmentVariable(AppDataLayout.RootEnvironmentVariable, originalRoot);
            Environment.SetEnvironmentVariable(env, null);
            FileChangeJournal.Configure(Path.GetDirectoryName(journal)!);
            root.Delete(recursive: true);
        }
    }

    private static async Task NavigateNativeTreeAsync(FumoTuiSession grandchild, string branch, string root,
        string sibling, string dataRoot, CancellationToken ct)
    {
        var ui = new TuiController(grandchild, ct, dataRoot);
        void Type(string text) { foreach (var c in text) ui.Key(new(c, (ConsoleKey)0, false, false, false), 12, 20); }
        void Press(ConsoleKey key) => ui.Key(new('\0', key, false, false, false), 12, 20);
        async Task WaitFor(Func<TuiView, bool> predicate)
        {
            while (true)
            {
                ui.Update();
                if (predicate(ui.View())) return;
                await Task.Delay(5, ct);
            }
        }
        async Task OpenTree()
        {
            Type("/tree"); Press(ConsoleKey.Enter);
            await WaitFor(v => v.Overlay?.Title == "会话分支树");
        }
        string Selected() { var panel = ui.View().Overlay!; return panel.Items[panel.Selected].Value; }
        try
        {
            await OpenTree();
            Check(Selected() == grandchild.Id && ui.View().Overlay!.Items.Count == 4, "native tree did not reveal its current grandchild and siblings");
            Press(ConsoleKey.LeftArrow);
            Check(Selected() == branch, "native tree left did not target the correct parent");
            Press(ConsoleKey.Enter);
            await WaitFor(v => v.Lines.Any(l => l.Kind == "error"));
            Check(ui.View().Session == grandchild.Id, "selecting a leased session abandoned the starting session");

            await OpenTree();
            Press(ConsoleKey.LeftArrow); Press(ConsoleKey.LeftArrow);
            Press(ConsoleKey.LeftArrow); Press(ConsoleKey.LeftArrow);
            Check(ui.View().Overlay!.Items.Single().Value == root, "collapsing the root left hidden descendants selectable");
            Type(sibling);
            Check(Selected() == sibling && ui.View().Overlay!.Items.Count == 1, "full ID did not find a folded sibling");
            Press(ConsoleKey.Enter);
            await WaitFor(v => v.Session == sibling);
            Check(ui.View().Lines.Contains(new TuiLine("user", "first")) && ui.View().Lines.All(l => l.Text != "child only"),
                "Enter resumed the wrong same-name branch or transcript");
        }
        finally { await ui.FinishAsync(); }
    }

    private sealed class CountingPlugin : IPluginPackage
    {
        public string Id => "chat.interactive-ui";
        public string Version => "1.0.0";
        public int Starts, Stops;
        public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;
        public Task InitializeAsync(PluginPackageContext context, CancellationToken ct = default) { Starts++; return Task.CompletedTask; }
        public Task ShutdownAsync(CancellationToken ct = default) { Stops++; return Task.CompletedTask; }
    }
}
