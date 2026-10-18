using System.Text.Json;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Plugins.AutoDev.Engine.Harnesses;

BatchHarnessAdapters.Register();

if (args.Length != 2 || args[0] is not ("register" or "run" or "status"))
{
    Console.Error.WriteLine("Usage: Fufucode.HarnessHost register|run|status <manifest.json>");
    return 2;
}
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var config = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.GetFullPath(args[1])), options)
    ?? throw new InvalidDataException("Missing manifest.");
if (!Path.IsPathFullyQualified(config.Database) || !Path.IsPathFullyQualified(config.EvidenceDirectory) ||
    !Path.IsPathFullyQualified(config.Worktree) || config.TimeoutSeconds < 1 || config.TimeoutSeconds > 3600)
    throw new ArgumentException("Absolute paths and a timeout of 1..3600 seconds are required.");
using var store = new AutomationStore(config.Database);
var executor = new HarnessExecutor(store, config.EvidenceDirectory, git: GitCliEvidence.Shared);
if (args[0] == "register")
{
    store.CreateThread(config.Request.ThreadId, config.ProjectId, config.Runtime.Harness,
        config.Request.Model, config.Worktree, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    store.CreateTask(config.Request.TaskId, config.Request.ThreadId, config.Request.TaskId,
        config.BaseSha, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    Console.WriteLine("Registered. No harness started.");
    return 0;
}
if (args[0] == "status")
{
    Console.WriteLine(JsonSerializer.Serialize(executor.Runs.Reconcile(config.Request.RunId), options));
    return 0;
}
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; timeout.Cancel(); };
var result = await executor.ExecuteAsync(config.Runtime, config.Request, timeout.Token);
Console.WriteLine(JsonSerializer.Serialize(result, options));
return result.Status == HarnessRunStatus.Completed ? 0 : 1;

internal sealed record Manifest(string Database, string EvidenceDirectory, string Worktree,
    string ProjectId, string BaseSha, int TimeoutSeconds, HarnessRuntime Runtime, HarnessRequest Request);
