using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.Fui;

internal sealed partial class FumoTuiSession
{
    public async Task<FuiSessionInfo> InspectAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closing, this);
        var native = _session.Chat as NativeCSharpChatSession;
        var attachments = native?.GetHistoryMessages()
            .Sum(message => message.Images?.Count ?? 0) ?? 0;
        string? thinking = null;
        try
        {
            thinking = (await _session.Coordinator.QueryThinkingLevelsAsync(ct)).CurrentLevel;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The level remains unknown when the live coordinator cannot prove it.
        }

        FuiUsageInfo? usage = null;
        var partial = false;
        try
        {
            var report = await Task.Run(() => ReadOnlySessionReport.Load(
                _project.Manager.Layout, Project, Id), ct);
            partial = report.PartialTail;
            var totals = SessionUsageQuery.Query(report.History.CurrentBranchMessages).Session;
            usage = new(
                totals.Input.Value,
                totals.Output.Value,
                totals.TotalTokens.Value,
                totals.CacheRead.Value,
                totals.CacheWrite.Value);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            partial = true;
        }

        return new(
            Project,
            Id,
            Model,
            thinking,
            _projection.MessageCount,
            _projection.ToolCount,
            _projection.ErrorCount,
            attachments,
            _session.Chat.QueuedCount,
            usage,
            partial);
    }

    public Task<string> RetryLastUserAsync(CancellationToken ct)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            ct.ThrowIfCancellationRequested();
            if (_session.Chat is not NativeCSharpChatSession native)
                throw new InvalidOperationException("当前会话没有 FUMO 原生发送接口，无法重试。");
            if (_workerPending || native.Activity != SessionActivity.Idle || native.QueuedCount != 0)
                throw new InvalidOperationException("当前回合仍在运行；请等待结束或先停止，历史未修改。");
            var previous = native.GetHistoryMessages()
                .LastOrDefault(message => message.Role == PureAgentRole.User && !string.IsNullOrWhiteSpace(message.Content));
            if (previous is null)
                throw new InvalidOperationException("当前分支没有可重发的用户文本消息，历史未修改。");
            var text = previous.Content;
            SubmitPrompt(() => native.SendAsync(text, ct));
            return Task.FromResult(text);
        }
    }
}
