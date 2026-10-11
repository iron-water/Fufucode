using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fufucode.Pi;

namespace Fufucode.Core;

/// <summary>Creates and reopens Pi sessions under the application's private data root.</summary>
public sealed class PersistentSessionManager
{
    private readonly string _nodePath;
    private readonly string _cliPath;
    private readonly string? _ompExecutablePath;
    private readonly string? _approvalBridgePath;
    private readonly bool _requireApprovalBridge;
    private readonly AppDataLayout _layout;
    private readonly DesktopMetadataStore _metadata;
    private readonly AppSettingsStore _settings;
    private readonly ICredentialResolver _credentials;
    private readonly object _extensionLock = new();
    private string[] _userExtensionPaths = [];

    public PersistentSessionManager(string nodePath, string cliPath, AppDataLayout? layout = null,
        string? approvalBridgePath = null, bool requireApprovalBridge = true,
        ICredentialResolver? credentialResolver = null, string? ompExecutablePath = null)
    {
        _nodePath = Path.GetFullPath(nodePath);
        _cliPath = Path.GetFullPath(cliPath);
        _ompExecutablePath = ompExecutablePath is null ? null : Path.GetFullPath(ompExecutablePath);
        // The managed runtime layout keeps the built-in bridge beside runtime/.
        // A packaged app may supply its own absolute bridge path explicitly.
        var candidate = approvalBridgePath ?? Path.Combine(Path.GetDirectoryName(_nodePath)!, "..", "..",
            "extensions", "fufucode-bridge");
        candidate = Path.GetFullPath(candidate);
        _approvalBridgePath = File.Exists(Path.Combine(candidate, "index.ts")) ? candidate : null;
        _requireApprovalBridge = requireApprovalBridge;
        _layout = layout ?? new AppDataLayout();
        _metadata = new DesktopMetadataStore(_layout);
        _settings = new AppSettingsStore(_layout);
        _credentials = credentialResolver ?? (OperatingSystem.IsWindows() ?
            new WindowsPasswordVaultCredentialResolver() : new EnvironmentCredentialResolver());
    }

    public AppDataLayout Layout => _layout;
    public DesktopMetadataStore Metadata => _metadata;
    public AppSettingsStore Settings => _settings;

    /// <summary>Explicit user-selected extensions for future Pi processes. Changing this never mutates a running session.</summary>
    public IReadOnlyList<string> UserExtensionPaths
    {
        get { lock (_extensionLock) return _userExtensionPaths.ToArray(); }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var paths = value.Select(path =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                if (!Path.IsPathFullyQualified(path))
                    throw new ArgumentException("Extension paths must be absolute.", nameof(value));
                return Path.GetFullPath(path);
            }).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
            lock (_extensionLock) _userExtensionPaths = paths;
        }
    }

    private string[] ExtensionSnapshot()
    {
        lock (_extensionLock) return _userExtensionPaths.ToArray();
    }

    private string[] ValidateUserExtensions()
    {
        var paths = ExtensionSnapshot();
        foreach (var path in paths)
            if (!File.Exists(path) && !Directory.Exists(path)) throw new UserExtensionLoadException(path);
        return paths;
    }

    /// <summary>Inspect before starting Pi so an RPC session never silently decides trust for the user.</summary>
    public ProjectTrustStatus GetProjectTrustStatus(string projectPath) =>
        InspectTrust(_metadata.GetOrCreateProject(projectPath), ExtensionSnapshot());

    /// <summary>Save an explicit application decision. It takes effect when the next Pi process starts.</summary>
    public ProjectTrustStatus SetProjectTrustDecision(string projectPath, ProjectTrustDecision decision)
    {
        if (!Enum.IsDefined(decision)) throw new ArgumentOutOfRangeException(nameof(decision));
        var project = _metadata.GetOrCreateProject(projectPath);
        _metadata.SetPreference(TrustPreferenceKey(project), decision == ProjectTrustDecision.Trust ? "trust" : "deny");
        return InspectTrust(project, ExtensionSnapshot());
    }

    private ProjectTrustStatus InspectTrust(ProjectRecord project, IReadOnlyList<string> userExtensions)
    {
        var saved = _metadata.GetPreference(TrustPreferenceKey(project));
        var decision = saved switch
        {
            "trust" => ProjectTrustDecision.Trust,
            "deny" => ProjectTrustDecision.DoNotTrust,
            null => (ProjectTrustDecision?)null,
            _ => throw new InvalidOperationException($"Invalid project trust decision for {project.Path}.")
        };
        var resources = ProjectTrustResources.Find(project.Path).Concat(userExtensions.Where(path =>
            IsProjectLocal(path, project.Path))).Distinct(OperatingSystem.IsWindows() ?
            StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        return new ProjectTrustStatus(project.Path, decision, resources);
    }

    private static bool IsProjectLocal(string path, string projectPath) =>
        Path.GetFullPath(path).Equals(projectPath, OperatingSystem.IsWindows() ?
            StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
        AppDataLayout.IsUnderDirectory(path, projectPath);

    private static string TrustPreferenceKey(ProjectRecord project) => "project-trust:" + project.Id;

    private ProjectTrustStatus RequireTrustDecision(ProjectRecord project, IReadOnlyList<string> userExtensions)
    {
        var status = InspectTrust(project, userExtensions);
        if (status.RequiresDecision) throw new ProjectTrustDecisionRequiredException(status);
        if (status.Decision == ProjectTrustDecision.DoNotTrust &&
            userExtensions.FirstOrDefault(path => IsProjectLocal(path, project.Path)) is { } local)
            throw new ProjectExtensionNotTrustedException(status, local);
        return status;
    }

    public async Task<PersistentSession> CreateAsync(string projectPath, string? name = null,
        CancellationToken cancellationToken = default, BackendId backend = BackendId.Pi)
    {
        RequireBackend(backend);
        var project = _metadata.GetOrCreateProject(projectPath);
        var userExtensions = backend == BackendId.Pi ? ValidateUserExtensions() : [];
        var trust = RequireTrustDecision(project, userExtensions);
        var session = _metadata.CreateSession(project, name, backend);
        return await OpenAsync(project, session, trust, userExtensions, createNew: true, cancellationToken);
    }

    public async Task<PersistentSession> ResumeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = _metadata.GetSession(sessionId);
        RequireBackend(session.Backend);
        var project = _metadata.GetProject(session.ProjectId);
        if (!Directory.Exists(project.Path)) throw new DirectoryNotFoundException(project.Path);
        var userExtensions = session.Backend == BackendId.Pi ? ValidateUserExtensions() : [];
        var trust = RequireTrustDecision(project, userExtensions);
        return await OpenAsync(project, session, trust, userExtensions, createNew: false, cancellationToken);
    }

    /// <summary>
    /// Branches the running Pi process at an entry and returns the child session. Pi keeps the same
    /// process; only the managed session identity, lease and metadata move. The parent row is kept.
    /// </summary>
    public async Task<SessionForkResult> ForkAsync(PersistentSession session, string entryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        if (session.Coordinator.Snapshot.Activity != ActivityState.Idle)
            throw new InvalidOperationException("Cannot fork while a run is active.");
        var parent = session.Session;
        if (!BackendCapabilities.For(parent.Backend).ProjectFork)
            throw new NotSupportedException("OMP branching is not part of the verified desktop contract.");
        var parentFile = parent.FilePath;
        var parentDigest = parentFile is { Length: > 0 } && File.Exists(parentFile) ? Sha256(parentFile) : null;

        var response = await session.Coordinator.ForkAsync(entryId, cancellationToken);
        if (!response.Success) throw new PiRpcException($"Pi fork failed: {response.Error}");
        if (response.Data is { ValueKind: JsonValueKind.Object } data &&
            data.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("Pi cancelled the fork.");
        var state = RequireData(await session.Coordinator.QueryStateAsync(cancellationToken), "get_state");
        var piId = RequiredString(state, "sessionId");
        var piFile = Path.GetFullPath(RequiredString(state, "sessionFile"));
        var project = session.Project;
        var sessionDirectory = _layout.ProjectSessionsDirectory(project.Id);
        if (piId == parent.Id || !AppDataLayout.IsUnderDirectory(piFile, sessionDirectory))
            throw new PiRpcException("Pi did not open a forked session.");
        var unchanged = parentDigest is not null && File.Exists(parentFile) && Sha256(parentFile) == parentDigest;

        // Lock handoff: the child lease is taken before the parent's is released, so no other process
        // can claim the branch in between, and the running Pi process is never left unowned.
        var child = _metadata.CreateForkSession(project, parent.Id, entryId, piId);
        SessionLease? childLease = null;
        try
        {
            childLease = SessionLease.Acquire(sessionDirectory, child.Id);
            if (File.Exists(piFile))
            {
                _metadata.UpdateSessionFile(child.Id, piFile);
                child = child with { FilePath = piFile };
            }
            if (response.Data is { } forkData && forkData.TryGetProperty("text", out var editorText) && editorText.ValueKind == JsonValueKind.String)
                _metadata.SetDraft(child.Id, editorText.GetString() ?? "");
            _metadata.SetSessionActive(parent.Id, false);
            var history = new SessionHistory();
            history.ApplyEntriesResponse(RequireData(await session.Coordinator.QueryEntriesAsync(
                cancellationToken: cancellationToken), "get_entries"), replace: true);
            session.Coordinator.RestoreHistory(history, false);
            var adapter = PiChatSessionAdapter.AttachConnected(session.Coordinator, state);
            // Hand Pi ownership to the child; disposing the parent afterwards must not kill the process.
            await session.TransferOwnershipAsync();
            var opened = new PersistentSession(child, project, session.Coordinator, history, _metadata, childLease,
                piFile, adapter, session.Diagnostics);
            return new(opened, parent.Id, parentFile, unchanged, entryId);
        }
        catch
        {
            childLease?.Dispose();
            _metadata.DeleteSession(child);
            throw;
        }
    }

    private static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>Forks the complete persisted branch into an independently owned process and project.</summary>
    public async Task<SessionForkResult> ForkIntoProjectAsync(PersistentSession parent, string projectPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (!BackendCapabilities.For(parent.Session.Backend).ProjectFork)
            throw new NotSupportedException("OMP project forks are not supported; create a new OMP chat instead.");
        if (parent.Coordinator.Snapshot.Activity != ActivityState.Idle)
            throw new InvalidOperationException("Cannot fork while a run is active.");
        await parent.RefreshHistoryAsync(cancellationToken);
        var source = parent.Session.FilePath;
        var owned = _metadata.GetSession(parent.Session.Id);
        if (source is null || !File.Exists(source) || owned.FilePath != source ||
            !AppDataLayout.IsUnderDirectory(source, _layout.ProjectSessionsDirectory(owned.ProjectId)))
            throw new InvalidOperationException("The source session has no owned persisted conversation.");
        var project = _metadata.GetOrCreateProject(projectPath);
        if (project.Id == owned.ProjectId)
            throw new InvalidOperationException("An independent project fork requires a different working directory.");
        var extensions = ValidateUserExtensions();
        var trust = RequireTrustDecision(project, extensions);
        var digest = Sha256(source);
        var leaf = parent.History.Cursor ?? throw new InvalidOperationException("The source branch is empty.");
        var child = _metadata.CreateForkSession(project, owned.Id, leaf, Guid.NewGuid().ToString("N"));
        // Keep a recoverable metadata row if startup fails. Never remove a created worktree or session file.
        var opened = await OpenAsync(project, child, trust, extensions, false, cancellationToken, source);
        if (Sha256(source) != digest)
        {
            await opened.DisposeAsync();
            throw new InvalidOperationException("The source session changed during the project fork.");
        }
        return new(opened, owned.Id, source, true, leaf);
    }

    private async Task<PersistentSession> OpenAsync(ProjectRecord project, SessionRecord session,
        ProjectTrustStatus trust, IReadOnlyList<string> userExtensions, bool createNew,
        CancellationToken cancellationToken, string? forkSource = null)
    {
        var sessionDirectory = session.Backend == BackendId.Pi ? _layout.ProjectSessionsDirectory(project.Id) :
            Path.Combine(_layout.ProjectSessionsDirectory(project.Id), "omp");
        Directory.CreateDirectory(sessionDirectory);
        if (session.FilePath is { } previous && !AppDataLayout.IsUnderDirectory(previous, sessionDirectory))
            throw new InvalidOperationException("Stored Pi session path is outside its project directory.");
        var lease = SessionLease.Acquire(sessionDirectory, session.Id);
        ChatSessionCoordinator? coordinator = null;
        DiagnosticLog? diagnosticLog = null;
        try
        {
            diagnosticLog = new DiagnosticLog(_layout);
            var settings = _settings.Current;
            if (session.Backend == BackendId.Pi) InstallEndpoint(settings.Endpoint);
            else InstallOmpEndpoint(settings.Endpoint);
            // An explicit override prevents saved/global Pi settings from deciding project trust.
            // Extension discovery remains under separate plugin management; explicit -e still loads the bridge.
            var trustFlag = trust.Decision == ProjectTrustDecision.Trust ? "--approve" : "--no-approve";
            var args = session.Backend == BackendId.Pi ?
                new List<string> { _cliPath, "--mode", "rpc", trustFlag, "--no-extensions", "--session-dir", sessionDirectory } :
                new List<string> { "--mode", "rpc", "--no-title", "--no-extensions", "--no-skills", "--no-rules", "--no-lsp",
                    "--approval-mode", "yolo", "--session-dir", sessionDirectory };
            if (session.Backend == BackendId.Omp && settings.SelectedModel is { } ompModel)
                args.AddRange(["--model", ompModel]);
            if (_approvalBridgePath is not null) args.AddRange(["--extension", _approvalBridgePath]);
            foreach (var path in userExtensions)
            {
                if (_approvalBridgePath is not null && path.Equals(_approvalBridgePath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                args.AddRange(["--extension", path]);
            }
            if (createNew && session.Backend == BackendId.Pi && session.Name is { Length: > 0 } name) args.AddRange(["--name", name]);
            if (forkSource is not null)
                args.AddRange(["--fork", forkSource, "--session-id", session.Id]);
            else if (session.FilePath is { } file)
            {
                if (!File.Exists(file)) throw new FileNotFoundException("Pi session file is missing.", file);
                args.AddRange([session.Backend == BackendId.Pi ? "--session" : "--resume", file]);
            }
            else if (session.Backend == BackendId.Pi) args.AddRange(["--session-id", session.Id]);
            else if (!createNew && session.BackendSessionId is not null)
                throw new FileNotFoundException("The OMP session file was not persisted; refusing to start a replacement conversation.");
            var environment = new Dictionary<string, string> { ["PI_CODING_AGENT_DIR"] =
                session.Backend == BackendId.Pi ? _layout.AgentDirectory : Path.Combine(_layout.Root, "omp-agent") };
            if (settings.Endpoint is { } endpoint)
            {
                var key = await _credentials.ResolveAsync(endpoint.ApiKeyEnvironmentVariable, cancellationToken);
                if (string.IsNullOrWhiteSpace(key))
                    throw new InvalidOperationException("The configured endpoint credential is unavailable.");
                environment[endpoint.ApiKeyEnvironmentVariable] = key;
            }
            if (settings.ProxyUrl is { } proxy)
            {
                environment["HTTP_PROXY"] = proxy;
                environment["HTTPS_PROXY"] = proxy;
            }
            var options = new PiRpcOptions(session.Backend == BackendId.Pi ? _nodePath : _ompExecutablePath!, args, project.Path,
                EnvironmentVariables: environment,
                RequireApprovalBridge: _requireApprovalBridge,
                DiagnosticSink: diagnosticLog, Backend: session.Backend);
            coordinator = new ChatSessionCoordinator(options);
            try { await coordinator.StartAsync(cancellationToken); }
            catch (Exception error)
            {
                // Pi 0.87.1 reports a failing explicit extension on stderr before exiting.
                // Identify only an exact configured path; never classify arbitrary startup faults as plugin errors.
                var detail = error.ToString();
                var failed = userExtensions.FirstOrDefault(path => detail.Contains(
                    $"Failed to load extension \"{path}\"", StringComparison.OrdinalIgnoreCase));
                if (failed is not null) throw new UserExtensionLoadException(failed, error);
                throw;
            }
            var state = await coordinator.QueryStateAsync(cancellationToken);
            var data = RequireData(state, "get_state");
            if (createNew) data = await ApplyModelPreferenceAsync(coordinator, settings, data, cancellationToken);
            var piId = RequiredString(data, "sessionId");
            var piFile = Path.GetFullPath(RequiredString(data, "sessionFile"));
            if ((session.Backend == BackendId.Pi ? piId != session.Id : session.BackendSessionId is not null && piId != session.BackendSessionId) ||
                !AppDataLayout.IsUnderDirectory(piFile, sessionDirectory) ||
                (session.FilePath is not null && !Path.GetFullPath(session.FilePath).Equals(piFile,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                throw new PiRpcException("Pi opened a different session than requested.");
            _metadata.BindBackendSession(session.Id, piId);
            session = session with { BackendSessionId = piId };
            if (File.Exists(piFile)) _metadata.UpdateSessionFile(session.Id, piFile);
            var response = await coordinator.QueryEntriesAsync(cancellationToken: cancellationToken);
            var history = new SessionHistory();
            history.ApplyEntriesResponse(RequireData(response, "get_entries"), replace: true);
            // Preserve an interrupted run across crashes; an incomplete last user entry is also interrupted.
            var lastMessage = history.CurrentBranchMessages.LastOrDefault();
            var interrupted = session.WasActive || lastMessage?.Role == "user" ||
                lastMessage is { Role: "assistant", StopReason: "aborted" };
            coordinator.RestoreHistory(history, interrupted);
            _metadata.SetSessionActive(session.Id, false);
            if (createNew && session.Backend == BackendId.Omp && session.Name is { Length: > 0 } ompName)
            {
                var rename = await coordinator.SetSessionNameAsync(ompName, cancellationToken);
                if (!rename.Success) throw new PiRpcException($"OMP refused session name: {rename.Error}");
                if (File.Exists(piFile)) _metadata.UpdateSessionFile(session.Id, piFile);
            }
            return new PersistentSession(session, project, coordinator, history, _metadata, lease, piFile,
                PiChatSessionAdapter.AttachConnected(coordinator, data), diagnosticLog);
        }
        catch
        {
            if (coordinator is not null) await coordinator.DisposeAsync();
            if (diagnosticLog is not null) await diagnosticLog.DisposeAsync();
            lease.Dispose();
            throw;
        }
    }

    private void InstallEndpoint(CompatibleEndpoint? endpoint)
    {
        var target = Path.Combine(_layout.AgentDirectory, "models.json");
        var ownership = Path.Combine(_layout.AgentDirectory, "fufucode-provider.txt");
        if (endpoint is null && !File.Exists(ownership)) return;
        Directory.CreateDirectory(_layout.AgentDirectory);
        JsonObject root;
        if (File.Exists(target))
        {
            root = JsonNode.Parse(File.ReadAllText(target)) as JsonObject ??
                throw new InvalidOperationException("Existing Pi models.json is invalid; refusing to overwrite it.");
        }
        else root = new JsonObject();
        var providers = root["providers"] as JsonObject;
        if (root["providers"] is not null && providers is null)
            throw new InvalidOperationException("Existing Pi providers configuration is invalid.");
        if (providers is null) root["providers"] = providers = new JsonObject();
        var previous = File.Exists(ownership) ? File.ReadAllText(ownership) : null;
        if (previous is not null)
        {
            if (endpoint is null || previous != endpoint.ProviderName) providers.Remove(previous);
        }
        if (endpoint is not null)
        {
            if (providers.ContainsKey(endpoint.ProviderName) && previous != endpoint.ProviderName)
                throw new InvalidOperationException("The configured provider already exists in Pi models.json.");
            providers[endpoint.ProviderName] = JsonSerializer.SerializeToNode(new
            {
                baseUrl = endpoint.BaseUrl,
                api = "openai-completions",
                apiKey = "$" + endpoint.ApiKeyEnvironmentVariable,
                models = new[] { new { id = endpoint.ModelId } }
            });
        }
        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        var markerTemp = ownership + ".tmp-" + Guid.NewGuid().ToString("N");
        var originalModels = File.Exists(target) ? File.ReadAllBytes(target) : null;
        try
        {
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (endpoint is not null) File.WriteAllText(markerTemp, endpoint.ProviderName);
            File.Move(temp, target, overwrite: true);
            try
            {
                if (endpoint is null) File.Delete(ownership);
                else File.Move(markerTemp, ownership, overwrite: true);
            }
            catch
            {
                // Keep the previous provider ownership and models file paired on a failed marker update.
                if (originalModels is null) File.Delete(target);
                else File.WriteAllBytes(target, originalModels);
                throw;
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (File.Exists(markerTemp)) File.Delete(markerTemp);
        }
    }

    private void RequireBackend(BackendId backend)
    {
        _ = BackendCapabilities.For(backend);
        if (backend == BackendId.Pi && (!File.Exists(_nodePath) || !File.Exists(_cliPath)))
            throw new FileNotFoundException("Pi runtime missing. Configure --pi-node and --pi-cli with the managed runtime paths.");
        if (backend == BackendId.Omp && (_ompExecutablePath is null || !File.Exists(_ompExecutablePath)))
            throw new FileNotFoundException("OMP runtime missing. Configure --omp-exe with the verified OMP 18.8.5 executable.", _ompExecutablePath);
    }

    private void InstallOmpEndpoint(CompatibleEndpoint? endpoint)
    {
        var directory = Path.Combine(_layout.Root, "omp-agent");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "models.yml");
        // This isolated file is owned by the desktop; never edit the user's global OMP config.
        // JSON is valid YAML and keeps environment references as data.
        var root = new JsonObject { ["providers"] = new JsonObject() };
        if (endpoint is not null)
            ((JsonObject)root["providers"]!)[endpoint.ProviderName] = JsonSerializer.SerializeToNode(new
            {
                baseUrl = endpoint.BaseUrl, api = "openai-completions", apiKey = endpoint.ApiKeyEnvironmentVariable,
                models = new[] { new { id = endpoint.ModelId } }
            });
        if (File.Exists(target) && !File.Exists(target + ".fufucode"))
            throw new InvalidOperationException("OMP models.yml is not desktop-owned; refusing to overwrite it.");
        File.WriteAllText(target + ".tmp", root.ToJsonString());
        File.Move(target + ".tmp", target, overwrite: true);
        File.WriteAllText(target + ".fufucode", "D1");
    }

    private static async Task<JsonElement> ApplyModelPreferenceAsync(ChatSessionCoordinator coordinator, AppSettings settings,
        JsonElement initialState, CancellationToken cancellationToken)
    {
        if (settings.SelectedModel is null && settings.ThinkingLevel is null) return initialState;
        ThinkingLevelAvailability? availableThinkingLevels = null;
        if (settings.SelectedModel is { } selected)
        {
            var separator = selected.IndexOf('/');
            if (separator < 1 || separator == selected.Length - 1)
                throw new InvalidOperationException("Selected model must have provider/modelId form.");
            var response = await coordinator.SetModelAsync(selected[..separator], selected[(separator + 1)..], cancellationToken);
            if (!response.Success) throw new PiRpcException($"Pi set_model failed: {response.Error}");
        }
        if (settings.ThinkingLevel is { } level)
        {
            var response = await coordinator.SetThinkingLevelAsync(level, cancellationToken);
            if (!response.Success) throw new PiRpcException($"Pi set_thinking_level failed: {response.Error}");
            // Pi clamps a level the current model does not support and still reports success.
            // Ask what is actually offered before setting, so the refusal names the real cause.
            availableThinkingLevels = await TryReadThinkingLevelsAsync(coordinator, cancellationToken);
        }
        var refreshed = RequireData(await coordinator.QueryStateAsync(cancellationToken), "get_state");
        if (settings.SelectedModel is { } model &&
            (!refreshed.TryGetProperty("model", out var actual) ||
             actual.ValueKind != JsonValueKind.Object ||
             !actual.TryGetProperty("provider", out var provider) ||
             !actual.TryGetProperty("id", out var id) ||
             model != provider.GetString() + "/" + id.GetString()))
            throw new PiRpcException("Pi did not select the requested model.");
        if (settings.ThinkingLevel is { } thinking && ThinkingLevelAvailability.ReadCurrentLevel(refreshed) != thinking)
        {
            // A model without reasoning support silently clamps; name the supported levels so the
            // caller can tell "rejected" from "not offered by this model".
            var known = availableThinkingLevels is { IsEmpty: false } listed ? listed : null;
            var offered = known is null ? "unknown" : string.Join(", ", known.Levels);
            throw new PiRpcException(known is not null && !known.Supports(thinking)
                ? $"Pi does not offer the thinking level '{thinking}' for this model; available levels: {offered}."
                : $"Pi did not select the requested thinking level '{thinking}'; available levels: {offered}.");
        }
        return refreshed;
    }

    /// <summary>A missing or failing capability query must not block a session from opening.</summary>
    private static async Task<ThinkingLevelAvailability?> TryReadThinkingLevelsAsync(ChatSessionCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        try { return await coordinator.QueryThinkingLevelsAsync(cancellationToken); }
        catch (Exception) { return null; }
    }

    internal static JsonElement RequireData(PiRpcResponse response, string command)
    {
        if (!response.Success || response.Data is not { ValueKind: JsonValueKind.Object } data)
            throw new PiRpcException($"Pi {command} failed: {response.Error}");
        return data;
    }

    private static string RequiredString(JsonElement data, string name) =>
        data.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() is { Length: > 0 } value
            ? value : throw new PiRpcException($"get_state lacks {name}.");
}

/// <summary>Ready-to-use session. History is loaded before this object becomes available to callers.</summary>
public sealed class PersistentSession : IAsyncDisposable
{
    private readonly DesktopMetadataStore _metadata;
    private readonly SessionLease _lease;
    private readonly string _sessionFile;
    private readonly PiChatSessionAdapter _chat;
    private readonly DiagnosticLog _diagnosticLog;
    private bool _fileRecorded;
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private readonly Channel<(bool Active, bool Faulted)> _metadataChanges =
        Channel.CreateUnbounded<(bool, bool)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _metadataWriter;
    private readonly object _metadataLock = new();
    private readonly object _enqueueLock = new();
    private bool _observedActive;
    private bool _recordedActive;
    private bool _disposed;
    private bool _transferred;

    internal PersistentSession(SessionRecord session, ProjectRecord project, ChatSessionCoordinator coordinator,
        SessionHistory history, DesktopMetadataStore metadata, SessionLease lease, string sessionFile,
        PiChatSessionAdapter chat, DiagnosticLog diagnosticLog)
    {
        Session = File.Exists(sessionFile) ? session with { FilePath = sessionFile } : session;
        Project = project;
        Coordinator = coordinator;
        History = history;
        _metadata = metadata;
        _lease = lease;
        _sessionFile = sessionFile;
        _chat = chat;
        _diagnosticLog = diagnosticLog;
        _fileRecorded = File.Exists(sessionFile);
        _metadataWriter = WriteMetadataAsync();
        Coordinator.SnapshotChanged += OnSnapshot;
    }

    public SessionRecord Session { get; private set; }
    public ProjectRecord Project { get; }
    public ChatSessionCoordinator Coordinator { get; }
    public SessionHistory History { get; }
    public Fufucode.App.Abstractions.IChatSession Chat => _chat;

    /// <summary>The Pi process, its diagnostics and its lease are shared, so a fork hands them over.</summary>
    internal DiagnosticLog Diagnostics => _diagnosticLog;
    public string DiagnosticFilePath => _diagnosticLog.FilePath;

    internal void ApplyConfirmedState(JsonElement state) => _chat.ReadModel(state);

    /// <summary>
    /// Ends this object's bookkeeping without touching the Pi process, the diagnostics log or the lease.
    /// Used by fork, where the child session inherits the same running process.
    /// </summary>
    internal async Task TransferOwnershipAsync()
    {
        _transferred = true;
        Coordinator.SnapshotChanged -= OnSnapshot;
        _metadataChanges.Writer.TryComplete();
        await _metadataWriter;
        RecordFileIfCreated();
        _lease.Dispose();
    }

    public async Task RenameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var response = await Coordinator.SetSessionNameAsync(name, cancellationToken);
        if (!response.Success) throw new PiRpcException($"Pi refused session name: {response.Error}");
        _metadata.SetSessionName(Session.Id, name);
        Session = Session with { Name = name };
        RecordFileIfCreated();
    }

    private void OnSnapshot(SessionSnapshot snapshot)
    {
        var active = snapshot.Activity is ActivityState.Running or ActivityState.Compacting or ActivityState.Retrying or ActivityState.Cancelling;
        lock (_enqueueLock)
        {
            if (active == _observedActive && snapshot.Connection != ConnectionState.Faulted) return;
            _observedActive = active;
            _metadataChanges.Writer.TryWrite((active, snapshot.Connection == ConnectionState.Faulted));
        }
    }

    private async Task WriteMetadataAsync()
    {
        await foreach (var (active, faulted) in _metadataChanges.Reader.ReadAllAsync())
        {
            // SQLite and filesystem checks run off the RPC event pump.
            lock (_metadataLock)
            {
                RecordFileIfCreated();
                // A crashed process may have emitted entries without agent_settled.
                if (faulted && _recordedActive) continue;
                if (active == _recordedActive) continue;
                _metadata.SetSessionActive(Session.Id, active);
                _recordedActive = active;
            }
        }
    }

    private void RecordFileIfCreated()
    {
        lock (_metadataLock)
        {
            if (_fileRecorded || !File.Exists(_sessionFile)) return;
            _metadata.UpdateSessionFile(Session.Id, _sessionFile);
            Session = Session with { FilePath = _sessionFile };
            _fileRecorded = true;
        }
    }

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _historyGate.WaitAsync(cancellationToken);
        try
        {
            if (Coordinator.Snapshot.Activity != ActivityState.Idle)
                throw new InvalidOperationException("Cannot refresh persisted history during a run.");
            var result = await Coordinator.QueryEntriesAsync(History.Cursor, cancellationToken);
            History.ApplyEntriesResponse(PersistentSessionManager.RequireData(result, "get_entries"));
            RecordFileIfCreated();
            Coordinator.RestoreHistory(History, Coordinator.Snapshot.LastResult == TaskResult.Interrupted);
        }
        finally { _historyGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_transferred)
        {
            // Fork moved the Pi process, diagnostics and lease to the child session.
            _historyGate.Dispose();
            return;
        }
        Coordinator.SnapshotChanged -= OnSnapshot;
        try
        {
            await _chat.DisposeAsync();
            _metadataChanges.Writer.TryComplete();
            await _metadataWriter;
            RecordFileIfCreated();
        }
        finally
        {
            await _diagnosticLog.DisposeAsync();
            _historyGate.Dispose();
            _lease.Dispose();
        }
    }
}

/// <summary>Outcome of a fork. ParentFileUnchanged is evidence, not a guarantee enforced by Pi.</summary>
public sealed record SessionForkResult(PersistentSession Session, string ParentSessionId, string? ParentFilePath,
    bool ParentFileUnchanged, string ForkEntryId);
