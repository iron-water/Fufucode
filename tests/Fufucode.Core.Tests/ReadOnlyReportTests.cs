using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Fufucode.Core;
using Fufucode.Pi;

internal static class ReadOnlyReportTests
{
    public static async Task RunAsync(string root)
    {
        // Outside the source worktree: Git discovery would otherwise inherit its ancestor repository.
        var stage = Path.Combine(Path.GetTempPath(), "fufucode-a8-contract-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(stage, "project space"); Directory.CreateDirectory(project);
        var records = new List<object>();
        async Task<(int Code, JsonElement Json, string Error)> Run(string[] command, string? session = null, string? work = null, string? data = null)
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            var savedOut = Console.Out; var savedError = Console.Error;
            var args = new List<string> { "--project", work ?? project, "--app-data", data ?? Path.Combine(stage, "data"), "--json" };
            if (session is not null) { args.Add("--session"); args.Add(session); }
            args.AddRange(command); int code;
            try { Console.SetOut(output); Console.SetError(error); code = await Fufucode.Cli.Program.Main(args.ToArray()); }
            finally { Console.SetOut(savedOut); Console.SetError(savedError); }
            var text = output.ToString();
            records.Add(new { command = args, exitCode = code, stdout = text, stderr = error.ToString() });
            return (code, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(), error.ToString());
        }
        static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); }
        async Task Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-C", project }.Concat(args)) start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync(); await output;
            if (child.ExitCode != 0) throw new Exception(await error);
            await error;
        }
        static byte[] Fingerprint(string path) => SHA256.HashData(File.ReadAllBytes(path));
        try
        {
            var absent = await Run(["usage", "show"]);
            Check(absent.Code == 0 && absent.Json.GetProperty("report").GetProperty("session").GetProperty("totalTokens").GetProperty("value").ValueKind == JsonValueKind.Null &&
                !Directory.Exists(Path.Combine(stage, "data")), "A8 empty read-only usage preserves unknown and creates no metadata");
            var nongit = await Run(["changes", "list"]);
            Check(nongit.Code == 3 && !nongit.Json.GetProperty("report").GetProperty("available").GetBoolean(),
                "A8 no Git repository is unavailable, not a successful empty diff");
            // New isolated fixture only: no commit/merge/push, never initialize the source repository.
            await Git("init", "--quiet");
            var empty = await Run(["changes", "list"]);
            Check(empty.Code == 0 && empty.Json.GetProperty("report").GetProperty("files").GetArrayLength() == 0,
                "A8 empty Git worktree remains distinguishable from unavailable");
            var textName = "new file 中文.txt";
            await File.WriteAllTextAsync(Path.Combine(project, textName), "written");
            await File.WriteAllBytesAsync(Path.Combine(project, "deleted.bin"), [1, 0, 2]);
            await Git("add", "--", "deleted.bin");
            File.Delete(Path.Combine(project, "deleted.bin"));
            await File.WriteAllBytesAsync(Path.Combine(project, "new.bin"), [3, 0, 4]);
            var index = Path.Combine(project, ".git", "index"); var beforeIndex = Fingerprint(index);
            var workspace = await Run(["changes", "list"]);
            var files = workspace.Json.GetProperty("report").GetProperty("files").EnumerateArray().ToArray();
            Check(workspace.Code == 0 && files.Any(f => f.GetProperty("path").GetString() == textName && f.GetProperty("isNew").GetBoolean() && !f.GetProperty("isBinary").GetBoolean()) &&
                files.Any(f => f.GetProperty("path").GetString() == "new.bin" && f.GetProperty("isBinary").GetBoolean()) &&
                files.Any(f => f.GetProperty("path").GetString() == "deleted.bin" && f.GetProperty("isDeleted").GetBoolean() && f.GetProperty("isBinary").GetBoolean()) &&
                beforeIndex.SequenceEqual(Fingerprint(index)), "A8 Git ranges preserve Unicode/new/deleted/binary flags without modifying index");

            var layout = new AppDataLayout(Path.Combine(stage, "data")); var metadata = new DesktopMetadataStore(layout);
            var registeredProject = metadata.GetOrCreateProject(project); var session = metadata.CreateSession(registeredProject);
            var historyFile = Path.Combine(layout.ProjectSessionsDirectory(registeredProject.Id), session.Id + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(historyFile)!);
            metadata.UpdateSessionFile(session.Id, historyFile);
            object Tool(string id, string path) => new { type = "toolCall", id, name = "write", arguments = new { path, content = "not-exported" } };
            var history = new object[]
            {
                new { type = "session", version = 3, id = "fixture" },
                new { type = "message", id = "u1", parentId = (string?)null, message = new { role = "user", content = new[] { new { type = "text", text = "seed" } } } },
                new { type = "message", id = "a1", parentId = "u1", message = new { role = "assistant", content = new[] { Tool("ok", textName), Tool("deny", "denied.txt"), Tool("pending", "pending.txt"), Tool("escape", "../outside.txt") }, stopReason = "toolUse", usage = new { input = 10, output = 2, cacheRead = 0, cacheWrite = 0, totalTokens = 12 } } },
                new { type = "message", id = "r1", parentId = "a1", message = new { role = "toolResult", toolCallId = "ok", isError = false, content = Array.Empty<object>() } },
                new { type = "message", id = "r2", parentId = "r1", message = new { role = "toolResult", toolCallId = "deny", isError = true, content = Array.Empty<object>() } },
                new { type = "message", id = "r3", parentId = "r2", message = new { role = "toolResult", toolCallId = "escape", isError = false, content = Array.Empty<object>() } },
                new { type = "message", id = "a2", parentId = "r3", message = new { role = "assistant", content = Array.Empty<object>(), stopReason = "stop" } },
                new { type = "message", id = "u2", parentId = "a2", message = new { role = "user", content = Array.Empty<object>() } },
                new { type = "message", id = "a3", parentId = "u2", message = new { role = "assistant", content = Array.Empty<object>(), stopReason = "stop", usage = new { input = 4, output = 2, cacheRead = 0, cacheWrite = 0, totalTokens = 6 } } },
            };
            await File.WriteAllTextAsync(historyFile, string.Join('\n', history.Select(item => JsonSerializer.Serialize(item))) + "\n");
            var beforeDb = Fingerprint(layout.DatabasePath); var beforeHistory = Fingerprint(historyFile);
            var usage = await Run(["usage", "show"], session.Id);
            var total = usage.Json.GetProperty("report").GetProperty("session").GetProperty("totalTokens");
            Check(usage.Code == 0 && total.GetProperty("value").ValueKind == JsonValueKind.Null && total.GetProperty("knownSubtotal").GetInt64() == 18 &&
                total.GetProperty("unknownMessageCount").GetInt32() == 1 && usage.Json.GetProperty("report").GetProperty("turns").GetArrayLength() == 2,
                "A8 CLI usage exposes unknown session totals and known per-turn contributions");
            var writes = await Run(["changes", "list", "--scope", "session"], session.Id);
            Check(writes.Code == 0 && writes.Json.GetProperty("report").GetProperty("files").GetArrayLength() == 1 &&
                writes.Json.GetProperty("report").GetProperty("files")[0].GetProperty("path").GetString() == textName &&
                writes.Json.GetProperty("report").GetProperty("excludedToolPaths").GetInt32() == 1 &&
                !writes.Json.GetRawText().Contains("not-exported"), "A8 session-write ranges include successful writes only and exclude denied/pending/outside paths");
            Check(beforeDb.SequenceEqual(Fingerprint(layout.DatabasePath)) && beforeHistory.SequenceEqual(Fingerprint(historyFile)) && beforeIndex.SequenceEqual(Fingerprint(index)),
                "A8 CLI report queries change no metadata, transcript, or Git index");
            CliUiTests.CheckWave5ReportDtos(usage.Json, writes.Json);
            var loaded = ReadOnlySessionReport.Load(layout, project, session.Id);
            var live = await ChangeRangeQuery.SessionWritesAsync(project, loaded.History.CurrentBranchMessages,
                [new PiToolCard("live-ok", "edit", JsonSerializer.SerializeToElement(new { path = textName }), null, true, false),
                 new PiToolCard("live-pending", "write", JsonSerializer.SerializeToElement(new { path = "pending.txt" }), null, false, false)]);
            Check(live.Files.Count == 1 && live.Files[0].ToolCallIds.Contains("live-ok"), "A8 live tool cards require final successful execution evidence");
            var reused = loaded.History.CurrentBranchMessages.Concat([
                new PiMessage("assistant", [new PiToolCallBlock("ok", "write", JsonSerializer.SerializeToElement(new { path = "denied-reused.txt" }), default)], null, null, null, default),
                new PiMessage("toolResult", [], null, null, null, JsonSerializer.SerializeToElement(new { toolCallId = "ok", isError = true }))]);
            var stable = await ChangeRangeQuery.SessionWritesAsync(project, reused);
            Check(stable.Files.Count == 1 && stable.Files[0].Path == textName, "A8 reused call IDs do not turn a later failed write into a successful path");
            var repeated = await Run(["usage", "show"], session.Id);
            Check(repeated.Json.GetProperty("report").GetRawText() == usage.Json.GetProperty("report").GetRawText(),
                "A8 restored history queries do not double-count usage");
            await File.AppendAllTextAsync(historyFile, "{\"type\":");
            var tail = await Run(["usage", "show"], session.Id);
            Check(tail.Code == 0 && tail.Json.GetProperty("partialTail").GetBoolean(), "A8 incomplete live JSONL tail is explicitly marked");
            var other = Path.Combine(stage, "other"); Directory.CreateDirectory(other);
            var wrong = await Run(["usage", "show"], session.Id, other);
            Check(wrong.Code == 3 && wrong.Error.Contains("not registered"), "A8 offline reports enforce registered project ownership");
        }
        finally { await File.WriteAllTextAsync(Path.Combine(stage, "report.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine("A8 artifacts: " + stage); }
    }
}
