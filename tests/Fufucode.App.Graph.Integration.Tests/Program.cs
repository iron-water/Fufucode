using System.Text.Json;
using Fufucode.App.Abstractions.Graph;
using Fufucode.App.Integration;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.AutoDev.Graph;

if (!args.Contains("--live"))
{
    Console.WriteLine("Live integration is opt-in: --live --project <path> --data-root <isolated-path>");
    return 0;
}
string Required(string key)
{
    var index = Array.IndexOf(args, key);
    if (index < 0 || index + 1 >= args.Length) throw new ArgumentException(key + " is required");
    return Path.GetFullPath(args[index + 1]);
}
var projectPath = Required("--project");
var root = Required("--data-root");
var sharedLayout = new AppDataLayout();
if (Path.GetFullPath(root).Equals(sharedLayout.Root, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Live test requires isolated app data.");
var shared = new AppSettingsStore(sharedLayout).Current;
var endpoint = shared.Endpoint ?? throw new InvalidOperationException("Shared model endpoint is not configured.");
var credentials = new WindowsPasswordVaultCredentialResolver();
var key = await credentials.ResolveAsync(endpoint.ApiKeyEnvironmentVariable);
using var catalog = new ModelCatalogClient();
var entries = await catalog.ListAsync(endpoint.BaseUrl, key);
var candidate = entries.FirstOrDefault(item => item.Id.EndsWith("devin/swe-2", StringComparison.OrdinalIgnoreCase) || item.Id.Equals("swe-2", StringComparison.OrdinalIgnoreCase))
    ?? entries.FirstOrDefault(item => item.Id.Equals("auto/deepseek-v4.1-flash", StringComparison.OrdinalIgnoreCase))
    ?? throw new InvalidOperationException("Neither preferred free SWE model nor configured low-cost Flash model is available; no expensive fallback.");
Console.WriteLine("Live model: " + candidate.Id);
Console.WriteLine(candidate.Id.Contains("swe-2", StringComparison.OrdinalIgnoreCase)
    ? "Preferred low-cost test route selected."
    : "Preferred devin/swe-2 unavailable; selected configured Flash route for one bounded read-only audit.");
var layout = new AppDataLayout(root);
var store = new AppSettingsStore(layout);
store.Update(_ => shared with { SelectedModel = candidate.Id, Endpoint = endpoint with { ModelId = candidate.Id }, GraphEnabled = true });
var auditPolicy = new Fufucode.Core.Security.SecurityPolicyStore(root);
auditPolicy.Update(policy => policy with
{
    FileRules = [new Fufucode.Core.Security.FileSecurityRule("**", Fufucode.Core.Security.SecurityAction.Allow,
        Fufucode.Core.Security.SecurityAction.Deny, Fufucode.Core.Security.SecurityAction.Deny)]
});
var manager = new PersistentSessionManager(string.Empty, string.Empty, layout);
manager.SetProjectTrustDecision(projectPath, ProjectTrustDecision.DoNotTrust);
var project = manager.Metadata.GetOrCreateProject(projectPath);
GraphProjectService? authority = null;
ReadOnlyGraphHandler? observedTools = null;
await using var discussion = await manager.CreateAsync(projectPath, "只读验收的已打开讨论", backend: BackendId.NativeCSharp);
var executor = new GraphNativeExecutor(layout,
    request => observedTools = new ReadOnlyGraphHandler(new GraphToolHandler(authority!, request.ProjectPath)),
    sessionInUse: id => id == discussion.Session.Id);
authority = new GraphProjectService(root, executor);
using var service = authority;
service.SetProject(project.Id, projectPath);
await service.ScanAsync();
var scan = service.ReadSnapshot();
if (scan.Modules.Count < 3) throw new InvalidOperationException("Real project scan returned too few modules.");
await service.SaveOptionsAsync(service.ReadOptions() with { Model = candidate.Id, BudgetMinutes = 3, Concurrency = 1, AutoAdvance = false });
service.SetPurpose("只读核查项目结构与实际来源，不修改项目源码。此次验收不授权自动派发后续工作。");
var targets = scan.Modules.Where(module => module.SourcePaths.Any(path => File.Exists(Path.Combine(projectPath, path)))).Take(3).ToArray();
if (targets.Length != 3) throw new InvalidOperationException("Three real file modules are required.");
var targetHashes = targets.SelectMany(module => module.SourcePaths).Where(path => File.Exists(Path.Combine(projectPath, path)))
    .Distinct().ToDictionary(path => path, path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(projectPath, path)))));
var ticket = service.CreateTicket("真实模型验收：项目结构来源",
    "只使用 graph_read 和 graph_context。至少实际调用这两种工具，核查以下三个模块ID：" + string.Join(", ", targets.Select(module => module.Id)) +
    "。报告各自名称与 sourcePaths，不修改任何文件、不运行shell、不创建后续任务。输出末尾写 GRAPH_SMOKE_OK。",
    targets.Select(module => module.Id).ToArray(), ["报告中的模块与来源能在当前项目快照核对"]);
service.LinkSession(ticket.Id, discussion.Session.Id);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
await service.RunTicketAsync(ticket.Id, deadline.Token);
var finished = service.ReadSnapshot();
var run = finished.Runs.Last(item => item.TicketId == ticket.Id);
Console.WriteLine("Run status: " + run.Status);
Console.WriteLine("Ticket status: " + finished.Tickets.Single(item => item.Id == ticket.Id).Status);
if (run.Status != "Completed") throw new InvalidOperationException("Real model run did not complete: " + run.Detail);
if (run.SessionId is null) throw new InvalidOperationException("No durable real session identity.");
var receipt = finished.Entries.Last(item => item.TicketId == ticket.Id && item.Kind == "run-result");
using var receiptDocument = JsonDocument.Parse(receipt.Text);
var output = receiptDocument.RootElement.GetProperty("output").GetString() ?? "";
if (!output.Contains("GRAPH_SMOKE_OK", StringComparison.Ordinal))
    throw new InvalidOperationException("Model output was not durably written back to the ticket.");
if (observedTools is null || observedTools.ReadCalls == 0 || observedTools.ContextCalls == 0)
    throw new InvalidOperationException("Both real Graph read tools must actually be dispatched.");
foreach (var target in targets)
    if (!output.Contains(target.Name, StringComparison.Ordinal) || !target.SourcePaths.All(path => output.Contains(path, StringComparison.Ordinal)))
        throw new InvalidOperationException("Model report omitted a real target module or source: " + target.Name);
foreach (var source in targetHashes)
    if (source.Value != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(projectPath, source.Key)))))
        throw new InvalidOperationException("Read-only audit changed a project source.");
if (run.SessionId == discussion.Session.Id || discussion.Chat.Activity != Fufucode.App.Abstractions.SessionActivity.Idle)
    throw new InvalidOperationException("Background work must preserve the already-open discussion lease.");
Console.WriteLine($"Actual Graph dispatch: read={observedTools.ReadCalls}, context={observedTools.ContextCalls}; three module paths independently matched.");
Console.WriteLine("Open discussion lease preserved; background run has its own durable session.");
if (finished.Tickets.Single(item => item.Id == ticket.Id).Status == "Done")
    throw new InvalidOperationException("Model success must not self-accept a ticket.");
await File.WriteAllTextAsync(Path.Combine(root, "live-result.txt"), string.Join("\n", new[] { "PASS real FUMO execution", "Model: " + candidate.Id, "Project: " + project.Id, "Run: " + run.Id, "Session: " + run.SessionId, "Ticket remains pending human acceptance" }));
Console.WriteLine("PASS real run, Graph tool dispatch, durable output, session association and pending acceptance.");
return 0;

sealed class ReadOnlyGraphHandler(IPluginToolHandler inner) : IPluginToolHandler
{
    public IReadOnlyList<PluginToolRegistration> Registrations => inner.Registrations.Where(item => item.Name is "graph_read" or "graph_context").ToArray();
    public int ReadCalls { get; private set; }
    public int ContextCalls { get; private set; }
    public async Task<PureToolResult> ExecuteAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        if (!Registrations.Any(item => item.Name == call.Name)) return new PureToolResult(call.Id, false, "Read-only integration audit.");
        var result = await inner.ExecuteAsync(call, cancellationToken);
        if (result.Success)
        {
            if (call.Name == "graph_read") ReadCalls++;
            if (call.Name == "graph_context") ContextCalls++;
        }
        return result;
    }
}
