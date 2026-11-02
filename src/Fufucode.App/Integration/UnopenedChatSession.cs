using System;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;

namespace Fufucode.App.Integration;

/// <summary>The project shell exists before a conversation. This object never runs a model.</summary>
internal sealed class UnopenedChatSession : IChatSession
{
    public SessionConnection Connection => SessionConnection.Stopped;
    public SessionActivity Activity => SessionActivity.Idle;
    public SessionOutcome? LastOutcome => null;
    public string? CurrentModel => null;
    public bool CanSend => false;
    public bool CanStop => false;
    public int QueuedCount => 0;
    public event EventHandler<SessionEvent>? Event { add { } remove { } }
    public Task SendAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("请先打开 Chat 会话。"));
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
