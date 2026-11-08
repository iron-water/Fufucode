using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.App.Abstractions.Graph;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Integration;

/// <summary>Runs a project ticket through the same persistent FUMO sessions as Chat.</summary>
public sealed class GraphNativeExecutor : IGraphRunExecutor
{
    private readonly AppDataLayout _layout;
    private readonly Func<GraphRunRequest, IPluginToolHandler?> _tools;
    private readonly Func<PersistentSession, Task<IDisposable?>>? _attachApproval;
    private readonly Func<string, bool>? _sessionInUse;
    private readonly Func<GraphRunRequest, bool>? _authorize;
    private readonly Func<GraphRunRequest, Task<GraphIntegrationRepairBinding?>>? _resolveRepair;

    public GraphNativeExecutor(AppDataLayout layout, Func<GraphRunRequest, IPluginToolHandler?> tools,
        Func<PersistentSession, Task<IDisposable?>>? attachApproval = null,
        Func<string, bool>? sessionInUse = null, Func<GraphRunRequest, bool>? authorize = null,
        Func<GraphRunRequest, Task<GraphIntegrationRepairBinding?>>? resolveRepair = null)
    {
        _layout = layout;
        _tools = tools;
        _attachApproval = attachApproval;
        _sessionInUse = sessionInUse;
        _authorize = authorize;
        _resolveRepair = resolveRepair;
    }

    private static async Task<string> ReadCommittedHeadAsync(string repository, CancellationToken token)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repository,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.ArgumentList.Add("rev-parse"); start.ArgumentList.Add("--verify"); start.ArgumentList.Add("HEAD^{commit}");
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); throw; }
        var value = (await output.ConfigureAwait(false)).Trim(); var detail = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException("Committed repository baseline unavailable: " + detail);
        return value;
    }

    public async Task<GraphRunResult> ExecuteAsync(GraphRunRequest request, Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var manager = new PersistentSessionManager(string.Empty, string.Empty, _layout);
        Fufucode.Core.Automation.ManagedDevelopmentWorkspace? workspace = null;
        var repair = _resolveRepair is null ? null : await _resolveRepair(request).ConfigureAwait(false);
        using var repairLease = repair is null ? null : new System.IO.FileStream(
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(repair.JournalPath)!, "repair-execution.lock"),
            System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
        // Leads inspect the project. Writing executions always receive a fixed, isolated Git baseline.
        if (request.Identity?.Role == GraphExecutionRole.Executor && repair is null)
        {
            var trust = manager.GetProjectTrustStatus(request.ProjectPath);
            if (trust.RequiresDecision) throw new ProjectTrustDecisionRequiredException(trust);
            var integratedBase = await Fufucode.Core.Automation.ManagedGitIntegration.GetIntegratedCommitAsync(request.ProjectPath, cancellationToken).ConfigureAwait(false)
                ?? await ReadCommittedHeadAsync(request.ProjectPath, cancellationToken).ConfigureAwait(false);
            var created = await Fufucode.Core.Automation.ManagedDevelopmentWorkspace.CreateFromBaseAsync(request.ProjectPath,
                System.IO.Path.Combine(_layout.Root, "auto-dev", "workspaces"), integratedBase, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (created.Status != Fufucode.Core.Automation.ManagedWorkspaceStatus.Ready || created.Workspace is null)
                return new("Failed", "", created.Status + ": " + (created.Error ?? "Workspace creation did not succeed.")) { NoSideEffectsDispatched = true };
            workspace = created.Workspace;
            // A generated path must not bypass the original project's trust boundary.
            manager.SetProjectTrustDecision(workspace.Manifest.WorktreePath,
                trust.Decision ?? ProjectTrustDecision.DoNotTrust);
        }
        var executionPath = repair?.WorktreePath ?? workspace?.Manifest.WorktreePath ?? request.ProjectPath;
        if (repair is not null)
        {
            var trust = manager.GetProjectTrustStatus(request.ProjectPath);
            if (trust.RequiresDecision) throw new ProjectTrustDecisionRequiredException(trust);
            manager.SetProjectTrustDecision(executionPath, trust.Decision ?? ProjectTrustDecision.DoNotTrust);
        }
        var executionAudit = new Fufucode.Core.Automation.ManagedExecutionJournal(
            System.IO.Path.Combine(_layout.Root, "auto-dev", "execution-audit", request.RunId),
            request.TicketId, request.RunId, executionPath);
        var unknownExternalEffects = 0;
        manager.NativeHostExecutionAudit = (call, state, pid) =>
        {
            executionAudit.Record(call, state, pid);
            if (call.Name == "run_command" && state == "effects-unknown") Interlocked.Exchange(ref unknownExternalEffects, 1);
        };
        manager.NativeHostToolAllowed = name => !cancellationToken.IsCancellationRequested &&
            (_authorize?.Invoke(request) ?? true) &&
            (request.Identity?.Role is not (GraphExecutionRole.ProjectLead or GraphExecutionRole.ScopeLead or GraphExecutionRole.Verifier) ||
                name.StartsWith("graph_", StringComparison.Ordinal) ||
                name is "read_file" or "list_dir" or "glob_files" or "grep_search" or "update_plan");
        var handler = _tools(request);
        manager.NativePluginToolProviders = () => handler is null ? [] : [new GraphApplicationToolDispatcher(handler)];
        PersistentSession? session = null;
        IDisposable? approval = null;
        var settled = new TaskCompletionSource<SessionEvent.TurnSettled>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        var outputGate = new object();
        string? error = null;
        var assistant = false;
        var dispatched = false;
        void OnEvent(object? sender, SessionEvent item)
        {
            switch (item)
            {
                case SessionEvent.MessageStarted started:
                    assistant = started.Role == MessageRole.Assistant;
                    break;
                case SessionEvent.TextAppended text when assistant:
                    lock (outputGate) output.Append(text.Delta);
                    break;
                case SessionEvent.ToolCallStatusChanged tool:
                    progress?.Invoke(tool.StatusText ?? tool.Status.ToString());
                    break;
                case SessionEvent.SessionError failed:
                    error = failed.Message;
                    break;
                case SessionEvent.TurnSettled terminal:
                    settled.TrySetResult(terminal);
                    break;
            }
        }
        string Collected() { lock (outputGate) return output.ToString(); }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A discussion currently displayed in Chat owns its session lease. Background work gets
            // its own run session; the ticket context keeps the discussion's identity and correspondence.
            var resume = workspace is null && repair is null && !string.IsNullOrWhiteSpace(request.SessionId)
                && _sessionInUse?.Invoke(request.SessionId) != true
                && !SessionWindowRegistry.TryGetOwner(request.SessionId, out _, out _);
            session = resume
                ? await manager.ResumeAsync(request.SessionId!, cancellationToken).ConfigureAwait(false)
                : await manager.CreateAsync(executionPath, "工作 / " + request.TicketId,
                    cancellationToken, BackendId.NativeCSharp).ConfigureAwait(false);
            if (workspace is null && repair is null && session.Project.Id != request.ProjectId)
                throw new InvalidOperationException("工单会话与项目身份不一致。");
            session.Chat.Event += OnEvent;
            if (_attachApproval is not null) approval = await _attachApproval(session).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.Options.Model))
            {
                var response = await session.Coordinator.SetModelAsync(request.Options.Provider,
                    request.Options.Model, cancellationToken).ConfigureAwait(false);
                if (!response.Success) throw new InvalidOperationException(response.Error);
            }
            await session.Coordinator.SetExecutionModeAsync("auto", cancellationToken).ConfigureAwait(false);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromMinutes(request.Options.BudgetMinutes));
            dispatched = true;
            var executionPrompt = workspace is null && repair is null ? request.Prompt :
                $"Host execution workspace: {executionPath}\nOriginal project identity (not a writable path): {request.ProjectPath}\nAll file tools and command cwd must use relative paths under the execution workspace. Never write to the original project absolute path. Candidate changes are collected by the host.\n" +
                (repair is null ? "" : "This is an existing integration repair tree. Resolve the retained Git conflicts/build failures, preserve both candidates and established acceptance tests, and commit the repaired integration in this tree. The host will require fresh validation; do not claim delivery from your own tests.\n") + request.Prompt;
            await session.Coordinator.SendPromptAsync(executionPrompt, cancellationToken: budget.Token).ConfigureAwait(false);
            var result = await settled.Task.WaitAsync(budget.Token).ConfigureAwait(false);
            if (repair is not null && result.Outcome == SessionOutcome.Completed)
            {
                if (cancellationToken.IsCancellationRequested || _authorize?.Invoke(request) == false)
                    throw new OperationCanceledException(cancellationToken);
                var transaction = await Fufucode.Core.Automation.ManagedGitIntegration.LoadAsync(repair.JournalPath, cancellationToken).ConfigureAwait(false);
                await Fufucode.Core.Automation.ManagedGitIntegration.RefreshPreparedAsync(transaction, cancellationToken).ConfigureAwait(false);
            }
            var delivery = repair is not null ? "IntegrationRepairReady" : workspace is null ? "NoCodeCandidateExpected" :
                (await workspace.CaptureCandidateAsync(CancellationToken.None).ConfigureAwait(false)).PatchSize > 0
                    ? "CandidateReady" : "EmptyCandidate";
            return new GraphRunResult(Volatile.Read(ref unknownExternalEffects) != 0 || error?.Contains("MANAGED_PROCESS_TERMINATION_UNKNOWN", StringComparison.Ordinal) == true ? "Unknown" : result.Outcome.ToString(), Collected(), error ??
                (result.Outcome == SessionOutcome.Completed ? null : result.Summary), session.Session.Id)
            { CandidateManifestPath = workspace?.ManifestPath, DeliveryStatus = delivery };
        }
        catch (OperationCanceledException)
        {
            if (session is null) return new GraphRunResult("Interrupted", Collected(), "派发前已取消。") { NoSideEffectsDispatched = !dispatched };
            try
            {
                // A cancellation request is not a terminal state until the actual session converges.
                await session.Coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (Volatile.Read(ref unknownExternalEffects) != 0)
                    return new GraphRunResult("Unknown", Collected(), "受管进程已请求停止，但外部副作用尚未核实。", session.Session.Id) { CandidateManifestPath = workspace?.ManifestPath };
                if (settled.Task.IsCompletedSuccessfully)
                {
                    var result = await settled.Task.ConfigureAwait(false);
                    return new GraphRunResult(result.Outcome.ToString(), Collected(), result.Summary, session.Session.Id)
                    { CandidateManifestPath = workspace?.ManifestPath };
                }
                if (session.Chat.Activity == SessionActivity.Idle)
                    return new GraphRunResult("Interrupted", Collected(), "原执行会话已确认停止。", session.Session.Id) { CandidateManifestPath = workspace?.ManifestPath };
                return new GraphRunResult("Unknown", Collected(), "停止请求已发出，尚无执行终态确认。", session.Session.Id) { CandidateManifestPath = workspace?.ManifestPath };
            }
            catch (Exception stopError)
            {
                return new GraphRunResult("Unknown", Collected(), "停止未确认：" + stopError.Message, session.Session.Id) { CandidateManifestPath = workspace?.ManifestPath };
            }
        }
        catch (Exception failed)
        {
            return new GraphRunResult(dispatched ? "Unknown" : "Failed", Collected(), failed.Message, session?.Session.Id) { CandidateManifestPath = workspace?.ManifestPath, NoSideEffectsDispatched = !dispatched };
        }
        finally
        {
            try
            {
                if (workspace is not null)
                {
                    // Preserve interrupted candidates without allowing capture failure to leak a session lease.
                    await workspace.CaptureCandidateAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                approval?.Dispose();
                if (session is not null)
                {
                    session.Chat.Event -= OnEvent;
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

}
