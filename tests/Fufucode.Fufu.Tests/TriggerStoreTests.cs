using Fufucode.Fufu.Bot;

internal static class TriggerStoreTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fufu-trigger-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ResidentTriggerStore(root);
            var interval = store.AddInterval("每日观察", TimeSpan.FromSeconds(10));
            var bridge = store.AddBridgeEvent("任务失败提醒", "error", TimeSpan.FromMinutes(5));
            Check(interval.Kind == ResidentTriggerKind.Interval && interval.NextDueAt is not null,
                "interval trigger has next due time");
            Check(bridge.Kind == ResidentTriggerKind.BridgeEvent && bridge.EventType == "error",
                "bridge trigger stores event type");
            store.Update(interval.Id, x => x with { LastFiredAt = DateTimeOffset.UtcNow });
            var reopened = new ResidentTriggerStore(root);
            Check(reopened.Find(interval.Id)?.LastFiredAt is not null, "trigger survives reopening store");
            Check(reopened.Triggers.Count == 2, "trigger store retains both conditions");
            Console.WriteLine("Fufu trigger store tests passed.");
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
