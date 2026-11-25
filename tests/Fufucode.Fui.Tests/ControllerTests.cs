using System.Threading.Channels;

namespace Fufucode.Fui.Tests;

internal static class ControllerTests
{
    public static void Run()
    {
        var options = TuiOptions.Parse(["--project", "two words", "--session", "abc", "--app-data", "data"]);
        Expect(options is { Project: "two words", SessionId: "abc", AppData: "data", Continue: false }, "explicit session arguments");
        foreach (string[] args in new string[][] { ["--project"], ["--app-data", "--continue"], ["--json"],
                     ["--session", "id", "--continue"], ["--project", "a", "--project", "b"] })
        {
            try { TuiOptions.Parse(args); throw new InvalidDataException("accepted: " + string.Join(' ', args)); }
            catch (ArgumentException) { }
        }

        var s = new TestSession();
        var ui = new TuiController(s, CancellationToken.None);
        void Type(string text) { foreach (var c in text) ui.Key(new(c, (ConsoleKey)0, false, false, false), 10, 30); }
        void Key(ConsoleKey key, bool ctrl = false, bool shift = false, bool alt = false) =>
            ui.Key(new('\0', key, shift, alt, ctrl), 10, 30);
        void Emit(TuiEvent ev) => s.Channel.Writer.TryWrite(ev);

        Type("/session"); Key(ConsoleKey.Enter);
        Expect(s.Sent.Count == 0 && ui.View().Lines.Last().Text.Contains(s.Id), "session command remains local");
        Type("first\nsecond");
        Expect(s.Sent.Count == 0 && ui.View().Draft == "first\nsecond", "literal paste did not submit");
        Key(ConsoleKey.Enter); ui.Update();
        Expect(s.Sent.SequenceEqual(["first\nsecond"]) && ui.View().Draft == "", "submit once");
        Type("next"); Key(ConsoleKey.Enter);
        Expect(s.Sent.Count == 1 && ui.View().Draft == "next", "preserve next draft while busy");
        Emit(new TuiEvent.Message("u", "user", "first\nsecond"));
        Emit(new TuiEvent.Message("a", "assistant", "one", true));
        Emit(new TuiEvent.Message("a", "assistant", "two", true));
        ui.Update();
        Expect(ui.View().Lines.Last().Text == "onetwo", "deltas visible before end");
        Emit(new TuiEvent.Message("a", "assistant", "ignored", true));
        Emit(new TuiEvent.Message("a", "assistant", "final"));
        Emit(new TuiEvent.Message("a", "assistant", "!", true));
        ui.Update();
        Expect(ui.View().Lines.Count(x => x.Kind == "assistant") == 1 && ui.View().Lines.Last().Text == "final!", "authoritative snapshot");

        Emit(new TuiEvent.Approval("1", "tool request A")); ui.Update();
        Key(ConsoleKey.F2);
        Expect(s.Answers.Count == 0, "unpainted approval");
        ui.Painted(30, canApprove: false); Key(ConsoleKey.F2);
        Expect(s.Answers.Count == 0, "a frame without visible request details cannot grant approval");
        ui.Painted(30, canApprove: true); Type("y\nn"); Key(ConsoleKey.Enter);
        Expect(s.Answers.Count == 0 && ui.View().Draft == "next", "typing does not approve or overwrite draft");
        Key(ConsoleKey.F3); Key(ConsoleKey.F2); ui.Update(); Key(ConsoleKey.F2);
        Expect(s.Answers.SequenceEqual([("1", false)]), "reject once, even with key repeat");
        Emit(new TuiEvent.Approval("2", "tool request B")); Emit(new TuiEvent.ApprovalClosed("1")); ui.Update();
        Key(ConsoleKey.F2); Expect(s.Answers.Count == 1, "no click-through onto next request");
        ui.Painted(30, canApprove: true); Key(ConsoleKey.F2); ui.Update();
        Expect(s.Answers.Last() == ("2", true), "explicit allow on visible request");
        Emit(new TuiEvent.ApprovalClosed("2")); ui.Update();
        Type("extra"); Key(ConsoleKey.Enter); Key(ConsoleKey.Escape);
        Expect(ui.View().Draft == "next" && s.Stops == 0, "input on the closing approval frame does not reach the draft or stop the turn");
        ui.Painted(30, canApprove: false);
        Key(ConsoleKey.Escape); Key(ConsoleKey.Escape);
        Expect(s.Stops == 1 && !ui.Exiting, "nonblocking single stop");
        Key(ConsoleKey.PageUp); Expect(ui.View().Scroll == 9, "can scroll while stop is in flight");
        s.StopAck.SetResult(); Emit(new TuiEvent.Status("已中断", false)); ui.Update();
        Key(ConsoleKey.C, true); Expect(!ui.Exiting && ui.View().Draft == "", "clear the prior draft before undo routing");
        Type("undo draft");
        Key(ConsoleKey.Z, true); Expect(ui.View().Draft == "", "Ctrl+Z is routed to the composer");
        Key(ConsoleKey.Z, true, shift: true); Expect(ui.View().Draft == "undo draft", "Ctrl+Shift+Z is routed to the composer");
        Key(ConsoleKey.Z, true); Key(ConsoleKey.Z, alt: true);
        Expect(ui.View().Draft == "undo draft", "Alt+Z is routed to the composer");
        Key(ConsoleKey.W, true); Expect(ui.View().Draft == "undo ", "Ctrl+W is routed as a word kill");
        Key(ConsoleKey.Y, true); Expect(ui.View().Draft == "undo draft", "Ctrl+Y is routed to the composer");
        Key(ConsoleKey.C, true); Expect(!ui.Exiting && ui.View().Draft == "", "Ctrl+C clears idle draft");
        Key(ConsoleKey.C, true); Expect(ui.Exiting, "Ctrl+C exits after the draft is empty");
        AgentActivityTests.Run();
    }

    private static void Expect(bool ok, string what) { if (!ok) throw new InvalidDataException(what); }

    private sealed class TestSession : ITuiSession
    {
        public string Project => "project";
        public string Id => "session-id";
        public string Model => "loopback";
        public IReadOnlyList<TuiLine> History => [new("user", "earlier")];
        public Channel<TuiEvent> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<TuiEvent>();
        public ChannelReader<TuiEvent> Events => Channel.Reader;
        public List<string> Sent { get; } = [];
        public List<(string, bool)> Answers { get; } = [];
        public TaskCompletionSource StopAck { get; } = new();
        public int Stops { get; private set; }
        public Task SendAsync(string text, CancellationToken ct) { Sent.Add(text); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { Stops++; return StopAck.Task; }
        public Task RespondAsync(string id, bool allow, CancellationToken ct) { Answers.Add((id, allow)); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
