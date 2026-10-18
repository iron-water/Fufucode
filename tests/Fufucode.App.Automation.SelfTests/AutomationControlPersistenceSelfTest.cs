using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Features.Automation;
using Fufucode.App.Features.Automation.Persistence;
using Fufucode.Core.Automation;
using Microsoft.Data.Sqlite;

namespace Fufucode.App.SelfTest;

internal static partial class AutomationControlSelfTest
{
    private static async Task RunPersistenceAsync(string root, Action<bool, string> check)
    {
        var area = Path.Combine(root, "native-persistence"); Directory.CreateDirectory(area);
        var db = Path.Combine(area, "authority.db"); var data = Path.Combine(area, "data");
        using var authority = new AutomationStore(db);
        var identity = authority.ReadControlClientSnapshot(includeGraph: true).AuthorityIdentity!;
        using (var again = new AutomationStore(db)) check(again.ReadControlClientSnapshot(includeGraph: true).AuthorityIdentity == identity,
            "stable authority GUID survives owner reopening without changing U1 schema or runtime identity");
        var first = new AutomationControlViewModel(data); first.SetProjectContext("项目 A "); await first.ConnectAsync(db);
        var view = new AutomationControlPageView(); view.Attach(first); var window = new Window { Content = view, Width = 760, Height = 700 }; window.Show();
        async Task Press(string name, IAsyncRelayCommand command, AutomationControlViewModel model)
        {
            // v4 本地草稿/原回执在“活动记录”页签；测试先真实切换页签，不绕过界面。
            model.SelectTab("activity"); await Paint(window);
            view.FindControl<Expander>("LocalAutomationRecords")!.IsExpanded = true; await Paint(window);
            var button = view.FindControl<Button>(name)!; button.BringIntoView(); await Paint(window); await Until(() => !model.IsBusy);
            window.Activate(); if (!button.IsEnabled || !button.Focus()) throw new InvalidOperationException("Persistence button unavailable: " + name);
            var previous = command.ExecutionTask;
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Until(() => command.ExecutionTask != previous); await command.ExecutionTask!;
        }
        try
        {
            first.BeginCreation("package"); first.PackageDraft.Id = "native-unsent"; first.PackageDraft.Title = "保留草稿";
            first.PackageDraft.Scope = "owned/**\nfixture.txt"; first.PackageDraft.Acceptance = "Review exact local evidence";
            first.PackageDraft.Model = "devin/swe-2";
            var form = view.FindControl<AutomationControlCreationView>("CreationForm")!;
            form.FindControl<TextBox>("PackageGoalInput")!.Text = "Unsent native goal 中文😀";
            await Press("SaveAutomationDraft", first.SaveDraftCommand, first);
            check(first.SavedDrafts.Count == 1 && authority.ReadControlSnapshot().Revision == 0 && authority.ListEventsAfter(0).Count == 0,
                "real compiled Save button persists Unicode creation input without making a plan or event");
            var restarted = new AutomationControlViewModel(data); restarted.SetProjectContext("项目 A "); await restarted.ConnectAsync(db); view.Attach(restarted);
            check(restarted.PersistenceReady && restarted.SavedDrafts.Count == 1 && restarted.PendingRequests.Count == 0,
                "new native client reconnects and finds the same scoped saved draft without resending anything");
            restarted.SelectedSavedDraft = restarted.SavedDrafts.Single(); await Press("RestoreAutomationDraft", restarted.RestoreDraftCommand, restarted);
            check(restarted.CreationVisible && restarted.PackageDraft.Id == "native-unsent" && restarted.PackageDraft.Goal == "Unsent native goal 中文😀" &&
                restarted.PackageDraft.Scope == "owned/**\nfixture.txt" && restarted.PackageDraft.Model == "devin/swe-2" && authority.ReadControlSnapshot().Revision == 0,
                "focused Restore button restores exact typed creation fields without applying the plan");
            var scope = AutomationPersistenceScope.TryCreate("项目 A ", db, identity).ValueOrThrow();
            var journal = new AutomationRequestJournal(data);
            var original = new ControlCommand("native-lost-receipt", 0, "fixture", ControlAction.SetMode, Mode: DevelopmentMode.SemiAutomatic);
            var json = JsonSerializer.Serialize(original);
            var request = new AutomationRequestRecord { CommandId = original.CommandId, Kind = AutomationRequestKind.Apply, PayloadJson = json,
                PayloadSha256 = AutomationPersistenceValidation.Sha256HexUtf8(json), CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), State = AutomationRequestState.PendingUnknown };
            journal.Record(scope, request, journal.ReadGeneration(scope).ValueOrThrow()).ValueOrThrow();
            authority.ApplyControlCommand(original, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await Until(() => !restarted.IsBusy); await restarted.RefreshAsync();
            await Until(() => !restarted.IsBusy); await restarted.SetModeAsync("manual");
            check(authority.ReadControlSnapshot().Revision == 1 && restarted.StatusText.Contains("GenerationConflict") && restarted.PackageDraft.Goal == "Unsent native goal 中文😀",
                "native stale local generation rejects a new submission before Core and preserves edited input");
            var reopened = new AutomationControlViewModel(data); reopened.SetProjectContext("项目 A "); await reopened.ConnectAsync(db); view.Attach(reopened);
            check(reopened.PendingRequests.Single().PayloadJson == json, "restart retains the exact immutable request whose Core receipt was not locally observed");
            reopened.SelectedPendingRequest = reopened.PendingRequests.Single(); var eventCount = authority.ListEventsAfter(0).Count;
            await Press("ObserveAutomationReceipt", reopened.ObservePendingRequestCommand, reopened);
            check(reopened.PendingRequests.Count == 0 && authority.ReadControlSnapshot().Revision == 1 && authority.ListEventsAfter(0).Count == eventCount &&
                journal.Find(scope, request.CommandId).ValueOrThrow()!.State == AutomationRequestState.ReceiptObserved,
                "native original receipt observation settles local unknown without a second Core operation or event");
            reopened.SelectedSavedDraft = reopened.SavedDrafts.Single(); await reopened.RestoreDraftAsync();
            check(!reopened.CreationVisible && reopened.StatusText.Contains("旧工作台版本") && reopened.SavedDrafts.Count == 1,
                "old creation version is preserved rather than silently restored under a new revision");
            var never = original with { CommandId = "native-never-sent", ExpectedRevision = 1, Mode = DevelopmentMode.Manual };
            var neverJson = JsonSerializer.Serialize(never);
            journal.Record(scope, request with { CommandId = never.CommandId, PayloadJson = neverJson, PayloadSha256 = AutomationPersistenceValidation.Sha256HexUtf8(neverJson) },
                journal.ReadGeneration(scope).ValueOrThrow()).ValueOrThrow();
            reopened.ReloadLocalRecords(); reopened.SelectedPendingRequest = reopened.PendingRequests.Single();
            var path = Path.Combine(data, AutomationPersistenceScope.StoreDirectoryName, scope.ScopeHash + ".json"); var bytes = File.ReadAllBytes(path);
            await reopened.ObservePendingRequestAsync();
            check(reopened.StatusText.Contains("结果仍未知") && reopened.PendingRequests.Count == 1 && File.ReadAllBytes(path).SequenceEqual(bytes) && authority.ReadControlSnapshot().Revision == 1,
                "a genuinely unsent unknown stays unknown with unchanged bytes and is never executed to obtain a receipt");
            var mismatch = false; try { authority.ReadControlReceipt("Apply", JsonSerializer.Serialize(original with { Mode = DevelopmentMode.Manual })); }
            catch (InvalidOperationException) { mismatch = true; }
            check(mismatch && authority.ListEventsAfter(0).Count == eventCount, "read-only receipt lookup refuses same ID with changed immutable payload");
            using var staleClient = AutomationStore.AttachControlClient(db);
            var replacementPath = Path.Combine(area, "replacement.db"); using (var replacement = new AutomationStore(replacementPath)) { }
            await Until(() => !reopened.IsBusy);
            File.Move(db, Path.Combine(area, "original-preserved.db")); File.Copy(replacementPath, db);
            var refused = false; try { staleClient.ApplyControlCommand(new("wrong-authority", 0, "fixture", ControlAction.SetMode, Mode: DevelopmentMode.SemiAutomatic), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
            catch (InvalidOperationException e) { refused = e.Message.Contains("authority changed"); }
            using var newClient = AutomationStore.AttachControlClient(db);
            check(refused && newClient.ReadControlSnapshot().Revision == 0, "same-path database replacement refuses the old cached authority even when the new CAS revision would match");
            await reopened.ConnectAsync(db);
            check(reopened.SavedDrafts.Count == 0 && reopened.PendingRequests.Count == 0 && File.ReadAllBytes(path).SequenceEqual(bytes),
                "reconnecting the replacement authority isolates old drafts and requests while retaining their original file bytes");
            long Rev() => newClient.ReadControlSnapshot().Revision;
            foreach (var id in new[] { "native-question-a", "native-question-b" })
                newClient.ApplyControlCommand(new("define-" + id, Rev(), "fixture", ControlAction.DefinePackage,
                    new(id, "same", "Scoped question", ["owned/**"], ["local evidence"], "codex", "devin/swe-2", "evidence", []), ExpectedPackageVersion: 0), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            newClient.ApplyControlKnowledge(new("native-draft-question", Rev(), "fixture", ControlKnowledgeAction.Ask, Text: "Preserve exact answer",
                FromPackage: "native-question-a", ToPackage: "native-question-b", ExpectedFromVersion: 1, ExpectedToVersion: 1), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await Until(() => !reopened.IsBusy); await reopened.RefreshAsync();
            reopened.SelectedDecision = reopened.Inbox.Single(row => row.Item.Kind == "question"); await Paint(window);
            view.FindControl<TextBox>("ResponseInput")!.Text = "Unsent exact answer 中文😀";
            await Press("SaveAutomationDraft", reopened.SaveDraftCommand, reopened);
            var answerReader = new AutomationControlViewModel(data); answerReader.SetProjectContext("项目 A "); await answerReader.ConnectAsync(db); view.Attach(answerReader);
            answerReader.SelectedSavedDraft = answerReader.SavedDrafts.Single(); await Press("RestoreAutomationDraft", answerReader.RestoreDraftCommand, answerReader);
            check(answerReader.SelectedDecision?.Item.ObjectId == "native-draft-question" && answerReader.ResponseText == "Unsent exact answer 中文😀" &&
                newClient.ReadControlSnapshot().Knowledge.Questions.Single().Answer is null,
                "new native client restores the exact scoped question answer without submitting or publishing it");
            check(view.FindControl<TextBox>("ResponseInput")!.Text == "Unsent exact answer 中文😀",
                "restored question draft reaches the actual compiled answer input");
            var latestAnswer = "Edited after save 中文😀\nDo not erase this answer";
            view.FindControl<TextBox>("ResponseInput")!.Text = latestAnswer;
            newClient.ApplyControlKnowledge(new("external-question-answer", Rev(), "fixture", ControlKnowledgeAction.Answer,
                Text: "Answer recorded elsewhere", QuestionId: "native-draft-question", ExpectedFromVersion: 1, ExpectedToVersion: 1), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await Until(() => !answerReader.IsBusy); await answerReader.RefreshAsync();
            await Paint(window);
            check(answerReader.SelectedDecision is null && !answerReader.DecideCommand.CanExecute("answer") && answerReader.InvalidatedDrafts.Single().Response == latestAnswer,
                "changed authority removes executable old decision but retains latest exact input in its original context");
            check(view.FindControl<StackPanel>("InvalidatedInputPanel")!.IsVisible && view.FindControl<SelectableTextBlock>("InvalidatedInputPreview")!.Text!.Contains(latestAnswer),
                "compiled obsolete-input panel visibly presents the exact retained Unicode answer");
            var afterExternal = newClient.ListEventsAfter(0).Count;
            await Press("SaveInvalidatedInput", answerReader.SaveInvalidatedDraftCommand, answerReader);
            var currentScope = AutomationPersistenceScope.TryCreate("项目 A ", db, newClient.ReadControlClientSnapshot(includeGraph: true).AuthorityIdentity!).ValueOrThrow();
            check(new AutomationDraftStore(data).Load(currentScope).ValueOrThrow().Count == 1,
                "invalidated question input keeps the same original scoped draft identity");
            check(new AutomationDraftStore(data).Load(currentScope).ValueOrThrow().Single().Response == latestAnswer,
                "authority refresh preserves newly edited obsolete answer so explicit Save still retains its exact input");
            check(answerReader.InvalidatedDrafts.Single().ContextJson == answerReader.SavedDrafts.Single().ContextJson &&
                answerReader.InvalidatedDrafts.Single().TodoVersion == answerReader.SavedDrafts.Single().TodoVersion,
                "saving invalidated input retains the original question version and context without rebasing");
            await answerReader.RefreshAsync(); await answerReader.RefreshAsync();
            check(answerReader.InvalidatedDrafts.Count == 1 && answerReader.InvalidatedDrafts.Single().Response == latestAnswer && newClient.ListEventsAfter(0).Count == afterExternal,
                "repeated observation retains one stale-input buffer without generating a decision or duplicate draft");
            answerReader.SelectedSavedDraft = answerReader.SavedDrafts.Single(); await answerReader.RestoreDraftAsync();
            check(answerReader.StatusText.Contains("旧草稿保留") && answerReader.SavedDrafts.Count == 1 &&
                newClient.ReadControlSnapshot().Knowledge.Questions.Single().Answer == "Answer recorded elsewhere",
                "obsolete question draft stays on disk and cannot replace a newer authoritative answer");
            answerReader.DiscardInvalidatedDraft();
            check(!answerReader.HasInvalidatedInput && answerReader.SavedDrafts.Count == 1 && newClient.ListEventsAfter(0).Count == afterExternal,
                "explicit stale-input discard removes only the temporary buffer, not saved draft or authoritative history");
            newClient.ApplyControlKnowledge(new("another-draft-question", Rev(), "fixture", ControlKnowledgeAction.Ask, Text: "Project isolation",
                FromPackage: "native-question-a", ToPackage: "native-question-b", ExpectedFromVersion: 1, ExpectedToVersion: 1), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await Until(() => !answerReader.IsBusy); await answerReader.RefreshAsync();
            answerReader.SelectedDecision = answerReader.Inbox.Single(row => row.Item.Kind == "question");
            answerReader.ResponseText = "Latest scoped unsaved answer";
            newClient.ApplyControlKnowledge(new("answer-another-draft-question", Rev(), "fixture", ControlKnowledgeAction.Answer,
                Text: "Other actor", QuestionId: "another-draft-question", ExpectedFromVersion: 1, ExpectedToVersion: 1), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await Until(() => !answerReader.IsBusy); await answerReader.RefreshAsync();
            check(answerReader.InvalidatedDrafts.Single().Response == "Latest scoped unsaved answer" && answerReader.SaveInvalidatedDraftCommand.CanExecute(null),
                "new unsaved obsolete answer remains explicitly saveable without relying on a previously saved draft");
            var savedPath = Path.Combine(data, AutomationPersistenceScope.StoreDirectoryName, currentScope.ScopeHash + ".json");
            var savedBytes = File.ReadAllBytes(savedPath);
            answerReader.SetProjectContext("项目 B"); await answerReader.ConnectAsync(db);
            check(!answerReader.HasInvalidatedInput && answerReader.InvalidatedInputPreview.Length == 0 && answerReader.SavedDrafts.Count == 0 &&
                !answerReader.SaveInvalidatedDraftCommand.CanExecute(null) && File.ReadAllBytes(savedPath).SequenceEqual(savedBytes),
                "project switch clears temporary obsolete input and never exposes or overwrites the prior scoped saved draft");
        }
        finally { window.Close(); view.Detach(); first.Dispose(); }
        await RunPersistenceFailureAsync(area, check);
    }

    private sealed class FailSecondPublish : IAutomationPersistenceFileIo
    {
        private readonly AutomationPersistenceFileIo _real = new(); private int _publish;
        public void EnsureDirectory(string path) => _real.EnsureDirectory(path);
        public bool FileExists(string path) => _real.FileExists(path);
        public byte[] ReadAllBytes(string path) => _real.ReadAllBytes(path);
        public bool TryAcquireWriteLock(string path, int timeout, out IAutomationPersistenceLock? handle) => _real.TryAcquireWriteLock(path, timeout, out handle);
        public string WriteTempFile(string directory, string hint, byte[] content) => _real.WriteTempFile(directory, hint, content);
        public void Publish(string temp, string target) { if (++_publish == 2) throw new IOException("Fixture receipt publication failure"); _real.Publish(temp, target); }
        public void DeleteQuietly(string path) => _real.DeleteQuietly(path);
    }
    private static async Task RunPersistenceFailureAsync(string area, Action<bool, string> check)
    {
        var db = Path.Combine(area, "failure.db"); var data = Path.Combine(area, "failure-data"); using var authority = new AutomationStore(db);
        using var model = new AutomationControlViewModel(data, new FailSecondPublish()); await model.ConnectAsync(db);
        await model.SetModeAsync("semi");
        check(authority.ReadControlSnapshot().Revision == 1 && model.PendingRequests.Count == 1 && model.StatusText.Contains("本地回执未保存"),
            "Core success followed by actual local publication failure retains pending_unknown and reports the two outcomes separately");
        var events = authority.ListEventsAfter(0).Count; await model.RetryReceiptAsync();
        check(model.PendingRequests.Count == 0 && authority.ReadControlSnapshot().Revision == 1 && authority.ListEventsAfter(0).Count == events,
            "retry after receipt-file failure queries the original receipt without invoking Core Apply again");
        var scope = AutomationPersistenceScope.TryCreate("default-workspace", db, authority.ReadControlClientSnapshot(includeGraph: true).AuthorityIdentity!).ValueOrThrow();
        var path = Path.Combine(data, AutomationPersistenceScope.StoreDirectoryName, scope.ScopeHash + ".json"); var bytes = File.ReadAllBytes(path);
        model.BeginCreation("package"); model.PackageDraft.Goal = "Keep edited input through file lock";
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) await model.SetModeAsync("manual");
        check(authority.ReadControlSnapshot().Revision == 1 && File.ReadAllBytes(path).SequenceEqual(bytes) && model.PackageDraft.Goal.Contains("file lock"),
            "real exclusive local record lock refuses a new Core mutation and preserves file bytes and input");
        model.ReloadLocalRecords();
        using var left = new AutomationControlViewModel(data); using var right = new AutomationControlViewModel(data);
        await left.ConnectAsync(db); await right.ConnectAsync(db); left.BeginCreation("package"); right.BeginCreation("package");
        left.PackageDraft.Goal = "first native writer"; right.PackageDraft.Goal = "second native writer";
        await left.SaveDraftAsync(); await right.SaveDraftAsync();
        check(right.StatusText.Contains("GenerationConflict") && right.PackageDraft.Goal == "second native writer" && left.SavedDrafts.Count == 1 && authority.ReadControlSnapshot().Revision == 1,
            "two native clients at the same local generation cannot silently overwrite each other's draft");
        File.WriteAllText(path, "{\"formatVersion\":999}"); var corrupt = File.ReadAllBytes(path);
        using var damaged = new AutomationControlViewModel(data); await damaged.ConnectAsync(db); await damaged.SetModeAsync("manual");
        check(!damaged.PersistenceReady && authority.ReadControlSnapshot().Revision == 1 && File.ReadAllBytes(path).SequenceEqual(corrupt),
            "damaged or future local records stay intact and prevent unjournaled native Core operations");
        var oldDb = Path.Combine(area, "legacy-no-authority.db"); using (var owner = new AutomationStore(oldDb)) { }
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = oldDb, Pooling = false }.ToString())) { c.Open(); using var q = c.CreateCommand(); q.CommandText = "DROP TABLE control_authority"; q.ExecuteNonQuery(); }
        var oldBytes = File.ReadAllBytes(oldDb); using var old = new AutomationControlViewModel(Path.Combine(area, "legacy-data")); await old.ConnectAsync(oldDb); await old.SetModeAsync("semi");
        check(old.Connected && !old.PersistenceReady && old.Snapshot!.Inbox.Revision == 0 && File.ReadAllBytes(oldDb).SequenceEqual(oldBytes),
            "legacy authority without stable identity remains observable but the client never migrates it or submits an unjournaled decision");
    }
}
