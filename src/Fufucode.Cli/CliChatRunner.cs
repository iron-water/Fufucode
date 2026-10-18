using System.Text.Json;
using System.Threading.Channels;
using Fufucode.App.Abstractions;
using Fufucode.Core;

namespace Fufucode.Cli;

public sealed record CliChatResult(SessionOutcome Outcome, string Text, string? Error, bool Handled = false,
    int? FailureExitCode = null);

/// <summary>One stdin reader per turn; RPC callbacks only enqueue requests.</summary>
public static class CliChatRunner
{
    private sealed record Failure(string Error, int ExitCode);
    public static async Task<CliChatResult> RunAsync(IChatSession chat, IExtensionUiSession? approval,
        string prompt, bool json, TextWriter output, TextReader input,
        CancellationToken cancellationToken = default, TextWriter? diagnostics = null,
        bool autoApprove = false)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        diagnostics ??= Console.Error;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var settled = new TaskCompletionSource<SessionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<Failure>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Fail(string error, int code = 3) => failure.TrySetResult(new Failure(error, code));
        var requests = Channel.CreateUnbounded<ExtensionUiRequest>(new UnboundedChannelOptions { SingleReader = true });
        var answer = new System.Text.StringBuilder();
        var streamingText = new Dictionary<string, System.Text.StringBuilder>();
        var gate = new object();
        var answered = new HashSet<ExtensionUiRequest>(ReferenceEqualityComparer.Instance);
        var denied = false;
        var wasHandled = false;
        var handledSession = chat as IPromptHandledSession;
        void Handled() { wasHandled = true; settled.TrySetResult(SessionOutcome.Completed); }
        EventHandler<SessionEvent> onEvent = (_, ev) =>
        {
            lock (gate)
            {
                switch (ev)
                {
                    case SessionEvent.TextAppended text when !json:
                        if (!streamingText.TryGetValue(text.MessageId, out var partial))
                            streamingText[text.MessageId] = partial = new System.Text.StringBuilder();
                        partial.Append(text.Delta); output.Write(text.Delta); output.Flush(); break;
                    case SessionEvent.MessageFinished { FinalMessage.Role: MessageRole.Assistant } message:
                        var final = string.Concat(message.FinalMessage.Blocks.OfType<TextBlock>().Select(b => b.Text));
                        answer.Append(final);
                        if (!json)
                        {
                            var prior = streamingText.Remove(message.MessageId, out var written) ? written.ToString() : "";
                            if (final.StartsWith(prior, StringComparison.Ordinal)) output.Write(final[prior.Length..]);
                            else { output.WriteLine(); output.Write(final); }
                            output.Flush();
                        }
                        break;
                    case SessionEvent.TurnSettled turn: settled.TrySetResult(turn.Outcome); break;
                    case SessionEvent.SessionError error: Fail(error.Message); break;
                }
            }
        };
        Action<ExtensionUiRequest> onRequest = request => requests.Writer.TryWrite(request);
        Action<ExtensionUiRequest> onClosed = request =>
        {
            lock (gate)
                if (!answered.Contains(request)) Fail("Approval request closed or expired before a reply.");
        };
        void MarkResponse(ExtensionUiRequest token, PiExtensionUiResponse response)
        {
            lock (gate)
            {
                answered.Add(token);
                if (response.Kind == PiExtensionUiResponseKind.Cancelled || response.Confirmed == false) denied = true;
            }
        }
        chat.Event += onEvent;
        if (handledSession is not null) handledSession.PromptHandled += Handled;
        if (approval is not null)
        {
            approval.ExtensionUiRequestReceived += onRequest;
            approval.ExtensionUiRequestClosed += onClosed;
            foreach (var pending in approval.PendingExtensionUiRequests) onRequest(pending);
        }
        var responses = RespondAsync();
        var send = SendAsync();
        try
        {
            await Task.WhenAny(settled.Task, failure.Task).WaitAsync(cancellationToken);
            lifetime.Cancel();
            await responses;
            if (failure.Task.IsCompleted) { sending.Cancel(); await StopAsync(); }
            else await send; // Validate acknowledgement even when model terminal events arrived first.
            var error = failure.Task.IsCompleted ? (await failure.Task).Error : denied ? "An approval was rejected or cancelled." : null;
            var outcome = error is not null ? SessionOutcome.Failed : await settled.Task;
            var result = new CliChatResult(outcome, answer.ToString() + string.Concat(streamingText.Values), error, wasHandled,
                failure.Task.IsCompleted ? (await failure.Task).ExitCode : null);
            lock (gate)
            {
                if (json) output.WriteLine(JsonSerializer.Serialize(new
                    { outcome = wasHandled && error is null ? "handled" : outcome.ToString().ToLowerInvariant(), text = result.Text, error }));
                else if (result.Text.Length > 0) output.WriteLine();
                output.Flush();
            }
            if (error is not null) await diagnostics.WriteLineAsync(error);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { await StopAsync(); throw; }
        finally
        {
            lifetime.Cancel(); sending.Cancel(); requests.Writer.TryComplete();
            await responses; await send;
            chat.Event -= onEvent;
            if (handledSession is not null) handledSession.PromptHandled -= Handled;
            if (approval is not null)
            {
                approval.ExtensionUiRequestReceived -= onRequest;
                approval.ExtensionUiRequestClosed -= onClosed;
            }
        }

        async Task SendAsync()
        {
            try { await chat.SendAsync(prompt, sending.Token); }
            catch (OperationCanceledException) when (sending.IsCancellationRequested) { }
            catch (Exception error) { Fail(error.Message); }
        }
        async Task StopAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await chat.StopAsync(timeout.Token); }
            catch (Exception error) { await diagnostics.WriteLineAsync($"Stop failed: {error.Message}"); }
        }
        async Task RespondAsync()
        {
            try
            {
                await foreach (var request in requests.Reader.ReadAllAsync(lifetime.Token))
                {
                    if (!approval!.PendingExtensionUiRequests.Contains(request)) continue;
                    if (autoApprove)
                    {
                        var autoResponse = request.Method switch
                        {
                            "confirm" => PiExtensionUiResponse.Confirm(true),
                            "select" when CliApprovalCommands.Options(request).Length > 0 => PiExtensionUiResponse.WithValue(CliApprovalCommands.Options(request)[0]),
                            "input" or "editor" => PiExtensionUiResponse.WithValue(""),
                            _ => PiExtensionUiResponse.Confirm(true)
                        };
                        MarkResponse(request, autoResponse);
                        await approval.RespondToExtensionUiAsync(request, autoResponse, lifetime.Token);
                        if (!json)
                        {
                            await diagnostics.WriteLineAsync($"[Auto-approved: {request.Method}] {request.Id}");
                            await diagnostics.FlushAsync();
                        }
                        continue;
                    }
                    lock (gate)
                    {
                        if (json) output.WriteLine(JsonSerializer.Serialize(new
                            { type = "approval_request", id = request.Id, method = request.Method, payload = request.Payload }));
                        output.Flush();
                    }
                    if (!json)
                    {
                        await diagnostics.WriteLineAsync($"[Pi extension: {request.Method}] {request.Id}: {request.Payload.GetRawText()}");
                        await diagnostics.WriteLineAsync("approve list | approve accept <id> [value] | approve reject <id>; or enter a direct reply.");
                        await diagnostics.FlushAsync();
                    }
                    using var read = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    var remaining = request.ReceivedAt + request.Timeout - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero) { Fail("Approval request expired."); return; }
                    read.CancelAfter(remaining);
                    void Closed(ExtensionUiRequest closed)
                    {
                        if (!ReferenceEquals(closed, request)) return;
                        read.Cancel();
                    }
                    approval.ExtensionUiRequestClosed += Closed;
                    try
                    {
                        while (approval.PendingExtensionUiRequests.Contains(request))
                        {
                            var line = await input.ReadLineAsync(read.Token);
                            if (line is null)
                            {
                                // Mark failure before a cancellation reply can trigger agent_settled.
                                Fail("Approval needs stdin; EOF cancelled the request. Use -y / --yes to auto-approve in non-interactive/piped mode.");
                                MarkResponse(request, PiExtensionUiResponse.Cancel());
                                await approval.RespondToExtensionUiAsync(request, PiExtensionUiResponse.Cancel(), lifetime.Token);
                                return;
                            }
                            try
                            {
                                if (line.TrimStart().StartsWith("approve ", StringComparison.Ordinal) ||
                                    line.TrimStart().StartsWith("/approve ", StringComparison.Ordinal))
                                {
                                    var args = CliCommandLine.Split(line.TrimStart().TrimStart('/')).Skip(1).ToArray();
                                    await CliApprovalCommands.ExecuteAsync(approval, args, json, output, lifetime.Token, MarkResponse);
                                }
                                else
                                {
                                    var response = json ? JsonReply(line, request) : DirectReply(line, request);
                                    MarkResponse(request, response);
                                    await approval.RespondToExtensionUiAsync(request, response, lifetime.Token);
                                }
                            }
                            catch (InvalidOperationException error) { Fail(error.Message); return; }
                            catch (Exception error) when (error is ArgumentException or JsonException)
                            { Fail(error.Message, 2); return; }
                        }
                    }
                    catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                    {
                        if (!settled.Task.IsCompleted && !failure.Task.IsCompleted)
                            Fail("Approval request closed or expired before a reply.");
                    }
                    finally { approval.ExtensionUiRequestClosed -= Closed; }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { Fail($"Extension response failed: {error.Message}"); }
        }
    }

    private static PiExtensionUiResponse DirectReply(string line, ExtensionUiRequest request) => request.Method switch
    {
        "confirm" => PiExtensionUiResponse.Confirm(line.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)),
        "select" when int.TryParse(line.Trim(), out var number) && number > 0 && number <= CliApprovalCommands.Options(request).Length
            => PiExtensionUiResponse.WithValue(CliApprovalCommands.Options(request)[number - 1]),
        "select" when string.IsNullOrWhiteSpace(line) => PiExtensionUiResponse.Cancel(),
        "select" => CliApprovalCommands.Accept(request, line),
        "input" or "editor" => PiExtensionUiResponse.WithValue(line),
        _ => PiExtensionUiResponse.Cancel()
    };

    private static PiExtensionUiResponse JsonReply(string line, ExtensionUiRequest request)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String || id.GetString() != request.Id)
            throw new ArgumentException("Reply must contain the current approval id.");
        if (root.TryGetProperty("cancel", out var cancel) && cancel.ValueKind == JsonValueKind.True)
            return PiExtensionUiResponse.Cancel();
        if (request.Method == "confirm" && root.TryGetProperty("confirmed", out var confirmed) &&
            confirmed.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return PiExtensionUiResponse.Confirm(confirmed.GetBoolean());
        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
            return CliApprovalCommands.Accept(request, value.GetString());
        throw new ArgumentException("Reply requires confirmed or value (or cancel: true).");
    }
}
