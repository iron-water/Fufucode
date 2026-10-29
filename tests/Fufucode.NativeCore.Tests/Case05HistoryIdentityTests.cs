using System.Text.Json;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

/// <summary>
/// Case 05: stable message identities survive compaction/reorder/JSON round-trips;
/// native positional cursors are rejected and forks bind to the current stable identity.
/// Exercises the real Core objects; only the scratch data root is isolated.
/// </summary>
internal static class Case05HistoryIdentityTests
{
    private static PureAgentMessage M(PureAgentRole role, string content, string entryId) =>
        new(role, content) { EntryId = entryId };

    private static void WriteNative(string file, IReadOnlyList<PureAgentMessage> messages)
    {
        AtomicJsonFile.Write(file, writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 2);
            writer.WritePropertyName("messages");
            JsonSerializer.Serialize(writer, messages);
            writer.WriteNull("modelId");
            writer.WriteString("thinkingLevel", "off");
            writer.WriteString("executionMode", "manual");
            writer.WriteEndObject();
        });
    }

    public static async Task RunAsync(Checks checks)
    {
        var scratch = TestTemp.Create("05");
        Directory.CreateDirectory(scratch);
        try
        {
            var e1 = M(PureAgentRole.User, "u1", "e1");
            var e2 = M(PureAgentRole.Assistant, "a2", "e2");
            var e3 = M(PureAgentRole.User, "u3", "e3");
            var e4 = M(PureAgentRole.Assistant, "a4", "e4");
            var e5 = M(PureAgentRole.User, "u5", "e5");
            var all = new[] { e1, e2, e3, e4, e5 };

            var built = InternalAccess.NativeEntriesBuild(all, stableIds: true);
            var ids = built.Select(entry => entry.GetProperty("id").GetString()).ToArray();
            checks.Check(ids.SequenceEqual(["native_e1", "native_e2", "native_e3", "native_e4", "native_e5"]),
                "stable ids survive mapping and carry persisted EntryId");
            checks.Equal(2, InternalAccess.NativeEntriesResolveIndex(all, "native_e3"), "stable id resolves to its message");
            checks.Equal(-1, InternalAccess.NativeEntriesResolveIndex(all, "entry_3"),
                "legacy positional id is rejected instead of selecting a message");
            checks.Equal(-1, InternalAccess.NativeEntriesResolveIndex(all, "native_missing"), "unknown stable id is rejected");
            checks.Equal(-1,
                InternalAccess.NativeEntriesResolveIndex(new[] { M(PureAgentRole.User, "x", "dup"), M(PureAgentRole.User, "y", "dup") },
                    "native_dup"),
                "duplicate persisted identity is rejected");

            var engine = new PureCSharpAgentEngine();
            engine.RestoreHistory(all);
            var chat = new NativeCSharpChatSession(engine, "entries", "test");
            var coordinator = new NativeSessionCoordinator(chat);
            var entriesResponse = await coordinator.QueryEntriesAsync("entry_2");
            var entriesData = entriesResponse.Data!.Value;
            checks.Check(entriesResponse.Success &&
                         entriesData.GetProperty("entries").GetArrayLength() == 5,
                "native get_entries ignores positional cursor and returns the full snapshot");
            checks.Equal("native_e5", entriesData.GetProperty("leafId").GetString(),
                "native leaf uses stable identity");
            await coordinator.DisposeAsync();

            var emptyEngine = new PureCSharpAgentEngine();
            var emptyChat = new NativeCSharpChatSession(emptyEngine, "empty", "test");
            var emptyCoordinator = new NativeSessionCoordinator(emptyChat);
            var empty = await emptyCoordinator.QueryEntriesAsync();
            checks.Check(empty.Success &&
                         empty.Data!.Value.GetProperty("entries").GetArrayLength() == 0 &&
                         empty.Data!.Value.GetProperty("leafId").ValueKind == JsonValueKind.Null,
                "empty native history returns no entries and null leaf");
            await emptyCoordinator.DisposeAsync();

            var layout = new AppDataLayout(Path.Combine(scratch, "data"));
            var manager = new PersistentSessionManager(string.Empty, string.Empty, layout, requireApprovalBridge: false);
            var projectPath = Path.Combine(scratch, "project");
            Directory.CreateDirectory(projectPath);
            manager.SetProjectTrustDecision(projectPath, ProjectTrustDecision.Trust);
            var project = manager.Metadata.GetOrCreateProject(projectPath);
            var record = manager.Metadata.CreateSession(project, "fix15", BackendId.NativeCSharp);
            var file = NativeSessionFactory.SessionFilePath(layout, project.Id, record.Id);
            WriteNative(file, all);

            await using var session = await manager.ResumeAsync(record.Id);
            checks.Check(session.History.Entries.Count == 5 && session.History.Cursor == "native_e5",
                "resume loads persisted history under stable identities");

            var nativeChat = (NativeCSharpChatSession)session.Chat;
            nativeChat.RestoreHistory([e4, e5]);
            await session.RefreshHistoryAsync();
            checks.Check(session.History.CurrentBranchMessages.Count == 2 &&
                         session.History.Cursor == "native_e5",
                "refresh after compaction replaces the cached history instead of appending a slice");

            nativeChat.RestoreHistory([e4, e5, M(PureAgentRole.User, "u6", "e6"),
                M(PureAgentRole.Assistant, "a7", "e7")]);
            await session.RefreshHistoryAsync();
            checks.Check(session.History.CurrentBranchMessages.Count == 4 &&
                         session.History.Cursor == "native_e7",
                "refresh after regrowth appends new entries once while retaining stable ids");
            checks.Check(
                session.History.CurrentBranchMessages[0].Content.OfType<PiTextBlock>().Single().Text == "a4" &&
                session.History.CurrentBranchMessages[1].Content.OfType<PiTextBlock>().Single().Text == "u5",
                "regrowth does not remap survivors to different messages");

            nativeChat.RestoreHistory([e5, e4, e3]);
            await session.RefreshHistoryAsync();
            checks.Check(session.History.CurrentBranchMessages.Count == 3 &&
                         session.History.Cursor == "native_e3" &&
                         session.History.CurrentBranchMessages[0].Content.OfType<PiTextBlock>().Single().Text == "u5",
                "refresh after reorder replaces order and never appends stale tails");
            checks.Equal(0, InternalAccess.NativeEntriesResolveIndex([e5, e4, e3], "native_e5"),
                "reorder keeps the original identity on the moved message");

            await checks.ThrowsAsync<ArgumentOutOfRangeException>(
                () => manager.ForkAsync(session, "native_e1"),
                "fork rejects an id removed by compaction");
            await checks.ThrowsAsync<ArgumentOutOfRangeException>(
                () => manager.ForkAsync(session, "entry_1"),
                "fork rejects a legacy positional id even when that index exists");

            var fork = await manager.ForkAsync(session, "native_e3");
            await using var child = fork.Session;
            checks.Check(child.History.CurrentBranchMessages.Count == 3 &&
                         child.History.Cursor == "native_e3",
                "fork at a current stable id copies exactly the branch up to that message");
            checks.Check(child.Session.ParentSessionId == session.Session.Id &&
                         child.Session.ForkEntryId == "native_e3",
                "fork records parent and stable fork entry identity");

            var history = new SessionHistory();
            history.ApplyEntries(InternalAccess.NativeEntriesBuild(all, true), InternalAccess.NativeEntriesLeaf(all, true), replace: true);
            history.ApplyEntries(InternalAccess.NativeEntriesBuild([e4, e5], true), InternalAccess.NativeEntriesLeaf([e4, e5], true),
                replace: true);
            checks.Check(history.Entries.Count == 2 && history.Cursor == "native_e5",
                "replace path shrinks history to the compacted set");
        }
        finally
        {
            TestTemp.Delete(scratch);
        }
    }
}
