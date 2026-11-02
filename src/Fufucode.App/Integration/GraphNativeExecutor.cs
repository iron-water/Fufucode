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

    public GraphNativeExecutor(AppDataLayout layout, Func<GraphRunRequest, IPluginToolHandler?> tools,
        Func<PersistentSession, Task<IDisposable?>>? attachApproval = null,
        Func<string, bool>? sessionInUse = null)
    {
        _layout = layout;
        _tools = tools;
        _attachApproval = attachApproval;
        _sessionInUse = sessionInUse;
    }

    public async Task<GraphRunResult> ExecuteAsync(GraphRunRequest request, Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var manager = new PersistentSessionManager(string.Empty, string.Empty, _layout);
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
            var resume = !string.IsNullOrWhiteSpace(request.SessionId)
                && _sessionInUse?.Invoke(request.SessionId) != true
                && !SessionWindowRegistry.TryGetOwner(request.SessionId, out _, out _);
            session = resume
                ? await manager.ResumeAsync(request.SessionId!, cancellationToken).ConfigureAwait(false)
                : await manager.CreateAsync(request.ProjectPath, "工单 / " + request.TicketId,
                    cancellationToken, BackendId.NativeCSharp).ConfigureAwait(false);
            if (session.Project.Id != request.ProjectId)
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
            await session.Coordinator.SendPromptAsync(request.Prompt, cancellationToken: budget.Token).ConfigureAwait(false);
            var result = await settled.Task.WaitAsync(budget.Token).ConfigureAwait(false);
            return new GraphRunResult(result.Outcome.ToString(), Collected(), error ??
                (result.Outcome == SessionOutcome.Completed ? null : result.Summary), session.Session.Id);
        }
        catch (OperationCanceledException)
        {
            if (session is null) return new("Interrupted", Collected(), "派发前已取消。");
            try
            {
                // A cancellation request is not a terminal state until the actual session converges.
                await session.Coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (settled.Task.IsCompletedSuccessfully)
                {
                    var result = await settled.Task.ConfigureAwait(false);
                    return new(result.Outcome.ToString(), Collected(), result.Summary, session.Session.Id);
                }
                if (session.Chat.Activity == SessionActivity.Idle)
                    return new("Interrupted", Collected(), "原执行会话已确认停止。", session.Session.Id);
                return new("Unknown", Collected(), "停止请求已发出，尚无执行终态确认。", session.Session.Id);
            }
            catch (Exception stopError)
            {
                return new("Unknown", Collected(), "停止未确认：" + stopError.Message, session.Session.Id);
            }
        }
        catch (Exception failed)
        {
            return new(dispatched ? "Unknown" : "Failed", Collected(), failed.Message, session?.Session.Id);
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
