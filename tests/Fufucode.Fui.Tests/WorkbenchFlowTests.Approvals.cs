namespace Fufucode.Fui.Tests;

internal static partial class WorkbenchFlowTests
{
    private static async Task ApprovalOverlapAsync(string project, string data)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = new Session(project, "parent");
        var ui = new TuiController(session, timeout.Token, data);
        var readStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var choices = new TaskCompletionSource<IReadOnlyList<FuiChoice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var action = new TaskCompletionSource<FuiActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Key(ConsoleKey key, bool ctrl = false) => ui.Key(new('\0', key, false, false, ctrl), 12, 25);
        void Type(string text) { foreach (var c in text) ui.Key(new(c, 0, false, false, false), 12, 25); }
        void Command(string text) { Type("/" + text); Key(ConsoleKey.Enter); }
        void Paint() => ui.Painted(25, canApprove: ui.View().Approval is not null);
        void Arrive(string id) { session.Emit(new TuiEvent.Approval(id, "tool " + id)); ui.Update(); Paint(); }
        void Close(string id) { session.Emit(new TuiEvent.ApprovalClosed(id)); ui.Update(); Paint(); }
        async Task Until(Func<TuiView, bool> matches)
        {
            while (true)
            {
                ui.Update();
                if (matches(ui.View())) return;
                await Task.Delay(5, timeout.Token);
            }
        }

        try
        {
            // This reader deliberately finishes normally after cancellation, as a synchronous catalogue can.
            session.ReadChoices = token => { readStarted.SetResult(token); return choices.Task; };
            Command("skills");
            var readToken = await readStarted.Task.WaitAsync(timeout.Token);
            Type("keep draft");
            Arrive("read");
            Check(readToken.IsCancellationRequested, "approval interrupts the outstanding catalogue read");
            Close("read");
            choices.SetResult([new("stale", "stale")]);
            await Until(v => v.Status == "就绪");
            Check(ui.View() is { Draft: "keep draft", Overlay: null }, "a read finishing after the gate closed cannot reopen its picker");

            // The explicit switch has already acquired another lease when the approval wins focus.
            session.Action = () => { actionStarted.SetResult(); return action.Task; };
            Key(ConsoleKey.P, true); Type("new"); Key(ConsoleKey.Enter);
            await actionStarted.Task.WaitAsync(timeout.Token);
            Arrive("switch");
            var child = new Session(project, "child", session.Created);
            action.SetResult(new(Replacement: child));
            await Until(_ => child.Disposals != 0);
            Check(ui.View() is { Session: "parent", Draft: "keep draft", Approval: "tool switch" } &&
                child.Disposals == 1 && session.Disposals == 0, "late switch releases only its target and retains the original gate and draft");
            Close("switch");
            Key(ConsoleKey.C, true);

            // A catalogue can be searched during a turn. Committing a model change still requires idle.
            session.Action = null;
            session.ReadChoices = null;
            session.Emit(new TuiEvent.Status("运行中", true)); ui.Update();
            Command("model");
            await Until(v => v.Overlay?.Title == "切换模型");
            Key(ConsoleKey.Enter);
            Check(ui.View().Lines[^1].Kind == "error" && !ui.View().Lines.Any(l => l.Text == "model=one"),
                "a busy turn permits browsing, then rejects the selected model change");
            session.Emit(new TuiEvent.Status("就绪", false)); ui.Update();

            Type("@src/hello"); Key(ConsoleKey.Tab); Type("!");
            await Until(v => v.Status == "就绪");
            Check(ui.View() is { Draft: "@src/hello!", Overlay: null }, "editing a pending mention discards its picker and keeps the literal");
            Key(ConsoleKey.C, true);
            Command("attach src/hello world.cs");
            await Until(v => v.Status == "就绪 · 附件 1");
            Type("review"); Key(ConsoleKey.Enter);
            // Both the edit and the later approval precede the next event-loop commit point.
            Type(" changed");
            await Until(v => v.Status == "就绪 · 附件 1");
            Check(ui.View().Draft == "review changed" && session.Sent.Count == 0, "editing during file preflight keeps the new draft and cancels sending");
            Key(ConsoleKey.Enter);
            Arrive("files");
            Close("files");
            await Until(v => v.Status == "就绪 · 附件 1");
            Check(ui.View().Draft == "review changed" && session.Sent.Count == 0, "preempted file preflight never dispatches or consumes the attachments");
            session.RejectSend = true;
            Key(ConsoleKey.Enter);
            await Until(v => v.Status == "发送失败 · 附件 1");
            Check(ui.View().Draft == "review changed" && session.Sent.Count == 0, "a rejected submission preserves both text and selected files");
            session.RejectSend = false;
            Key(ConsoleKey.Enter);
            await Until(v => v.Draft.Length == 0 && !v.Busy);
            Check(session.Sent.Single().StartsWith("review changed\n\n[项目文件：src/hello world.cs]", StringComparison.Ordinal),
                "explicit resubmission sends the preserved context exactly once");
        }
        finally
        {
            choices.TrySetCanceled();
            action.TrySetCanceled();
            timeout.Cancel();
            await ui.FinishAsync();
        }
        Check(session.Created.All(s => s.Disposals == 1), "preempted and active leases released once");
    }
}
