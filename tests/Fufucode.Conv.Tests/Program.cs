using Fufucode.Conv.Tests;
using Fufucode.Plugins.AutoDev.Engine.Harnesses;

BatchHarnessAdapters.Register();

// CONV-1 acceptance harness: the managed worktree lifecycle, the durable cross-thread inbox and the
// host terminal-state watchdog. Every case uses a real git repository, real child processes and a
// real SQLite journal under a temp directory. No model is contacted and no user data is touched.
if (args.Length >= 3 && args[0] == "--child") return await ChildRunner.Run(args);

var root = Path.Combine(Path.GetTempPath(), "fufucode-conv-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine("CONV evidence: " + root);
var count = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception("FAIL " + label);
    count++;
    Console.WriteLine("PASS " + label);
}
async Task CheckAsync(bool value, string label)
{
    Check(value, label);
    await Task.CompletedTask;
}

await WorktreeChecks.Run(root, Check);
InboxChecks.Run(root, Check);
await WatchdogChecks.Run(root, CheckAsync);

Console.WriteLine($"PASS {count} CONV-1 checks. Evidence: {root}");
return 0;
