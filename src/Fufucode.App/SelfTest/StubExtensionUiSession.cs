using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Pi;

namespace Fufucode.App.SelfTest;

/// <summary>
/// 测试替身：IChatSession 行为委托给 <see cref="ScriptedChatSession"/>，
/// 另实现 <see cref="IExtensionUiSession"/>，用 RaiseRequest/RaiseClosed 手动模拟
/// 待决扩展 UI 请求（安全重启守卫自测用）。
/// </summary>
public sealed class StubExtensionUiSession : IChatSession, IExtensionUiSession
{
    private readonly ScriptedChatSession _inner = new();
    private readonly List<ExtensionUiRequest> _pending = new();

    public ScriptedChatSession Inner => _inner;

    public SessionConnection Connection => _inner.Connection;
    public SessionActivity Activity => _inner.Activity;
    public SessionOutcome? LastOutcome => _inner.LastOutcome;
    public string? CurrentModel => _inner.CurrentModel;
    public bool CanSend => _inner.CanSend;
    public bool CanStop => _inner.CanStop;
    public int QueuedCount => _inner.QueuedCount;

    public event EventHandler<SessionEvent>? Event
    {
        add => _inner.Event += value;
        remove => _inner.Event -= value;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        _inner.ConnectAsync(cancellationToken);

    public Task SendAsync(string text, CancellationToken cancellationToken = default) =>
        _inner.SendAsync(text, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _inner.StopAsync(cancellationToken);

    // ── IExtensionUiSession ──

    public IReadOnlyList<ExtensionUiRequest> PendingExtensionUiRequests => _pending;

    public ApprovalAvailability ApprovalAvailability { get; set; } = ApprovalAvailability.Verified;

    public string? ApprovalDiagnostic { get; set; }

    public event Action<ExtensionUiRequest>? ExtensionUiRequestReceived;
    public event Action<ExtensionUiRequest>? ExtensionUiRequestClosed;
    public event Action<PiExtensionUiRequestEvent>? ExtensionUiNotification
    {
        add { }
        remove { }
    }
    public event Action<PiExtensionErrorEvent>? ExtensionError;

    /// <summary>登记一个待决扩展请求并广播收到事件。</summary>
    public ExtensionUiRequest RaiseRequest(string id = "req-stub")
    {
        var request = new ExtensionUiRequest(Guid.NewGuid(), id, "confirm",
            System.Text.Json.JsonDocument.Parse("{}").RootElement,
            DateTimeOffset.Now, TimeSpan.FromMinutes(1));
        _pending.Add(request);
        ExtensionUiRequestReceived?.Invoke(request);
        return request;
    }

    /// <summary>移除并广播关闭事件。</summary>
    public void RaiseClosed(ExtensionUiRequest request)
    {
        _pending.Remove(request);
        ExtensionUiRequestClosed?.Invoke(request);
    }

    public void RaiseError(PiExtensionErrorEvent error) => ExtensionError?.Invoke(error);

    public Task RespondToExtensionUiAsync(ExtensionUiRequest request,
        PiExtensionUiResponse response, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
