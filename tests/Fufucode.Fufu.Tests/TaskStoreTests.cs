using Fufucode.Fufu.Bot;

internal static class TaskStoreTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fufu-task-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ResidentTaskStore(root);
            var task = store.Create("Observed task", "test", "subject", "observe this", "session-1");
            Check(task.State == ResidentTaskState.Queued, "new task starts queued");
            Check(store.SetState(task.Id, ResidentTaskState.Running)?.State == ResidentTaskState.Running, "task enters running");
            Check(store.SetState(task.Id, ResidentTaskState.Awaiting, error: "approval needed")?.State == ResidentTaskState.Awaiting, "task enters awaiting");
            Check(store.SetState(task.Id, ResidentTaskState.Succeeded, result: "done")?.Result == "done", "task enters succeeded with result");

            var reopened = new ResidentTaskStore(root);
            var restored = reopened.Find(task.Id);
            Check(restored?.State == ResidentTaskState.Succeeded && restored.Result == "done", "task survives reopening store");
            Check(reopened.FindBySession("session-1") is null, "completed task is not considered active");

            var failed = reopened.Create("Failed task", "test", "subject", "observe failure", "session-2");
            reopened.SetState(failed.Id, ResidentTaskState.Failed, error: "boom");
            var cancelled = reopened.Create("Cancelled task", "test", "subject", "observe cancel", "session-3");
            reopened.SetState(cancelled.Id, ResidentTaskState.Cancelled);
            Check(reopened.Tasks.Count == 3, "task store retains terminal task history");
            Console.WriteLine("Fufu task store tests passed.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
