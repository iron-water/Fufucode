using System.Net.Http.Headers;
using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.App.Abstractions.Graph;
using Fufucode.App.Integration;
using Fufucode.Core;
using Fufucode.Plugins.AutoDev.Graph;

// This is a test driver of the product authority, not a second scheduler or implementation.
return await Driver.RunAsync(args);

internal static class Driver
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string? Value(string flag) { var i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (args.Contains("--fixture-test"))
        {
            var fixture = @"C:\tmp\work\2026-11-05-sgline-live\scratch\candidate-fixture";
            Directory.CreateDirectory(fixture);
            foreach (var pair in new[] { (0, "EmptyCandidateBlocked"), (12, "CompletedCandidate"), (-1, "InvalidCandidateBlocked") })
            {
                var path = Path.Combine(fixture, "candidate-" + pair.Item1 + ".json");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { PatchSize = pair.Item1 }));
                if (CandidateEvidence.Classify(path) != pair.Item2) throw new InvalidOperationException("Candidate fixture failed");
            }
            if (!CandidateEvidence.BlockingSummary("approval denied 无法执行").Contains("denied")) throw new InvalidOperationException("Summary fixture failed");
            Console.WriteLine("status=FixturePassed"); return 0;
        }
        var project = Value("--project");
        var root = Value("--data-root");
        if (!args.Contains("--live") || project is null || root is null)
        {
            Console.WriteLine("status=OptInRequired --live --data-root <isolated-root> --project <SGline-root> [--model workbuddy/hy4free]");
            return 2;
        }
        root = Path.GetFullPath(root); project = Path.GetFullPath(project);
        if (!root.Equals(@"C:\tmp\work\2026-11-05-sgline-live", StringComparison.OrdinalIgnoreCase)
            || !project.Equals(@"C:\Projects\SGline", StringComparison.OrdinalIgnoreCase))
        { Console.WriteLine("status=InvalidIsolationPaths"); return 2; }
        // Explicitly create dedicated children before any writes, never use C:\tmp itself.
        var evidence = Path.Combine(root, "evidence", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(evidence);
        var data = Path.Combine(root, "data", Path.GetFileName(evidence)); Directory.CreateDirectory(data);
        var model = Value("--model") ?? "workbuddy/hy4free";
        var approvalDir = Value("--approval-dir");
        if (approvalDir is not null)
        {
            approvalDir = Path.GetFullPath(approvalDir);
            if (!approvalDir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Console.WriteLine("status=InvalidApprovalPath"); return 2; }
            ApprovalBridge.ValidatePath(approvalDir);
            Directory.CreateDirectory(approvalDir);
        }
        var feedbackFile = Value("--feedback-file");
        if (feedbackFile is not null)
        {
            feedbackFile = Path.GetFullPath(feedbackFile);
            if (!feedbackFile.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(feedbackFile) != ".jsonl")
            { Console.WriteLine("status=InvalidFeedbackPath"); return 2; }
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(feedbackFile)!); dir is not null && dir.FullName.Length >= root.Length; dir = dir.Parent)
                if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                { Console.WriteLine("status=InvalidFeedbackPath"); return 2; }
            if (File.Exists(feedbackFile) && File.GetAttributes(feedbackFile).HasFlag(FileAttributes.ReparsePoint))
            { Console.WriteLine("status=InvalidFeedbackPath"); return 2; }
        }

        string[] permitted = ["workbuddy/hy4free", "group/deepseek-v4.1-flash-free", "auto/deepseek-v4.1-flash"];
        if (!permitted.Contains(model, StringComparer.Ordinal)) { Console.WriteLine("status=ModelNotAllowed"); return 2; }
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        try
        {
            // Read only: do not construct a shared settings store or copy a credential-bearing file.
            var shared = new AppDataLayout();
            var settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(Path.Combine(shared.Root, "settings.json")), jsonOptions);
            var endpoint = settings?.Endpoint;
            if (endpoint is null) { Console.WriteLine("status=EndpointMissing"); return 3; }
            using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            ICredentialResolver credentials = OperatingSystem.IsWindows() ? new WindowsPasswordVaultCredentialResolver() : new EnvironmentCredentialResolver();
            var key = await credentials.ResolveAsync(endpoint.ApiKeyEnvironmentVariable, budget.Token);
            if (!string.IsNullOrWhiteSpace(key))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            // A configured local gateway may legitimately allow anonymous access; let the server decide.
            using var response = await http.GetAsync(endpoint.BaseUrl.TrimEnd('/') + "/models", budget.Token);
            if (!response.IsSuccessStatusCode) { Console.WriteLine("status=CatalogUnavailable"); return 3; }
            using var catalog = JsonDocument.Parse(await response.Content.ReadAsStringAsync(budget.Token));
            var ids = catalog.RootElement.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
            foreach (var id in permitted.Where(ids.Contains)) Console.WriteLine("modelid=" + id);
            if (!ids.Contains(model)) { Console.WriteLine("status=RequestedModelUnavailable"); return 3; }
            // Settings contain only endpoint coordinates and the vault/environment variable NAME, never the key.
            var layout = new AppDataLayout(data);
            new AppSettingsStore(layout).Update(_ => new AppSettings(SelectedModel: model,
                Endpoint: endpoint with { ModelId = model, Models = [model] }, AutoCheckUpdates: false));
            var manager = new PersistentSessionManager("", "", layout);
            manager.SetProjectTrustDecision(project, ProjectTrustDecision.DoNotTrust);
            var record = manager.Metadata.GetOrCreateProject(project);
            GraphProjectService? service = null;
            var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sink = new Evidence(Path.Combine(evidence, "events.jsonl"));
            var native = new GraphNativeExecutor(layout,
                request => new GraphToolHandler(service!, request.ProjectPath, request.Identity),
                attachApproval: session =>
                {
                    var telemetry = new SessionTelemetry(session, sink);
                    if (approvalDir is not null)
                        return Task.FromResult<IDisposable?>(new CombinedLease(telemetry, new ApprovalBridge(session, approvalDir, budget.Token)));
                    if (session.Project.Path.Equals(project, StringComparison.OrdinalIgnoreCase))
                        return Task.FromResult<IDisposable?>(telemetry); // Read/graph-only lead; no shell capability.
                    // No approval owner: let the existing security dispatcher allow ordinary authorized
                    // tools and reject actual Ask requests. Do not artificially block before dispatch.
                    sink.Add(new { kind = "Approval", status = "NoInteractiveApprovalOwner", path = session.Project.Path });
                    return Task.FromResult<IDisposable?>(telemetry);
                });
            var executor = new ObservedExecutor(native, sink, model);
            service = new GraphProjectService(data, executor);
            var safeToDispose = false;
            {
                service.SetProject(record.Id, project);
                await service.SaveOptionsAsync(new GraphOptions { Model = model, Provider = endpoint.ProviderName,
                    BudgetMinutes = 8, Concurrency = 1, ScanDepth = 3, AutoAdvance = false }, budget.Token);
                service.SetPurpose("SGLINE真实任务：互动UI延续笔谈，借鉴ChatGPT intelligenceUI；互动应先提出具体控件/交互方案交human收件体验，不以预设模式选择或按钮装饰冒充互动；应用支持hy4/DeepSeek；先测量卡顿再修复。editor_screen.dart每delta更新_penTalkDraft导致重复渲染只是待测假设，不是已证根因。SM X620真设备未授权覆盖私人数据，只可独立app id fixture或受控preview。主应用是app/，领域packages/；legacy仅参考，standalone是旧应用且其AGENTS不套主app。只读项目根README及app/README并遵守适用指令；实现前读取app/docs/PEN_TALK.md现有基线约束：纸面上对话、不弹独立聊天页，AI流式仍可书写/擦除/缩放，落盘普通block，双语与停止撤销。原则监控由主控按其人工记录补做，驱动器不冒充完成原则验收。负责人使用真实graph工具建工单、拆分、委派与审查，不由测试驱动器预造票。审查必须有测量基线/复测、定向测试、候选worktree证据、风险及未验证项，不把build当UI验收。不读用户私人笔记、笔记库、密钥或用户数据；不得发布、部署、安装、推送或合并候选到主项目。只允许明确配置的hy4/DeepSeek两族，优先免费，禁止昂贵fallback。预算首轮最多8分钟、并发1。安全审批缺监听者必须明确Blocked，禁止关闭安全或自行allow-all。监控七原则：最高原则节约人类注意力；核心假设未来模型会越来越强大；所有决策透明可追溯；前端视觉克制、洗练、一致、理性；在约束下动态自演化；最小充分信息；高耦合任务尽量放在一个Agent内。组织选择不可默认复杂，不得以节约注意力隐藏判断。检测候选交付即停止自治，失败/Unknown不重放。");
                service.UpsertModule(null, null, "Application", "Main application, not legacy", ["app"]);
                service.UpsertModule(null, null, "Domain packages", "Pure Dart domain", ["packages"]);
                var observed = executor.Results;
                service.Changed += () =>
                {
                    var s = service.ReadSnapshot();
                    sink.Add(new { kind = "Projection", revision = s.Revision,
                        runs = s.Runs.Select(x => new { x.Id, x.TicketId, x.Status, x.SessionId }),
                        tickets = s.Tickets.Select(x => new { x.Id, x.Status }),
                        organization = s.Organization,
                        humanMessages = s.Entries.Where(x => x.Actor == "human").Select(x => new { x.Id, x.Kind, x.CreatedUtc }) });
                    foreach (var run in s.Runs)
                    {
                        if (!observed.TryGetValue(run.Id, out var result)) continue;
                        // A wrapper receipt is not authoritative until the service has committed its run-result entry.
                        if (!s.Entries.Any(e => e.Kind == "run-result" && e.Reference == run.Id)) continue;
                        var status = result.Status.ToLowerInvariant();
                        if (status is "failed" or "unknown" or "interrupted" or "cancelled" or "canceled")
                            stopped.TrySetResult(result.Status);
                        else if (status == "completed" && result.CandidateManifestPath is { } candidate && File.Exists(candidate))
                            stopped.TrySetResult(CandidateEvidence.Classify(candidate));
                    }
                };
                sink.Add(new { kind = "Driver", status = "StartingTicketlessAutonomy", modelid = model, path = project });
                try
                {
                    await service.SetAutonomyAsync(true, budget.Token);
                    var feedback = new FeedbackInbox(feedbackFile, Path.Combine(evidence, "feedback-acks.jsonl"), sink);
                    while (!stopped.Task.IsCompleted)
                    {
                        feedback.Deliver(service, project);
                        await Task.WhenAny(stopped.Task, Task.Delay(1000, budget.Token));
                        budget.Token.ThrowIfCancellationRequested();
                    }

                }
                catch (OperationCanceledException) { sink.Add(new { kind = "Driver", status = "BudgetExpired" }); }
                finally
                {
                    async Task SettleAsync()
                    {
                        await service.SetAutonomyAsync(false, CancellationToken.None);
                        await service.StopAllAsync(CancellationToken.None);
                    }
                    var settlement = SettleAsync();
                    try
                    {
                        await settlement.WaitAsync(TimeSpan.FromSeconds(45));
                        safeToDispose = true;
                    }
                    catch (TimeoutException)
                    {
                        sink.Add(new { kind = "Driver", status = "UnknownCleanupDeadline", path = data,
                            stoppedConfirmed = false, disposed = false });
                        Console.WriteLine("status=UnknownCleanupDeadline");
                        Console.WriteLine("path=" + evidence);
                        // Keep the process and live authority owned until actual acknowledgement.
                        // The deadline reports Unknown; it is not permission to kill or dispose live work.
                        await settlement;
                        safeToDispose = true;
                        sink.Add(new { kind = "Driver", status = "CleanupEventuallyAcknowledged", path = data });
                    }
                }
            }
            if (!safeToDispose) return 5; // No Dispose: outstanding work has not acknowledged stopping.
            service.Dispose();
            Console.WriteLine("status=" + (stopped.Task.IsCompletedSuccessfully ? stopped.Task.Result : "BudgetExpired"));
            Console.WriteLine("path=" + evidence);
            return 4; // No end-to-end success claim while headless approval is absent.
        }
        catch (Exception ex)
        {
            // Exception messages, payloads, prompts and keys are intentionally not printed.
            Console.WriteLine("status=Failed type=" + ex.GetType().Name);
            Console.WriteLine("path=" + evidence);
            return 1;
        }
    }
}

internal static class CandidateEvidence
{
    internal static string Classify(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var size = doc.RootElement.EnumerateObject().First(x => x.Name.Equals("PatchSize", StringComparison.OrdinalIgnoreCase)).Value.GetInt64();
            return size > 0 ? "CompletedCandidate" : size == 0 ? "EmptyCandidateBlocked" : "InvalidCandidateBlocked";
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException)
        { return "InvalidCandidateBlocked"; }
    }
    internal static string[] BlockingSummary(string text) => new[]
    {
        ("approval", "approval"), ("审批", "approval"), ("拒绝", "denied"), ("denied", "denied"),
        ("blocked", "blocked"), ("权限", "permission"), ("permission", "permission"), ("无法", "unable"), ("cannot", "unable")
    }.Where(x => text.Contains(x.Item1, StringComparison.OrdinalIgnoreCase)).Select(x => x.Item2).Distinct().ToArray();
}
internal sealed class SessionTelemetry : IDisposable
{
    private readonly PersistentSession session;
    private readonly Evidence sink;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> failures = new(StringComparer.Ordinal);
    internal SessionTelemetry(PersistentSession session, Evidence sink)
    { this.session = session; this.sink = sink; session.Chat.Event += OnEvent; }
    private void OnEvent(object? sender, SessionEvent item)
    {
        if (item is SessionEvent.ToolCallStatusChanged tool && tool.Status == ToolCallStatus.Failed)
            failures.TryAdd(tool.ToolCallId, 0);
    }
    public void Dispose()
    {
        session.Chat.Event -= OnEvent;
        sink.Add(new { kind = "SessionToolFailures", sessionId = session.Session.Id, actualFailedToolCount = failures.Count,
            definition = "unique ToolCallId with actual ToolCallStatus.Failed; not inferred from model text" });
    }
}
internal sealed class CombinedLease(IDisposable first, IDisposable second) : IDisposable
{
    public void Dispose() { try { second.Dispose(); } finally { first.Dispose(); } }
}

internal sealed class ApprovalBridge : IDisposable
{
    private readonly Fufucode.Core.Approvals.SharedApprovalGate gate;
    private readonly CancellationTokenSource lifetime;
    private readonly string directory;
    private readonly Task watch;
    private readonly object fileGate = new();
    private readonly HashSet<string> processed = new(StringComparer.Ordinal);
    internal static void ValidatePath(string path)
    {
        var root = Path.GetFullPath(@"C:\tmp\work\2026-11-05-sgline-live");
        path = Path.GetFullPath(path);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid approval path");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Reparse point");
        for (var dir = new DirectoryInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!); dir is not null && dir.FullName.Length >= root.Length; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Reparse point");
    }
    internal ApprovalBridge(PersistentSession session, string parent, CancellationToken cancellationToken)
    {
        directory = Path.Combine(parent, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(session.Session.Id))));
        ValidatePath(directory);
        Directory.CreateDirectory(directory);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        gate = new Fufucode.Core.Approvals.SharedApprovalGate(session.Chat as IExtensionUiSession
            ?? throw new InvalidOperationException("Session does not expose native approval requests"));
        gate.Requested += Requested;
        gate.Decided += Decided;
        foreach (var request in gate.Pending) Requested(request);
        watch = WatchAsync();
    }
    private static string Hash(Fufucode.Core.Approvals.SharedApprovalRequest request) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { request.ApprovalId, request.ToolCallId, request.ToolName, request.ArgsJson }))));
    private void Append(string name, object value)
    {
        lock (fileGate)
        {
            var path = Path.Combine(directory, name); ValidatePath(path);
            File.AppendAllText(path, JsonSerializer.Serialize(value) + Environment.NewLine);
        }
    }
    private void Requested(Fufucode.Core.Approvals.SharedApprovalRequest request) =>
        Append("pending.jsonl", new { request, requestHash = Hash(request), status = "Pending", source = "SharedApprovalGate" });
    private void Decided(Fufucode.Core.Approvals.SharedApprovalResolution resolution) => Append("resolutions.jsonl", resolution);
    private async Task WatchAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    var path = Path.Combine(directory, "decision.json"); ValidatePath(path);
                    if (File.Exists(path))
                    {
                        var raw = await File.ReadAllTextAsync(path, lifetime.Token);
                        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
                        if (!processed.Contains(fingerprint))
                        {
                            using var doc = JsonDocument.Parse(raw);
                            var item = doc.RootElement;
                            var names = item.EnumerateObject().Select(x => x.Name).ToArray();
                            if (names.Length != 4 || names.Any(x => x is not ("approvalId" or "requestHash" or "source" or "approved"))) throw new JsonException();
                            var id = item.GetProperty("approvalId").GetString();
                            var hash = item.GetProperty("requestHash").GetString();
                            var source = item.GetProperty("source").GetString();
                            var approved = item.GetProperty("approved").GetBoolean();
                            var request = gate.Pending.SingleOrDefault(x => x.ApprovalId == id);
                            if (request is not null && hash == Hash(request) && source == "controller-simulated-human")
                            {
                                var resolution = await gate.DecideAsync(id!, approved, source, lifetime.Token);
                                Append("decision-acks.jsonl", new { id, requestHash = hash, resolution.Accepted, resolution.Approved, source });
                            }
                            else Append("decision-acks.jsonl", new { id, status = "RejectedBindingOrSource" });
                            processed.Add(fingerprint);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
                { /* Partial atomic replacement or malformed input grants no approval; retry on next bounded tick. */ }
                await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public void Dispose()
    {
        lifetime.Cancel();
        watch.GetAwaiter().GetResult();
        gate.Requested -= Requested; gate.Decided -= Decided;
        gate.Dispose(); lifetime.Dispose();
    }
}

internal sealed class FeedbackInbox(string? file, string ackPath, Evidence sink)
{
    private readonly Dictionary<string, string> acknowledged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphMessageDraft> pending = new(StringComparer.Ordinal);
    internal void Deliver(GraphProjectService service, string project)
    {
        if (file is null || !File.Exists(file)) return;
        try
        {
            // Never evaluate input. Only complete JSONL records are messages, not control commands.
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new IOException();
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(file)!); dir is not null && dir.FullName.Length >= @"C:\tmp\work\2026-11-05-sgline-live".Length; dir = dir.Parent)
                if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException();
            var text = File.ReadAllText(file);
            var lines = text.Split('\n');
            foreach (var line in lines.Take(lines.Length - 1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > 32768) { sink.Add(new { kind = "Feedback", status = "RejectedOversize" }); continue; }
                using var doc = JsonDocument.Parse(line);
                var item = doc.RootElement;
                var fields = item.EnumerateObject().Select(x => x.Name).ToArray();
                if (fields.Length != 3 || fields.Any(x => x is not ("id" or "source" or "text"))
                    || item.GetProperty("source").GetString() != "human")
                { sink.Add(new { kind = "Feedback", status = "RejectedSchema" }); continue; }
                var id = item.GetProperty("id").GetString();
                var body = item.GetProperty("text").GetString();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                    || string.IsNullOrWhiteSpace(body))
                { sink.Add(new { kind = "Feedback", status = "RejectedSchema" }); continue; }
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)));
                if (acknowledged.TryGetValue(id, out var prior))
                {
                    if (prior != hash) sink.Add(new { kind = "Feedback", status = "RejectedIdReuse", id });
                    continue;
                }
                var snapshot = service.ReadSnapshot();
                var lead = snapshot.Organization!.Responsibilities.Single(x => x.Role == GraphExecutionRole.ProjectLead);
                // This envelope is product feedback, never authority to change trust, tools, budget or model restrictions.
                if (!pending.TryGetValue(id, out var draft))
                {
                    draft = new GraphMessageDraft(lead.Id, false, "feedback",
                        "Human product feedback (verbatim follows; no permission, trust, budget or model authorization changes):\n" + body,
                        null, null, snapshot.Revision);
                    pending.Add(id, draft);
                }
                service.SendOrganizationMessage("driver-feedback-" + id, draft);
                service.SignalProjectLead(project, "human-message", "driver-feedback-" + id);
                File.AppendAllText(ackPath, JsonSerializer.Serialize(new { id, hash, status = "PersistedAndSignalled", utc = DateTime.UtcNow }) + Environment.NewLine);
                acknowledged.Add(id, hash);
                sink.Add(new { kind = "Feedback", status = "PersistedAndSignalled", id, path = ackPath });
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        { sink.Add(new { kind = "Feedback", status = "PendingOrInvalid", type = ex.GetType().Name }); }
    }
}

internal sealed class Evidence(string path)
{
    private readonly object gate = new();
    internal void Add(object value) { lock (gate) File.AppendAllText(path, JsonSerializer.Serialize(new { utc = DateTime.UtcNow, value }) + Environment.NewLine); }
}
internal sealed class ObservedExecutor(IGraphRunExecutor inner, Evidence sink, string selectedModel) : IGraphRunExecutor
{
    internal System.Collections.Concurrent.ConcurrentDictionary<string, GraphRunResult> Results { get; } = new();
    public async Task<GraphRunResult> ExecuteAsync(GraphRunRequest request, Action<string>? progress, CancellationToken cancellationToken)
    {
        if (request.Options.Model != selectedModel || request.Options.BudgetMinutes > 8 || request.Options.Concurrency != 1)
        {
            var blocked = new GraphRunResult("Failed", "", "BlockedConfigurationDrift");
            Results[request.RunId] = blocked;
            return blocked;
        }
        sink.Add(new { kind = "RunStarted", request.RunId, request.TicketId, modelid = request.Options.Model, role = request.Identity?.Role.ToString() });
        var result = await inner.ExecuteAsync(request, _ => progress?.Invoke("Running"), cancellationToken);
        sink.Add(new { kind = "RunSettled", request.RunId, request.TicketId, result.Status, result.SessionId, path = result.CandidateManifestPath,
            approval = result.Error?.Contains("BlockedNoAttachApprovalListener", StringComparison.Ordinal) == true ? "BlockedNoAttachApprovalListener" : null });
        sink.Add(new { kind = "ModelBlockingSummary", request.RunId, request.TicketId,
            categories = CandidateEvidence.BlockingSummary(result.Output + "\n" + result.Error),
            rawTextSaved = false, reason = "category-only summary prevents keys or private note text leaking into evidence" });
        if (result.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) && result.CandidateManifestPath is { } manifest)
            sink.Add(new { kind = "CandidateClassification", request.RunId, status = CandidateEvidence.Classify(manifest), path = manifest });
        Results[request.RunId] = result;
        return result;
    }
}
