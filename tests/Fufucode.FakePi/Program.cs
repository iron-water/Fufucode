#nullable enable
using System.Text;
using System.Text.Json;

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var reader = new StreamReader(input, new UTF8Encoding(false));
var reverse = new List<(string Id, string Command)>();
var queued = new List<string>();
var commandOrder = new List<string>();
var promptCount = 0;
var streaming = false;
var stateFailure = false;
string? selectedProvider = null;
string? selectedModel = null;
string? thinkingLevel = null;
var availableThinkingLevels = (Option("--thinking-levels") ?? "off,low,medium,high")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
string? forkParentId = null;
string? forkEntryId = null;
var abortWithMessage = false;
var deferAbortSettled = false;
var uiReplies = new List<JsonElement>();
var uiEffectCount = 0;
var cliDialogReplies = new HashSet<string>(StringComparer.Ordinal);
var cliDialogTurn = false;
var cliApprovalTurn = false;
string? pendingExtensionCommandId = null;
string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
var sessionDirectory = Option("--session-dir");
var sessionId = Option("--session-id");
var sessionFile = Option("--session");
var sessionName = Option("--name");
var bridgeMode = Option("--bridge-mode") ?? "missing";
if (bridgeMode == "broken")
{
    await Console.Error.WriteLineAsync("Failed to load extension: fixture startup failure");
    return;
}
var storedEntries = new List<JsonElement>();
if (sessionDirectory is not null)
{
    Directory.CreateDirectory(sessionDirectory);
    if (sessionFile is not null)
    {
        var lines = File.ReadAllLines(sessionFile);
        foreach (var saved in lines)
        {
            using var parsed = JsonDocument.Parse(saved);
            if (parsed.RootElement.TryGetProperty("type", out var kind) && kind.GetString() == PiProtocolStrings.Wire.Session)
                sessionId = parsed.RootElement.GetProperty("id").GetString();
            else storedEntries.Add(parsed.RootElement.Clone());
        }
    }
    else
    {
        sessionId ??= Guid.NewGuid().ToString("N");
        sessionFile = Path.Combine(sessionDirectory, sessionId + ".jsonl");
        File.WriteAllText(sessionFile, JsonSerializer.Serialize(new
        {
            type = PiProtocolStrings.Wire.Session,
            version = PiProtocolStrings.SessionVersion,
            id = sessionId,
            cwd = Environment.CurrentDirectory
        }) + "\n");
    }
}

void SaveEntry(object entry)
{
    var json = JsonSerializer.Serialize(entry);
    using var parsed = JsonDocument.Parse(json);
    storedEntries.Add(parsed.RootElement.Clone());
    File.AppendAllText(sessionFile!, json + "\n");
}

async Task Write(string value, bool split = false)
{
    var bytes = Encoding.UTF8.GetBytes(value + "\r\n");
    if (split)
    {
        foreach (var b in bytes)
        {
            await output.WriteAsync(new[] { b });
            await output.FlushAsync();
        }
    }
    else { await output.WriteAsync(bytes); await output.FlushAsync(); }
}

// One explicit encoder per outbound envelope kind; nested content kinds live in Wire.
var eventContracts = new Dictionary<string, EventContract>(StringComparer.Ordinal)
{
    [PiProtocolStrings.Wire.Response] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.Response, PiEventHandlers.ResponseSchema),
    [PiProtocolStrings.Wire.AgentStart] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.AgentStart, PiEventHandlers.AgentStartSchema),
    [PiProtocolStrings.Wire.AgentEnd] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.AgentEnd, PiEventHandlers.AgentEndSchema),
    [PiProtocolStrings.Wire.AgentSettled] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.AgentSettled, PiEventHandlers.AgentSettledSchema),
    [PiProtocolStrings.Wire.MessageStart] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.MessageStart, PiEventHandlers.MessageStartSchema),
    [PiProtocolStrings.Wire.MessageUpdate] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.MessageUpdate, PiEventHandlers.MessageUpdateSchema),
    [PiProtocolStrings.Wire.MessageEnd] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.MessageEnd, PiEventHandlers.MessageEndSchema),
    [PiProtocolStrings.Wire.AutoRetryStart] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.AutoRetryStart, PiEventHandlers.AutoRetryStartSchema),
    [PiProtocolStrings.Wire.AutoRetryEnd] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.AutoRetryEnd, PiEventHandlers.AutoRetryEndSchema),
    [PiProtocolStrings.Wire.CompactionStart] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.CompactionStart, PiEventHandlers.CompactionStartSchema),
    [PiProtocolStrings.Wire.CompactionEnd] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.CompactionEnd, PiEventHandlers.CompactionEndSchema),
    [PiProtocolStrings.Wire.ExtensionUiRequest] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.ExtensionUiRequest, PiEventHandlers.ExtensionUiRequestSchema),
    [PiProtocolStrings.Wire.ExtensionError] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.ExtensionError, PiEventHandlers.ExtensionErrorSchema),
    [PiProtocolStrings.Wire.ProgressWhileApproval] = new(BaselineAnchors.FixtureOnly, PiEventHandlers.ProgressWhileApproval, PiEventHandlers.ProgressWhileApprovalSchema),
    [PiProtocolStrings.Wire.ToolExecutionStart] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.ToolExecutionStart, PiEventHandlers.ToolExecutionStartSchema),
    [PiProtocolStrings.Wire.ToolExecutionUpdate] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.ToolExecutionUpdate, PiEventHandlers.ToolExecutionUpdateSchema),
    [PiProtocolStrings.Wire.ToolExecutionEnd] = new(BaselineAnchors.CapabilityTable, PiEventHandlers.ToolExecutionEnd, PiEventHandlers.ToolExecutionEndSchema),
    [PiProtocolStrings.Wire.Mystery] = new(BaselineAnchors.FixtureOnly, PiEventHandlers.Mystery, PiEventHandlers.MysterySchema),
    [PiProtocolStrings.Wire.FirstEvent] = new(BaselineAnchors.FixtureOnly, PiEventHandlers.FirstEvent, PiEventHandlers.FirstEventSchema),
    [PiProtocolStrings.Wire.SecondEvent] = new(BaselineAnchors.FixtureOnly, PiEventHandlers.SecondEvent, PiEventHandlers.SecondEventSchema),
};

// Pi 1.0.0 compatible fixture subset. These references are labels in runtime-baseline.md,
// not claims that every injected transport/approval failure was emitted by real Pi.
// Handlers below preserve the original fixture behavior and emission order.
var commandContracts = new Dictionary<string, RpcContract>(StringComparer.Ordinal)
{
    [PiProtocolStrings.Command.ExtensionUiResponse] = new(BaselineAnchors.CapabilityTable, HandleExtensionUiResponse),
    // Explicitly unsupported: historical generic {ok:true} ACK, no model list or queue semantics.
    [PiProtocolStrings.Command.GetAvailableModels] = new(BaselineAnchors.CapabilityTable, HandleGetAvailableModels, IsSimulated: false),
    [PiProtocolStrings.Command.Steer] = new(BaselineAnchors.CapabilityTable, HandleSteer, IsSimulated: false),
    [PiProtocolStrings.Command.FollowUp] = new(BaselineAnchors.CapabilityTable, HandleFollowUp, IsSimulated: false),
    [PiProtocolStrings.Command.GetState] = new(BaselineAnchors.CapabilityTable, HandleGetState),
    [PiProtocolStrings.Command.SetModel] = new(BaselineAnchors.CapabilityTable, HandleSetModel),
    [PiProtocolStrings.Command.SetThinkingLevel] = new(BaselineAnchors.CapabilityTable, HandleSetThinkingLevel),
    [PiProtocolStrings.Command.GetAvailableThinkingLevels] = new(BaselineAnchors.CapabilityTable, HandleGetAvailableThinkingLevels),
    [PiProtocolStrings.Command.Fork] = new(BaselineAnchors.SessionPersistence, HandleFork),
    [PiProtocolStrings.Command.GetCommands] = new(BaselineAnchors.ProjectTrust, HandleGetCommands),
    [PiProtocolStrings.Command.SetSessionName] = new(BaselineAnchors.SessionPersistence, HandleSetSessionName),
    [PiProtocolStrings.Command.GetEntries] = new(BaselineAnchors.SessionPersistence, HandleGetEntries),
    [PiProtocolStrings.Command.ClearQueue] = new(BaselineAnchors.CapabilityTable, HandleClearQueue),
    [PiProtocolStrings.Command.Abort] = new(BaselineAnchors.CapabilityTable, HandleAbort),
    [PiProtocolStrings.Command.Prompt] = new(BaselineAnchors.CapabilityTable, HandlePrompt),
    [PiProtocolStrings.Command.Die] = new(BaselineAnchors.FixtureOnly, HandleDie),
    [PiProtocolStrings.Command.RespondExit] = new(BaselineAnchors.FixtureOnly, HandleRespondExit),
    [PiProtocolStrings.Command.Hang] = new(BaselineAnchors.FixtureOnly, HandleHang),
    [PiProtocolStrings.Command.Malformed] = new(BaselineAnchors.FixtureOnly, HandleMalformed),
    [PiProtocolStrings.Command.NoId] = new(BaselineAnchors.FixtureOnly, HandleNoId),
    [PiProtocolStrings.Command.MissingFields] = new(BaselineAnchors.FixtureOnly, HandleMissingFields),
    [PiProtocolStrings.Command.WrongCommand] = new(BaselineAnchors.FixtureOnly, HandleWrongCommand),
    [PiProtocolStrings.Command.Large] = new(BaselineAnchors.FixtureOnly, HandleLarge),
    [PiProtocolStrings.Command.EmitMulti] = new(BaselineAnchors.FixtureOnly, HandleEmitMulti),
    [PiProtocolStrings.Command.Reverse] = new(BaselineAnchors.FixtureOnly, HandleReverse),
    [PiProtocolStrings.Command.Stderr] = new(BaselineAnchors.FixtureOnly, HandleStderr),
    [PiProtocolStrings.Command.Emit] = new(BaselineAnchors.FixtureOnly, HandleEmit),
};

string EncodeEvent(object envelope)
{
    // Looking up the emitted type makes omissions in the upgrade inventory fail visibly.
    var element = JsonSerializer.SerializeToElement(envelope);
    var kind = element.GetProperty("type").GetString()!;
    if (!eventContracts.TryGetValue(kind, out var contract))
        throw new InvalidOperationException($"Unregistered FakePi event: {kind}");
    return contract.Encode(envelope);
}

Task Emit(object envelope, bool split = false) => Write(EncodeEvent(envelope), split);

while (await reader.ReadLineAsync() is { } line)
{
    using var request = JsonDocument.Parse(line);
    var root = request.RootElement;
    var id = root.GetProperty("id").GetString()!;
    var type = root.GetProperty("type").GetString()!;
    commandOrder.Add(type);
    var handler = commandContracts.TryGetValue(type, out var contract)
        ? contract.Handler : HandleFallback;
    if (!await handler(root, id, type)) return;
}

async Task<bool> HandleExtensionUiResponse(JsonElement root, string id, string type)
{
    uiReplies.Add(root.Clone());
    if (cliDialogTurn && id is "select-1" or "input-1" or "editor-1")
    {
        cliDialogReplies.Add(id);
        if (cliDialogReplies.Count == 3)
        {
            cliDialogTurn = false;
            streaming = false;
            await Emit(new { type = PiProtocolStrings.Wire.AgentSettled });
        }
    }
    if (id == "slow-ui" && pendingExtensionCommandId is { } commandId)
    {
        pendingExtensionCommandId = null;
        await Emit(new { type = PiProtocolStrings.Wire.Response, id = commandId, command = PiProtocolStrings.Command.Prompt, success = true });
    }
    if (id == "approval-1")
    {
        if (root.TryGetProperty("confirmed", out var confirmed) && confirmed.ValueKind == JsonValueKind.True)
        {
            uiEffectCount++;
            if (Option("--approval-path") is { } effectPath) File.WriteAllText(effectPath, "approved");
        }
        streaming = false;
        if (cliApprovalTurn)
        {
            cliApprovalTurn = false;
            await Emit(new { type = PiProtocolStrings.Wire.MessageStart, message = new { role = "assistant", content = Array.Empty<object>() } });
            await Emit(new
            {
                type = PiProtocolStrings.Wire.MessageEnd,
                message = new
                {
                    role = "assistant",
                    stopReason = PiProtocolStrings.StopReason.Stop,
                    content = new[] { new { type = PiProtocolStrings.Wire.Text, text = "Approval processed" } }
                }
            });
        }
        await Emit(new { type = PiProtocolStrings.Wire.AgentSettled });
    }
    return true;
}

async Task<bool> HandleGetState(JsonElement root, string id, string type)
{
    if (stateFailure)
    {

        await Emit(new
        {
            type = PiProtocolStrings.Wire.Response,
            id,
            command = type,
            success = false,
            error = "fixture state query failed"
        });
        return true;

    }

    await Emit(new
    {
        type = PiProtocolStrings.Wire.Response,
        id,
        command = type,
        success = true,
        data = new
        {
            isStreaming = streaming,
            pendingMessageCount = queued.Count,
            promptCount,
            uiReplies = uiReplies.ToArray(),
            uiEffectCount,
            commandOrder = commandOrder.ToArray(),
            sessionFile,
            sessionId,
            sessionName,
            forkParentSessionId = forkParentId,
            forkEntryId,
            startupArgs = args,
            agentDirectory = Environment.GetEnvironmentVariable(PiProtocolStrings.AgentDirectoryVariable),
            model = selectedModel is null ? null : new { provider = selectedProvider, id = selectedModel },
            thinkingLevel,
            proxy = Environment.GetEnvironmentVariable("HTTPS_PROXY"),
            fixtureKeyPresent = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FUFUCODE_TEST_KEY"))
        }
    });
    return true;
}

async Task<bool> HandleSetModel(JsonElement root, string id, string type)
{
    selectedProvider = root.GetProperty("provider").GetString();
    selectedModel = root.GetProperty("modelId").GetString();
    await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
    return true;
}

async Task<bool> HandleSetThinkingLevel(JsonElement root, string id, string type)
{
    var requested = root.GetProperty("level").GetString();
    // Pi clamps a level the model does not offer and still reports success.
    thinkingLevel = availableThinkingLevels.Contains(requested, StringComparer.Ordinal) ? requested : "off";
    await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
    return true;
}

async Task<bool> HandleGetAvailableThinkingLevels(JsonElement root, string id, string type)
{
    await Emit(new
    {
        type = PiProtocolStrings.Wire.Response,
        id,
        command = type,
        success = true,
        data = new { levels = availableThinkingLevels.ToArray() }
    });
    return true;
}

// Mirrors Pi 1.0.0: a fork rebinds the RPC session to a new session id and file in the same directory.
async Task<bool> HandleFork(JsonElement root, string id, string type)
{
    var entryId = root.TryGetProperty("entryId", out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() : null;
    if (string.IsNullOrEmpty(entryId) || entryId == "bad-entry")
    {
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = false,
            error = "Invalid entry ID for forking" });
        return true;
    }
    forkParentId = sessionId;
    forkEntryId = entryId;
    sessionId = Guid.NewGuid().ToString("N");
    sessionFile = sessionDirectory is null ? sessionFile : Path.Combine(sessionDirectory, sessionId + ".jsonl");
    // Pi writes the branch file when the fork is created; the fixture mirrors that so the
    // managed session can register a real path.
    if (sessionFile is { } branch) File.WriteAllText(branch, JsonSerializer.Serialize(new
    { type = PiProtocolStrings.Wire.Session, version = PiProtocolStrings.SessionVersion, id = sessionId, cwd = Environment.CurrentDirectory }) + "\n");
    await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true,
        data = new { text = "", cancelled = false } });
    return true;
}

async Task<bool> HandleGetCommands(JsonElement root, string id, string type)
{
    // Product --no-extensions suppresses implicit /llama and /mcp; explicit bridge remains.
    object[] commands = bridgeMode == "present"
        ? [new { name = PiProtocolStrings.BridgeStatusCommand, source = PiProtocolStrings.ExtensionSource }]
        : [];
    await Emit(new
    {
        type = PiProtocolStrings.Wire.Response,
        id,
        command = type,
        success = true,
        data = new { commands }
    });
    return true;
}

async Task<bool> HandleSetSessionName(JsonElement root, string id, string type)
{
    sessionName = root.GetProperty("name").GetString();
    await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
    return true;
}

async Task<bool> HandleGetEntries(JsonElement root, string id, string type)
{
    var since = root.TryGetProperty("since", out var sinceValue) ? sinceValue.GetString() : null;
    var index = since is null ? -1 : storedEntries.FindIndex(e => e.GetProperty("id").GetString() == since);
    if (since is not null && index < 0)
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = false, error = "unknown since" });
    else
        await Emit(new
        {
            type = PiProtocolStrings.Wire.Response,
            id,
            command = type,
            success = true,
            data = new
            {
                entries = storedEntries.Skip(index + 1).ToArray(),
                leafId = storedEntries.LastOrDefault().ValueKind == JsonValueKind.Object ?
                    storedEntries[^1].GetProperty("id").GetString() : null
            }
        });
    return true;
}

async Task<bool> HandleClearQueue(JsonElement root, string id, string type)
{
    await Emit(new
    {
        type = PiProtocolStrings.Wire.Response,
        id,
        command = type,
        success = true,
        data = new { steering = Array.Empty<string>(), followUp = queued.ToArray() }
    });
    queued.Clear();
    return true;
}

async Task<bool> HandleAbort(JsonElement root, string id, string type)
{
    streaming = false;
    if (deferAbortSettled)
    {
        deferAbortSettled = false;
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        var release = Option("--settle-release") ?? throw new InvalidOperationException("Missing fixture release path");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(release))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Fixture settlement was not released");
            await Task.Delay(10);
        }
        await Emit(new { type = PiProtocolStrings.Wire.AgentSettled });
        return true;
    }
    if (abortWithMessage)
    {
        abortWithMessage = false;
        await Emit(new
        {
            type = PiProtocolStrings.Wire.MessageEnd,
            message = new
            {
                role = "assistant",
                stopReason = PiProtocolStrings.StopReason.Aborted,
                errorMessage = "request aborted",
                content = Array.Empty<object>()
            }
        });
    }
    await Emit(new { type = PiProtocolStrings.Wire.AgentSettled });
    await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
    return true;
}

async Task<bool> HandlePrompt(JsonElement root, string id, string type)
{
    promptCount++;
    var message = root.GetProperty("message").GetString()!;
    if (message == "/slow.ui")
    {
        pendingExtensionCommandId = id;
        await Emit(new
        {
            type = PiProtocolStrings.Wire.ExtensionUiRequest,
            id = "slow-ui",
            method = PiProtocolStrings.UiMethod.Confirm,
            title = "Slow command",
            message = "Awaiting a user response",
            timeout = 5000
        });
        return true;
    }
    if (message == "late_events")
    {
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        await Emit(new { type = PiProtocolStrings.Wire.AgentStart });
        await Emit(new { type = PiProtocolStrings.Wire.AgentSettled });
        await Emit(new { type = PiProtocolStrings.Wire.CompactionStart });
        await Emit(new { type = PiProtocolStrings.Wire.CompactionEnd });
        await Emit(new { type = PiProtocolStrings.Wire.AutoRetryStart });
        await Emit(new { type = PiProtocolStrings.Wire.AutoRetryEnd, success = true });
        return true;
    }
    if (message == "ui_unknown")
    {
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        await Emit(new { type = PiProtocolStrings.Wire.ExtensionUiRequest, id = "unknown-1", method = PiProtocolStrings.UiMethod.FutureMethod });
        return true;
    }
    if (message == "editor_only")
    {
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        await Emit(new
        {
            type = PiProtocolStrings.Wire.ExtensionUiRequest,
            id = "editor-only",
            method = PiProtocolStrings.UiMethod.Editor,
            title = "Edit",
            prefill = "draft"
        });
        return true;
    }
    if (message == "approval_crash")
    {
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        await Emit(new
        {
            type = PiProtocolStrings.Wire.ExtensionUiRequest,
            id = "crash-approval",
            method = PiProtocolStrings.UiMethod.Confirm,
            title = "Approve?",
            message = "Will never execute",
            timeout = 5000
        });
        await Task.Delay(100);
        return false;
    }
    if (message is "approval" or "approval_settled" or "cli_approval")
    {
        cliApprovalTurn = message == "cli_approval";
        streaming = true;
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        await Emit(new { type = PiProtocolStrings.Wire.AgentStart });
        await Emit(new
        {
            type = PiProtocolStrings.Wire.ExtensionUiRequest,
            id = "approval-1",
            method = PiProtocolStrings.UiMethod.Confirm,
            title = "Approve?",
            message = "Exact action: write marker",
            timeout = message == "cli_approval" ? 5000 : 350
        });
        await Emit(new
        {
            type = PiProtocolStrings.Wire.ExtensionUiRequest,
            id = "notice-1",
            method = PiProtocolStrings.UiMethod.Notify,
            message = "Approval pending",
            notifyType = "info"
        });
        await Emit(new
        {
            type = PiProtocolStrings.Wire.ExtensionError,
            extensionPath = "fixture.ts",
            @event = "other_handler",
            error = "example failure"
        });
        await Emit(new { type = PiProtocolStrings.Wire.ProgressWhileApproval, value = 1 });
        if (message == "approval_settled") await Emit(new { type = PiProtocolStrings.Wire.AgentSettled });
        return true;
    }
    if (message is "ui_dialogs" or "cli_dialogs")
    {
        if (message == "cli_dialogs") { cliDialogTurn = true; cliDialogReplies.Clear(); streaming = true; }
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        if (cliDialogTurn) await Emit(new { type = PiProtocolStrings.Wire.AgentStart });
        await Emit(new
        {
            type = PiProtocolStrings.Wire.ExtensionUiRequest,
            id = "select-1",
            method = PiProtocolStrings.UiMethod.Select,
            title = "Pick",
            options = new[] { "Allow", "Block" },
            timeout = 5000
        });
        await Emit(new
        {
            type = PiProtocolStrings.Wire.ExtensionUiRequest,
            id = "input-1",
            method = PiProtocolStrings.UiMethod.Input,
            title = "Input",
            placeholder = "text"
        });
        await Emit(new
        {
            type = PiProtocolStrings.Wire.ExtensionUiRequest,
            id = "editor-1",
            method = PiProtocolStrings.UiMethod.Editor,
            title = "Edit",
            prefill = "draft"
        });
        await Emit(new
        {
            type = PiProtocolStrings.Wire.ExtensionUiRequest,
            id = "status-1",
            method = PiProtocolStrings.UiMethod.Setstatus,
            statusKey = "fixture",
            statusText = "ready"
        });
        return true;
    }
    if (sessionFile is not null && message is "persist_compact" or "persist_crash" or "persist_continue" or "persist_crash_after_assistant")
    {
        var parent = storedEntries.LastOrDefault().ValueKind == JsonValueKind.Object ?
            storedEntries[^1].GetProperty("id").GetString() : null;
        var userId = "e" + (storedEntries.Count + 1);
        SaveEntry(new
        {
            type = PiProtocolStrings.Wire.Message,
            id = userId,
            parentId = parent,
            timestamp = DateTimeOffset.UtcNow,
            message = new { role = "user", content = message }
        });
        if (message == "persist_crash") return false;
        if (message == "persist_crash_after_assistant")
        {
            SaveEntry(new
            {
                type = PiProtocolStrings.Wire.Message,
                id = "e" + (storedEntries.Count + 1),
                parentId = userId,
                timestamp = DateTimeOffset.UtcNow,
                message = new { role = "assistant", content = new[] { new { type = PiProtocolStrings.Wire.Toolcall, id = "pending-tool", name = "bash", arguments = new { command = "echo pending" } } }, stopReason = PiProtocolStrings.StopReason.Tooluse }
            });
            await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
            await Emit(new { type = PiProtocolStrings.Wire.AgentStart });
            await Emit(new { type = PiProtocolStrings.Wire.MessageStart, message = new { role = "assistant", content = Array.Empty<object>() } });
            await Emit(new { type = PiProtocolStrings.Wire.MessageEnd, message = new { role = "assistant", content = new[] { new { type = PiProtocolStrings.Wire.Text, text = "incomplete" } }, stopReason = PiProtocolStrings.StopReason.Tooluse } });
            return false;
        }
        if (message == "persist_compact")
        {
            var beforeId = "e" + (storedEntries.Count + 1);
            SaveEntry(new
            {
                type = PiProtocolStrings.Wire.Message,
                id = beforeId,
                parentId = userId,
                timestamp = DateTimeOffset.UtcNow,
                message = new { role = "assistant", content = new[] { new { type = PiProtocolStrings.Wire.Text, text = "Before compaction" } }, stopReason = PiProtocolStrings.StopReason.Stop }
            });
            var compactId = "e" + (storedEntries.Count + 1);
            SaveEntry(new
            {
                type = PiProtocolStrings.Wire.Compaction,
                id = compactId,
                parentId = beforeId,
                timestamp = DateTimeOffset.UtcNow,
                summary = "Earlier turn summary",
                firstKeptEntryId = compactId
            });
        }
        var assistantParent = storedEntries[^1].GetProperty("id").GetString();
        SaveEntry(new
        {
            type = PiProtocolStrings.Wire.Message,
            id = "e" + (storedEntries.Count + 1),
            parentId = assistantParent,
            timestamp = DateTimeOffset.UtcNow,
            message = new { role = "assistant", content = new[] { new { type = PiProtocolStrings.Wire.Text, text = "Persisted answer" } }, stopReason = PiProtocolStrings.StopReason.Stop }
        });
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        await Emit(new { type = PiProtocolStrings.Wire.AgentStart });
        await Emit(new { type = PiProtocolStrings.Wire.MessageStart, message = new { role = "assistant", content = Array.Empty<object>() } });
        await Emit(new
        {
            type = PiProtocolStrings.Wire.MessageEnd,
            message = new
            {
                role = "assistant",
                stopReason = PiProtocolStrings.StopReason.Stop,
                content = new[] { new { type = PiProtocolStrings.Wire.Text, text = "Persisted answer" } }
            }
        });
        await Emit(new { type = PiProtocolStrings.Wire.AgentSettled });
        return true;
    }
    if (message == "ack_timeout") return true;
    if (message == "crash") return false;
    if (message == "bad_event")
    {
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        await Write("not-json");
        return true;
    }
    if (message == "handled")
    {
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        return true;
    }
    if (message == "state_fail")
    {
        streaming = true;
        stateFailure = true;
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        return true;
    }
    if (message == "error")
    {
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        await Emit(new { type = PiProtocolStrings.Wire.AgentStart });
        await Emit(new { type = PiProtocolStrings.Wire.MessageStart, message = new { role = "assistant", content = Array.Empty<object>() } });
        await Emit(new { type = PiProtocolStrings.Wire.MessageEnd, message = new { role = "assistant", stopReason = PiProtocolStrings.StopReason.Error, errorMessage = "provider failed", content = Array.Empty<object>() } });
        await Emit(new { type = PiProtocolStrings.Wire.AgentSettled });
        return true;
    }
    if (root.TryGetProperty("streamingBehavior", out var behavior) && behavior.GetString() == PiProtocolStrings.FollowUpBehavior)
    {
        queued.Add(message);
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        return true;
    }
    if (message is "hold" or "hold_late_settled")
    {
        streaming = true;
        deferAbortSettled = message == "hold_late_settled";
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        await Emit(new { type = PiProtocolStrings.Wire.AgentStart });
        return true;
    }
    if (message == "hold_aborted")
    {
        streaming = true;
        abortWithMessage = true;
        await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        await Emit(new { type = PiProtocolStrings.Wire.AgentStart });
        await Emit(new { type = PiProtocolStrings.Wire.MessageStart, message = new { role = "assistant", content = Array.Empty<object>() } });
        return true;
    }
    if (message == "stream" || message == "early")
    {
        streaming = true;
        if (message == "stream") await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        await Emit(new { type = PiProtocolStrings.Wire.AgentStart });
        await Emit(new { type = PiProtocolStrings.Wire.MessageStart, message = new { role = "assistant", content = Array.Empty<object>() } });
        await Emit(new { type = PiProtocolStrings.Wire.MessageUpdate, usage = new { input = 10, output = 1, totalTokens = 11 }, assistantMessageEvent = new { type = PiProtocolStrings.Wire.TextStart, contentIndex = 0 } });
        await Emit(new { type = PiProtocolStrings.Wire.MessageUpdate, usage = new { input = 10, output = 1, totalTokens = 11 }, assistantMessageEvent = new { type = PiProtocolStrings.Wire.TextDelta, contentIndex = 0, delta = "Hel" } });
        await Emit(new { type = PiProtocolStrings.Wire.MessageUpdate, usage = new { input = 10, output = 2, totalTokens = 12 }, assistantMessageEvent = new { type = PiProtocolStrings.Wire.ThinkingDelta, contentIndex = 1, delta = "thought" } });
        await Emit(new { type = PiProtocolStrings.Wire.MessageUpdate, usage = new { input = 10, output = 2, totalTokens = 12 }, assistantMessageEvent = new { type = PiProtocolStrings.Wire.ToolcallStart, contentIndex = 2, id = "call-1", toolName = "read" } });
        await Emit(new { type = PiProtocolStrings.Wire.MessageUpdate, usage = new { input = 10, output = 2, totalTokens = 12 }, assistantMessageEvent = new { type = PiProtocolStrings.Wire.ToolcallDelta, contentIndex = 2, delta = "{\"path\":\"a" } });
        await Emit(new { type = PiProtocolStrings.Wire.MessageUpdate, usage = new { input = 10, output = 2, totalTokens = 12 }, assistantMessageEvent = new { type = PiProtocolStrings.Wire.TextEnd, contentIndex = 0, content = "Hello" } });
        await Emit(new { type = PiProtocolStrings.Wire.MessageUpdate, usage = new { input = 10, output = 2, totalTokens = 12 }, assistantMessageEvent = new { type = PiProtocolStrings.Wire.ThinkingEnd, contentIndex = 1, content = "thought-final" } });
        await Emit(new { type = PiProtocolStrings.Wire.MessageUpdate, usage = new { input = 10, output = 2, totalTokens = 12 }, assistantMessageEvent = new { type = PiProtocolStrings.Wire.ToolcallEnd, contentIndex = 2, toolCall = new { type = PiProtocolStrings.Wire.Toolcall, id = "call-1", name = "read", arguments = new { path = "abc" } } } });
        await Emit(new { type = PiProtocolStrings.Wire.MessageEnd, message = new { role = "assistant", stopReason = PiProtocolStrings.StopReason.Stop, usage = new { input = 10, output = 2, totalTokens = 12 }, content = new object[] { new { type = PiProtocolStrings.Wire.Text, text = "Final answer" }, new { type = PiProtocolStrings.Wire.Thinking, thinking = "thought-final" }, new { type = PiProtocolStrings.Wire.Toolcall, id = "call-1", name = "read", arguments = new { path = "abc" } } } } });
        await Emit(new { type = PiProtocolStrings.Wire.ToolExecutionStart, toolCallId = "call-1", toolName = "read", args = new { path = "abc" } });
        await Emit(new { type = PiProtocolStrings.Wire.ToolExecutionUpdate, toolCallId = "call-1", toolName = "read", partialResult = new { content = new[] { new { type = PiProtocolStrings.Wire.Text, text = "partial" } } } });
        await Emit(new { type = PiProtocolStrings.Wire.ToolExecutionEnd, toolCallId = "call-1", toolName = "read", result = new { content = new[] { new { type = PiProtocolStrings.Wire.Text, text = "complete" } } }, isError = false });
        await Emit(new { type = PiProtocolStrings.Wire.AgentEnd, willRetry = true });
        await Task.Delay(150);
        await Emit(new { type = PiProtocolStrings.Wire.AutoRetryStart, attempt = 1 });
        await Emit(new { type = PiProtocolStrings.Wire.AutoRetryEnd, success = true });
        streaming = false;
        await Emit(new { type = PiProtocolStrings.Wire.AgentSettled });
        if (message == "early") await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
        return true;
    }
    await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
    return true;
}

async Task<bool> HandleDie(JsonElement root, string id, string type)
{
    return false;
}

async Task<bool> HandleRespondExit(JsonElement root, string id, string type)
{
    await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true });
    return false;
}

async Task<bool> HandleHang(JsonElement root, string id, string type)
{
    return true;
}

async Task<bool> HandleMalformed(JsonElement root, string id, string type)
{
    await Write("not-json");
    return true;
}

async Task<bool> HandleNoId(JsonElement root, string id, string type)
{
    await Emit(new { type = PiProtocolStrings.Wire.Response, command = "parse", success = false, error = "bad input" });
    return true;
}

async Task<bool> HandleMissingFields(JsonElement root, string id, string type)
{
    await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type });
    return true;
}

async Task<bool> HandleWrongCommand(JsonElement root, string id, string type)
{
    await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = "other", success = true });
    return true;
}

async Task<bool> HandleLarge(JsonElement root, string id, string type)
{
    await Emit(new { type = PiProtocolStrings.Wire.Mystery, text = new string('a', 10000) });
    return true;
}

async Task<bool> HandleEmitMulti(JsonElement root, string id, string type)
{
    var one = EncodeEvent(new { type = PiProtocolStrings.Wire.FirstEvent });
    var two = EncodeEvent(new { type = PiProtocolStrings.Wire.SecondEvent });
    await output.WriteAsync(Encoding.UTF8.GetBytes(one + "\n" + two + "\n"));
    await output.FlushAsync();

    return await HandleFallback(root, id, type);
}

async Task<bool> HandleReverse(JsonElement root, string id, string type)
{
    reverse.Add((id, type));
    if (reverse.Count == 2)
    {
        await Emit(new { type = PiProtocolStrings.Wire.Response, id = reverse[1].Id, command = type, success = true, data = new { order = 2, text = "中\u2028文\u2029字" } }, true);
        await Emit(new { type = PiProtocolStrings.Wire.Response, id = reverse[0].Id, command = type, success = true, data = new { order = 1 } });
        reverse.Clear();
    }
    return true;
}

async Task<bool> HandleStderr(JsonElement root, string id, string type)
{
    await Console.Error.WriteAsync(new string('x', 200000));
    return await HandleFallback(root, id, type);
}

async Task<bool> HandleEmit(JsonElement root, string id, string type)
{
    await Emit(new { type = PiProtocolStrings.Wire.Mystery, text = "中\u2028文\u2029字" }, true);
    return await HandleFallback(root, id, type);
}

async Task<bool> HandleFallback(JsonElement root, string id, string type)
{
    await Emit(new { type = PiProtocolStrings.Wire.Response, id, command = type, success = true, data = new { ok = true } });
    return true;
}


// Unsupported commands deliberately keep the pre-refactor generic ACK unchanged.
// These handlers cannot establish real-Pi model discovery or steering/follow-up capability.
Task<bool> HandleGetAvailableModels(JsonElement root, string id, string type) => HandleFallback(root, id, type);
Task<bool> HandleSteer(JsonElement root, string id, string type) => HandleFallback(root, id, type);
Task<bool> HandleFollowUp(JsonElement root, string id, string type) => HandleFallback(root, id, type);
