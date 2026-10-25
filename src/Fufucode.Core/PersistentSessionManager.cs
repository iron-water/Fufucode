using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Core;

/// <summary>Creates and reopens Pi sessions under the application's private data root.</summary>
public sealed class PersistentSessionManager
{
    private readonly string _nodePath;
    private readonly string _cliPath;
    private readonly string? _ompExecutablePath;
    private readonly string? _claudeExecutablePath;
    private readonly string? _codexExecutablePath;
    private readonly string? _dshExecutablePath;
    private readonly string? _approvalBridgePath;
    private readonly bool _requireApprovalBridge;
    private readonly AppDataLayout _layout;
    private readonly DesktopMetadataStore _metadata;
    private readonly AppSettingsStore _settings;
    private readonly ICredentialResolver _credentials;
    private readonly Func<BackendProcessSpec, IRpcClientFactory?>? _clientFactoryFactory;
    private readonly object _extensionLock = new();
    private string[] _userExtensionPaths = [];

    public PersistentSessionManager(string nodePath, string cliPath, AppDataLayout? layout = null,
        string? approvalBridgePath = null, bool requireApprovalBridge = true,
        ICredentialResolver? credentialResolver = null, string? ompExecutablePath = null,
        Func<BackendProcessSpec, IRpcClientFactory?>? clientFactoryFactory = null,
        string? claudeExecutablePath = null,
        string? codexExecutablePath = null,
        string? dshExecutablePath = null)
    {
        _nodePath = string.IsNullOrWhiteSpace(nodePath) ? string.Empty : Path.GetFullPath(nodePath);
        _cliPath = string.IsNullOrWhiteSpace(cliPath) ? string.Empty : Path.GetFullPath(cliPath);
        var runtimeLayout = layout ?? new AppDataLayout();
        // 五个后端同一解析顺序：显式参数 > 用户按需安装的 runtime/<id> > 系统安装。
        _ompExecutablePath = ompExecutablePath is null
            ? ResolveFromCatalog(BackendId.Omp, runtimeLayout)
            : (string.IsNullOrWhiteSpace(ompExecutablePath) ? null : Path.GetFullPath(ompExecutablePath));
        _claudeExecutablePath = claudeExecutablePath is null
            ? ResolveClaudeExecutable(runtimeLayout)
            : (string.IsNullOrWhiteSpace(claudeExecutablePath) ? null : Path.GetFullPath(claudeExecutablePath));
        _codexExecutablePath = codexExecutablePath is null
            ? ResolveCodexExecutable(runtimeLayout)
            : (string.IsNullOrWhiteSpace(codexExecutablePath) ? null : Path.GetFullPath(codexExecutablePath));
        _dshExecutablePath = dshExecutablePath is null
            ? ResolveDshExecutable(runtimeLayout)
            : (string.IsNullOrWhiteSpace(dshExecutablePath) ? null : Path.GetFullPath(dshExecutablePath));
        // 只有显式给出 Pi 运行时路径时才探测审批桥；FUMO 默认路径不触碰任何 Pi 文件。
        _approvalBridgePath = null;
        if (_nodePath.Length > 0)
        {
            var candidate = approvalBridgePath ?? Path.Combine(Path.GetDirectoryName(_nodePath)!, "..", "..",
                "extensions", "fufucode-bridge");
            candidate = Path.GetFullPath(candidate);
            if (File.Exists(Path.Combine(candidate, "index.ts"))) _approvalBridgePath = candidate;
        }
        _requireApprovalBridge = requireApprovalBridge;
        _layout = layout ?? new AppDataLayout();
        _metadata = new DesktopMetadataStore(_layout);
        _settings = new AppSettingsStore(_layout);
        _credentials = credentialResolver ?? (OperatingSystem.IsWindows() ?
            new WindowsPasswordVaultCredentialResolver() : new EnvironmentCredentialResolver());
        _clientFactoryFactory = clientFactoryFactory;
    }

    public AppDataLayout Layout => _layout;
    public DesktopMetadataStore Metadata => _metadata;
    public AppSettingsStore Settings => _settings;

    /// <summary>
    /// 组合根提供的插件工具注册表（Browser Use / Seep 等）。FUMO 会话打开时读取，
    /// 安装/卸载插件后下一次请求立即反映；未接线时只暴露内置工具。
    /// </summary>
    public Func<IReadOnlyList<IPureToolDispatcher>>? NativePluginToolProviders { get; set; }

    /// <summary>组合根提供的插件人格片段（如 Seep 作战协议）；未接线时为 null。</summary>
    public IPluginSystemPromptProvider? NativeSystemPromptProvider { get; set; }

    /// <summary>全局默认后端（backend.json）；缺省 FUMO 原生内核。</summary>
    public BackendId PreferredBackend => BackendCapabilities.FromEngineId(
        new Backend.BackendPreferenceStore(_layout).DefaultBackend) ?? BackendId.NativeCSharp;

    /// <summary>检查指定后端对应的可执行文件与运行时是否存在于当前机器上。</summary>
    public bool IsBackendAvailable(BackendId backend) => backend switch
    {
        BackendId.Pi => File.Exists(_nodePath) && File.Exists(_cliPath),
        BackendId.Omp => _ompExecutablePath is not null && File.Exists(_ompExecutablePath),
        BackendId.Codex => _codexExecutablePath is not null && File.Exists(_codexExecutablePath),
        BackendId.Dsh => _dshExecutablePath is not null && File.Exists(_dshExecutablePath),
        BackendId.Claude => _claudeExecutablePath is not null && File.Exists(_claudeExecutablePath),
        BackendId.NativeCSharp => true,
        _ => false,
    };

    /// <summary>
    /// 自动在系统与常见全局包管理器路径中查找官方 Claude Code CLI 可执行文件。
    /// 未找到时返回 null，调用方据此判断该后端不可用，不伪造可用性。
    /// </summary>
    /// <summary>
    /// Claude Code CLI：显式参数 > 系统安装 > 用户按需安装的 runtime/claude（统一运行时表）。
    /// </summary>
    public static string? ResolveClaudeExecutable(AppDataLayout? layout = null) =>
        ResolveFromCatalog(BackendId.Claude, layout);

    /// <summary>
    /// 自动在系统与常见全局路径中查找官方 Codex CLI 可执行文件。
    /// </summary>
    /// <summary>Codex CLI：显式参数 > 系统安装 > runtime/codex（统一运行时表）。</summary>
    public static string? ResolveCodexExecutable(AppDataLayout? layout = null) =>
        ResolveFromCatalog(BackendId.Codex, layout);

    /// <summary>
    /// 自动在系统与常见全局路径中查找官方 DeepSeek Harness (DSH) CLI 可执行文件。
    /// </summary>
    /// <summary>DSH CLI：显式参数 > 系统安装 > runtime/dsh（统一运行时表）。</summary>
    public static string? ResolveDshExecutable(AppDataLayout? layout = null) =>
        ResolveFromCatalog(BackendId.Dsh, layout);

    /// <summary>统一运行时探测：所有后端走 ToolchainCatalog，不再各自维护查找路径。</summary>
    private static string? ResolveFromCatalog(BackendId backend, AppDataLayout? layout)
    {
        var detection = Runtimes.ToolchainCatalog.Detect(layout ?? new AppDataLayout(),
            BackendCapabilities.StorageId(backend));
        // 与组合根一致：用户安装到 Fufucode 的运行时优先，系统安装兜底。
        return detection.ManagedPath ?? detection.SystemPath;
    }

    private static string? FindBinaryOnPath(string[] binaryNames)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null) return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim();
            if (string.IsNullOrEmpty(trimmed) || !Directory.Exists(trimmed)) continue;
            foreach (var name in binaryNames)
            {
                try
                {
                    var candidate = Path.Combine(trimmed, name);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch { }
            }
        }

        return null;
    }

    private static string ResolveDotnetHost()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var standard = Path.Combine(pf, "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (File.Exists(standard)) return standard;
        return "dotnet";
    }

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
        CancellationToken cancellationToken = default, BackendId? backend = null)
    {
        // 不指定时使用全局默认后端；默认安装下是 FUMO，而不是硬编码 Pi。
        var resolved = backend ?? PreferredBackend;
        RequireBackend(resolved);
        var project = _metadata.GetOrCreateProject(projectPath);
        var userExtensions = resolved == BackendId.Pi ? ValidateUserExtensions() : [];
        var trust = RequireTrustDecision(project, userExtensions);
        var session = _metadata.CreateSession(project, name, resolved);
        return await OpenAsync(project, session, trust, userExtensions, createNew: true, cancellationToken);
    }

    public async Task<PersistentSession> ResumeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        Diagnostics.StartupProbe.Mark("resume.begin", sessionId);
        var session = _metadata.GetSession(sessionId);
        Diagnostics.StartupProbe.Mark("resume.metadata", session.Backend.ToString());
        RequireBackend(session.Backend);
        var project = _metadata.GetProject(session.ProjectId);
        if (!Directory.Exists(project.Path)) throw new DirectoryNotFoundException(project.Path);
        var userExtensions = session.Backend == BackendId.Pi ? ValidateUserExtensions() : [];
        Diagnostics.StartupProbe.Mark("resume.before-trust", project.Path);
        var trust = RequireTrustDecision(project, userExtensions);
        Diagnostics.StartupProbe.Mark("resume.after-trust", trust.Decision?.ToString() ?? "no-decision");
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
        if (session.Session.Backend == BackendId.NativeCSharp)
            return await ForkNativeAsync(session, entryId, cancellationToken).ConfigureAwait(false);
        var parent = session.Session;
        if (!BackendCapabilities.For(parent.Backend).ProjectFork)
            throw new NotSupportedException("OMP branching is not part of the verified desktop contract.");
        var parentFile = parent.FilePath;
        var parentDigest = parentFile is { Length: > 0 } && File.Exists(parentFile) ? Sha256(parentFile) : null;

        var response = await session.Coordinator.ForkAsync(entryId, cancellationToken);
        if (!response.Success) throw new RpcException($"Pi fork failed: {response.Error}");
        if (response.Data is { ValueKind: JsonValueKind.Object } data &&
            data.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("Pi cancelled the fork.");
        var state = RequireData(await session.Coordinator.QueryStateAsync(cancellationToken), "get_state");
        var piId = RequiredString(state, "sessionId");
        var piFile = Path.GetFullPath(RequiredString(state, "sessionFile"));
        var project = session.Project;
        var sessionDirectory = _layout.ProjectSessionsDirectory(project.Id);
        if (piId == parent.Id || !AppDataLayout.IsUnderDirectory(piFile, sessionDirectory))
            throw new RpcException("Pi did not open a forked session.");
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
            var adapter = PiChatSessionAdapter.AttachConnected((ChatSessionCoordinator)session.Coordinator, state);
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

    /// <summary>
    /// FUMO 分叉：把父会话截至指定用户条目的历史复制成新会话文件。
    /// 与 Pi 不同，FUMO 的上下文就是本地 JSON，因此分叉不需要重启进程：
    /// 新建会话行 → 写入截断后的历史 → 按同一路径重新打开。
    /// </summary>
    private async Task<SessionForkResult> ForkNativeAsync(PersistentSession session, string entryId,
        CancellationToken cancellationToken)
    {
        var parent = session.Session;
        var project = session.Project;
        var messages = session.Chat is Backend.NativeCSharpChatSession native
            ? native.GetHistoryMessages()
            : LoadNativeMessages(NativeSessionFactory.SessionFilePath(_layout, project.Id, parent.Id))?.Messages;
        if (messages is not { Count: > 0 })
            throw new InvalidOperationException("会话还没有可分支的消息。");
        if (!entryId.StartsWith("entry_", StringComparison.Ordinal) ||
            !int.TryParse(entryId.AsSpan("entry_".Length), out var index) || index < 0 || index >= messages.Count)
            throw new ArgumentOutOfRangeException(nameof(entryId), "分叉点不在当前会话历史中。");
        if (messages[index].Role != PureAgentRole.User)
            throw new InvalidOperationException("只能在用户消息处分叉。");

        // 截断到分叉点之前：用户消息本身作为新会话的第一条输入保留。
        var branch = messages.Take(index + 1).ToList();
        var child = _metadata.CreateForkSession(project, parent.Id, entryId);
        var childFile = NativeSessionFactory.SessionFilePath(_layout, project.Id, child.Id);
        try
        {
            var modelId = session.Chat.CurrentModel;
            AtomicJsonFile.Write(childFile, writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 2);
                writer.WritePropertyName("messages");
                JsonSerializer.Serialize(writer, branch);
                if (modelId is null) writer.WriteNull("modelId");
                else writer.WriteString("modelId", modelId);
                writer.WriteEndObject();
            });
            _metadata.UpdateSessionFile(child.Id, childFile);
            _metadata.SetSessionActive(parent.Id, false);
            var opened = await ResumeAsync(child.Id, cancellationToken).ConfigureAwait(false);
            return new SessionForkResult(opened, parent.Id, parent.FilePath, true, entryId);
        }
        catch
        {
            try { if (File.Exists(childFile)) File.Delete(childFile); } catch (IOException) { }
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
        // 是否支持“分叉到另一个项目”由能力表声明，不在调用点写死后端名单。
        if (!BackendCapabilities.For(parent.Session.Backend).ProjectFork)
            throw new NotSupportedException("此后端不支持项目分叉；请新建会话。");
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
        if (parent.Session.Backend == BackendId.NativeCSharp)
        {
            // FUMO 没有外部进程可“--fork”：把源会话文件复制成子会话文件，再按普通会话打开。
            var target = NativeSessionFactory.SessionFilePath(_layout, project.Id, child.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
            _metadata.UpdateSessionFile(child.Id, target);
            child = child with { FilePath = target };
            var openedNative = await OpenAsync(project, child, trust, extensions, false, cancellationToken);
            return new(openedNative, owned.Id, source, true, leaf);
        }
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
        var sessionDirectory = session.Backend switch
        {
            BackendId.Omp => Path.Combine(_layout.ProjectSessionsDirectory(project.Id), "omp"),
            BackendId.NativeCSharp => Path.Combine(_layout.ProjectSessionsDirectory(project.Id), "fumo"),
            BackendId.Claude => Path.Combine(_layout.ProjectSessionsDirectory(project.Id), "claude"),
            BackendId.Codex => Path.Combine(_layout.ProjectSessionsDirectory(project.Id), "codex"),
            BackendId.Dsh => Path.Combine(_layout.ProjectSessionsDirectory(project.Id), "dsh"),
            _ => _layout.ProjectSessionsDirectory(project.Id),
        };
        Directory.CreateDirectory(sessionDirectory);
        Diagnostics.StartupProbe.Mark("open.session-dir", sessionDirectory);
        if (session.FilePath is { } previous && !AppDataLayout.IsUnderDirectory(previous, sessionDirectory))
            throw new InvalidOperationException("Stored session path is outside its project directory.");
        Diagnostics.StartupProbe.Mark("open.lease.begin", session.Id);
        var lease = SessionLease.Acquire(sessionDirectory, session.Id);
        Diagnostics.StartupProbe.Mark("open.lease.end", "lease acquired");
        if (session.Backend == BackendId.NativeCSharp)
            return await OpenNativeAsync(project, session, sessionDirectory, lease, cancellationToken)
                .ConfigureAwait(false);
        if (session.Backend is BackendId.Claude or BackendId.Codex or BackendId.Dsh)
            return await OpenProtocolPluginAsync(project, session, sessionDirectory, lease, createNew, cancellationToken)
                .ConfigureAwait(false);
        // 走到这里的只可能是 Pi / OMP；新后端若未登记启动路径，显式失败而不是落到默认分支。
        if (!RpcLaunchProfile.IsBuiltInRpcBackend(session.Backend))
            throw new InvalidOperationException($"后端 '{session.Backend}' 没有内置 RPC 启动路径。");
        ChatSessionCoordinator? coordinator = null;
        DiagnosticLog? diagnosticLog = null;
        try
        {
            diagnosticLog = new DiagnosticLog(_layout);
            var settings = _settings.Current;
            // An explicit override prevents saved settings from deciding project trust.
            // Extension discovery remains under separate plugin management; explicit -e still loads the bridge.
            var trustFlag = trust.Decision == ProjectTrustDecision.Trust ? "--approve" : "--no-approve";
            // 启动差异集中在 RpcLaunchProfile：Pi 与 OMP 是两条平级画像，没有“否则就是 OMP”的兜底分支。
            var profile = RpcLaunchProfile.For(session.Backend, sessionDirectory, trustFlag,
                _nodePath, _cliPath, _ompExecutablePath, _layout.LegacyPiAgentDirectory,
                Path.Combine(_layout.Root, "omp-agent"));
            if (profile.InstallsPiStyleEndpoint) InstallEndpoint(settings.Endpoint);
            else InstallOmpEndpoint(settings.Endpoint);
            var args = new List<string>(profile.BaseArguments);
            if (profile.ModelFlag is { } modelFlag && settings.SelectedModel is { } selectedModel)
                args.AddRange([modelFlag, selectedModel]);
            if (_approvalBridgePath is not null) args.AddRange(["--extension", _approvalBridgePath]);
            if (profile.AcceptsUserExtensions)
                foreach (var path in userExtensions)
                {
                    if (_approvalBridgePath is not null && path.Equals(_approvalBridgePath,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                    args.AddRange(["--extension", path]);
                }
            if (createNew && profile.AcceptsNameFlag && session.Name is { Length: > 0 } name) args.AddRange(["--name", name]);
            if (forkSource is not null)
                args.AddRange(["--fork", forkSource, "--session-id", session.Id]);
            else if (session.FilePath is { } file)
            {
                if (!File.Exists(file)) throw new FileNotFoundException("Session file is missing.", file);
                args.AddRange([profile.ResumeFlag, file]);
            }
            else if (profile.SessionIdFlag is { } sessionIdFlag) args.AddRange([sessionIdFlag, session.Id]);
            else if (!createNew && session.BackendSessionId is not null)
                throw new FileNotFoundException("The backend session file was not persisted; refusing to start a replacement conversation.");
            var environment = new Dictionary<string, string> { [RpcLaunchProfile.AgentDirectoryVariable] = profile.AgentDirectory };
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
            var rpcOptions = new RpcSessionOptions(RequireApprovalBridge: _requireApprovalBridge, DiagnosticSink: diagnosticLog);
            var spec = new BackendProcessSpec(profile.Executable, args, project.Path, environment, session.Backend);
            var factory = (_clientFactoryFactory ?? BackendPluginLoader.CreateRpcFactory)(spec)
                ?? throw new InvalidOperationException(
                    $"后端 '{session.Backend}' 的插件未安装或无法加载（缺少 Fufucode.Plugins.Pi.dll）；" +
                    "请安装对应后端插件，或改用默认的 FUMO 原生内核。");
            coordinator = new ChatSessionCoordinator(factory, session.Backend, rpcOptions);
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
            var backendSessionId = RequiredString(data, "sessionId");
            var backendSessionFile = Path.GetFullPath(RequiredString(data, "sessionFile"));
            // 身份比对由画像声明：Pi 上报的就是受管会话 ID，OMP 上报的是它自己的会话 ID。
            var identityMismatch = profile.UsesManagedSessionId
                ? !string.Equals(backendSessionId, session.Id, StringComparison.Ordinal)
                : session.BackendSessionId is not null && !string.Equals(backendSessionId, session.BackendSessionId, StringComparison.Ordinal);
            if (identityMismatch ||
                !AppDataLayout.IsUnderDirectory(backendSessionFile, sessionDirectory) ||
                (session.FilePath is not null && !Path.GetFullPath(session.FilePath).Equals(backendSessionFile,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                throw new RpcException("The backend opened a different session than requested.");
            _metadata.BindBackendSession(session.Id, backendSessionId);
            session = session with { BackendSessionId = backendSessionId };
            if (File.Exists(backendSessionFile)) _metadata.UpdateSessionFile(session.Id, backendSessionFile);
            var response = await coordinator.QueryEntriesAsync(cancellationToken: cancellationToken);
            var history = new SessionHistory();
            history.ApplyEntriesResponse(RequireData(response, "get_entries"), replace: true);
            // Preserve an interrupted run across crashes; an incomplete last user entry is also interrupted.
            var lastMessage = history.CurrentBranchMessages.LastOrDefault();
            var interrupted = session.WasActive || lastMessage?.Role == "user" ||
                lastMessage is { Role: "assistant", StopReason: "aborted" };
            coordinator.RestoreHistory(history, interrupted);
            _metadata.SetSessionActive(session.Id, false);
            // 没有 --name 参数的后端（OMP）在启动后补一次改名，与 Pi 的 --name 语义一致。
            if (createNew && !profile.AcceptsNameFlag && session.Name is { Length: > 0 } pendingName)
            {
                var rename = await coordinator.SetSessionNameAsync(pendingName, cancellationToken);
                if (!rename.Success) throw new RpcException($"The backend refused session name: {rename.Error}");
                if (File.Exists(backendSessionFile)) _metadata.UpdateSessionFile(session.Id, backendSessionFile);
            }
            return new PersistentSession(session, project, coordinator, history, _metadata, lease, backendSessionFile,
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

    /// <summary>
    /// FUMO 原生内核的会话打开路径：不启动外部进程、不写 Pi 配置。
    /// 会话历史由桌面元数据记录；原生引擎通过设置页端点访问模型。
    /// </summary>
    /// <summary>后台探测端点是否支持图片输入；失败保持默认“支持”，不阻塞会话打开。</summary>
    private static async Task ProbeImageSupportAsync(NativeCSharpChatSession chat, CompatibleEndpoint endpoint,
        string? apiKey, string modelId)
    {
        try
        {
            var probed = await ModelCapabilityProbe.SupportsImageInputAsync(endpoint.BaseUrl, apiKey, modelId)
                .ConfigureAwait(false);
            if (probed is { } known) chat.SetSupportsImages(known);
        }
        catch
        {
            // 探测失败不影响会话；保留默认值。
        }
        finally
        {
            Diagnostics.StartupProbe.Mark("native.image-probe.end", "background probe finished");
        }
    }

    private async Task<PersistentSession> OpenNativeAsync(ProjectRecord project, SessionRecord session,
        string sessionDirectory, SessionLease lease, CancellationToken cancellationToken)
    {
        var diagnosticLog = new DiagnosticLog(_layout);
        try
        {
            Diagnostics.StartupProbe.Mark("native.open.begin", "OpenNativeAsync");
            var file = NativeSessionFactory.SessionFilePath(_layout, project.Id, session.Id);
            var endpoint = _settings.Current.Endpoint;
            Diagnostics.StartupProbe.Mark("native.credentials.begin", "resolve api key");
            var apiKey = endpoint is null
                ? null
                : await _credentials.ResolveAsync(endpoint.ApiKeyEnvironmentVariable, cancellationToken)
                    .ConfigureAwait(false);
            Diagnostics.StartupProbe.Mark("native.credentials.end", apiKey is null ? "no key" : "key resolved");

            var resolvedModel = endpoint is null
                ? null
                : string.IsNullOrWhiteSpace(endpoint.ModelId) ? _settings.Current.SelectedModel : endpoint.ModelId;
            // 图片能力探测是可选能力，不是打开会话的前置条件：本地网关未就绪时会白等 2 秒。
            // 先按“支持图片”创建会话，探测在后台完成后再更新能力位。
            var chat = (NativeCSharpChatSession)NativeSessionFactory.Create(
                _settings, session.Id, project.Path, persistFilePath: file, apiKey: apiKey,
                systemPromptProvider: NativeSystemPromptProvider,
                pluginProviders: NativePluginToolProviders,
                supportsImages: true);
            if (endpoint is not null && !string.IsNullOrWhiteSpace(resolvedModel))
            {
                Diagnostics.StartupProbe.Mark("native.image-probe.begin", resolvedModel);
                _ = ProbeImageSupportAsync(chat, endpoint, apiKey, resolvedModel);
            }
            Diagnostics.StartupProbe.Mark("native.factory.end", "NativeSessionFactory.Create returned");
            var coordinator = new NativeSessionCoordinator(chat, settings: _settings, endpointApiKey: apiKey);
            await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
            Diagnostics.StartupProbe.Mark("native.coordinator.start", "native coordinator started");
            _metadata.BindBackendSession(session.Id, session.Id);
            session = session with { BackendSessionId = session.Id };
            _metadata.UpdateSessionFile(session.Id, file);
            _metadata.SetSessionActive(session.Id, false);

            var history = new SessionHistory();
            var interrupted = session.WasActive;
            Diagnostics.StartupProbe.Mark("native.history.begin", File.Exists(file) ? "loading saved history" : "no saved history");
            if (File.Exists(file))
            {
                var saved = LoadNativeMessages(file);
                if (saved is not null)
                {
                    // 恢复会话级选择：模型、思考等级与执行模式。
                    if (!string.IsNullOrWhiteSpace(saved.ModelId)) chat.SetModel(saved.ModelId);
                    chat.SetThinkingLevel(saved.ThinkingLevel ?? "off");
                    if (saved.ExecutionMode is "manual" or "auto" or "full" or "plan")
                        await chat.SetExecutionModeAsync(saved.ExecutionMode, cancellationToken).ConfigureAwait(false);

                    if (saved.Messages.Count > 0)
                    {
                        chat.RestoreHistory(saved.Messages);
                        history.ApplyEntries(NativeEntries.Build(saved.Messages), leaf: NativeEntries.Leaf(saved.Messages), replace: true);
                        interrupted |= saved.Messages[^1].Role == PureAgentRole.User;
                        if (interrupted) chat.MarkInterrupted();
                    }
                }
            }

            coordinator.RestoreHistory(history, interrupted);

            // 新会话的文件可能尚未写入；仍把预定路径暴露给调用方（恢复/删除/诊断都依赖它）。
            var opened = new PersistentSession(session with { FilePath = file }, project, coordinator, history,
                _metadata, lease, file, chat, diagnosticLog);
            return opened;
        }
        catch
        {
            await diagnosticLog.DisposeAsync().ConfigureAwait(false);
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Claude/Codex/DSH 等协议适配插件的统一打开路径。插件通过 IBackendSessionFactory 提供会话；
    /// 未安装或协议未实现时由插件返回明确诊断，不落回 Pi/OMP 的 RPC 路径。
    /// </summary>
    private async Task<PersistentSession> OpenProtocolPluginAsync(ProjectRecord project, SessionRecord session,
        string sessionDirectory, SessionLease lease, bool createNew, CancellationToken cancellationToken)
    {
        var diagnosticLog = new DiagnosticLog(_layout);
        try
        {
            Directory.CreateDirectory(sessionDirectory);
            var factory = BackendPluginLoader.GetSessionFactory(session.Backend)
                ?? throw new InvalidOperationException(
                    $"后端插件 '{session.Backend}' 未安装或未启用。请先从插件市场安装，或改用 FUMO 原生内核。");
            var file = Path.Combine(sessionDirectory, session.Id + ".json");

            PersistentSession? opened = null;
            var result = await factory.OpenAsync(new BackendSessionOpenRequest(
                session.Id,
                project.Path,
                sessionDirectory,
                file,
                createNew,
                session.WasActive,
                createNew ? null : session.BackendSessionId,
                session.Backend switch
                {
                    BackendId.Claude => _claudeExecutablePath ?? string.Empty,
                    BackendId.Codex => _codexExecutablePath ?? string.Empty,
                    BackendId.Dsh => _dshExecutablePath ?? string.Empty,
                    _ => string.Empty,
                },
                _settings,
                diagnosticLog,
                backendSessionId =>
                {
                    if (string.IsNullOrWhiteSpace(backendSessionId)) return;
                    _metadata.BindBackendSession(session.Id, backendSessionId);
                    opened?.UpdateBackendSessionId(backendSessionId);
                }), cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(result.BackendSessionId))
                _metadata.BindBackendSession(session.Id, result.BackendSessionId);
            _metadata.UpdateSessionFile(session.Id, file);
            _metadata.SetSessionActive(session.Id, false);

            var history = new SessionHistory();
            if (result.Messages.Count > 0)
                history.ApplyEntries(NativeEntries.Build(result.Messages), leaf: NativeEntries.Leaf(result.Messages), replace: true);
            result.Coordinator.RestoreHistory(history, result.Interrupted || session.WasActive);

            opened = new PersistentSession(session, project, result.Coordinator, history, _metadata, lease, file,
                result.Chat, diagnosticLog);
            if (!string.IsNullOrEmpty(result.BackendSessionId))
                opened.UpdateBackendSessionId(result.BackendSessionId);
            return opened;
        }
        catch
        {
            await diagnosticLog.DisposeAsync().ConfigureAwait(false);
            lease.Dispose();
            throw;
        }
    }

    /// <summary>已解析的 FUMO 会话文件（外部宿主复用同一格式与损坏隔离语义）。</summary>
    public sealed record NativeSessionFile(List<PureAgentMessage> Messages, string? ModelId,
        string? ThinkingLevel, string? ExecutionMode);

    public static NativeSessionFile? LoadNativeMessages(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            JsonElement messages;
            string? modelId = null, thinkingLevel = null, executionMode = null;

            if (root.ValueKind == JsonValueKind.Array)
            {
                messages = root;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("messages", out var stored))
            {
                messages = stored;
                if (root.TryGetProperty("modelId", out var model) && model.ValueKind == JsonValueKind.String)
                    modelId = model.GetString();
                if (root.TryGetProperty("thinkingLevel", out var thinking) && thinking.ValueKind == JsonValueKind.String)
                    thinkingLevel = thinking.GetString();
                if (root.TryGetProperty("executionMode", out var mode) && mode.ValueKind == JsonValueKind.String)
                    executionMode = mode.GetString();
            }
            else
            {
                throw new InvalidDataException("会话文件缺少 messages。");
            }

            // 直接复用已解析的 JsonElement，不再 GetRawText() 生成字符串并二次解析。
            var list = messages.Deserialize<List<PureAgentMessage>>();
            return list is null ? null : new NativeSessionFile(list, modelId, thinkingLevel, executionMode);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Failed to restore FUMO session history: {0}", ex);
            try
            {
                var backup = file + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(file, backup, overwrite: true);
            }
            catch { }
            return null;
        }
    }

    private void InstallEndpoint(CompatibleEndpoint? endpoint)
    {
        var target = Path.Combine(_layout.LegacyPiAgentDirectory, "models.json");
        var ownership = Path.Combine(_layout.LegacyPiAgentDirectory, "fufucode-provider.txt");
        if (endpoint is null && !File.Exists(ownership)) return;
        Directory.CreateDirectory(_layout.LegacyPiAgentDirectory);
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
        if (backend == BackendId.NativeCSharp) return;
        if (_clientFactoryFactory is not null) return;
        if (backend == BackendId.Claude && (_claudeExecutablePath is null || !File.Exists(_claudeExecutablePath)))
            throw new FileNotFoundException("Claude runtime missing. Install Claude Code CLI or configure --claude-exe with the verified claude executable.", _claudeExecutablePath);
        if (backend == BackendId.Codex && (_codexExecutablePath is null || !File.Exists(_codexExecutablePath)))
            throw new FileNotFoundException("Codex runtime missing. Install Codex CLI or configure --codex-exe with the verified codex executable.", _codexExecutablePath);
        if (backend == BackendId.Dsh && (_dshExecutablePath is null || !File.Exists(_dshExecutablePath)))
            throw new FileNotFoundException("DSH runtime missing. Install DeepSeek Harness CLI or configure --dsh-exe with the verified dsh executable.", _dshExecutablePath);
        if (backend == BackendId.Pi && (_nodePath.Length == 0 || !File.Exists(_nodePath) || !File.Exists(_cliPath)))
            throw new FileNotFoundException("Pi runtime missing. Configure --pi-node and --pi-cli with the managed runtime paths.");
        if (backend == BackendId.Omp && (_ompExecutablePath is null || !File.Exists(_ompExecutablePath)))
            throw new FileNotFoundException("OMP runtime missing. Configure --omp-exe with the verified OMP 18.8.5 executable.", _ompExecutablePath);
    }

    private static bool PiPluginAvailable() =>
        BackendPluginLoader.CreateRpcFactory(new BackendProcessSpec(string.Empty, [], string.Empty,
            new Dictionary<string, string>(), BackendId.Pi)) is not null;

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
            if (!response.Success) throw new RpcException($"Pi set_model failed: {response.Error}");
        }
        if (settings.ThinkingLevel is { } level)
        {
            var response = await coordinator.SetThinkingLevelAsync(level, cancellationToken);
            if (!response.Success) throw new RpcException($"Pi set_thinking_level failed: {response.Error}");
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
            throw new RpcException("Pi did not select the requested model.");
        if (settings.ThinkingLevel is { } thinking && ThinkingLevelAvailability.ReadCurrentLevel(refreshed) != thinking)
        {
            // A model without reasoning support silently clamps; name the supported levels so the
            // caller can tell "rejected" from "not offered by this model".
            var known = availableThinkingLevels is { IsEmpty: false } listed ? listed : null;
            var offered = known is null ? "unknown" : string.Join(", ", known.Levels);
            throw new RpcException(known is not null && !known.Supports(thinking)
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

    internal static JsonElement RequireData(RpcResponse response, string command)
    {
        if (!response.Success || response.Data is not { ValueKind: JsonValueKind.Object } data)
            throw new RpcException($"Pi {command} failed: {response.Error}");
        return data;
    }

    private static string RequiredString(JsonElement data, string name) =>
        data.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() is { Length: > 0 } value
            ? value : throw new RpcException($"get_state lacks {name}.");
}

/// <summary>Ready-to-use session. History is loaded before this object becomes available to callers.</summary>
public sealed class PersistentSession : IAsyncDisposable
{
    private readonly DesktopMetadataStore _metadata;
    private readonly SessionLease _lease;
    private readonly string _sessionFile;
    private readonly Fufucode.App.Abstractions.IChatSession _chat;
    private readonly DiagnosticLog _diagnosticLog;
    private bool _fileRecorded;
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    // 状态元数据只需保留最新的活跃/故障状态；读取端落后时丢弃旧值，内存有界。
    private const int MetadataChannelCapacity = 64;
    private readonly Channel<(bool Active, bool Faulted)> _metadataChanges =
        Channel.CreateBounded<(bool, bool)>(new BoundedChannelOptions(MetadataChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly Task _metadataWriter;
    private readonly object _metadataLock = new();
    private readonly object _enqueueLock = new();
    private bool _observedActive;
    private bool _recordedActive;
    private bool _disposed;
    private bool _transferred;

    internal PersistentSession(SessionRecord session, ProjectRecord project, ISessionCoordinator coordinator,
        SessionHistory history, DesktopMetadataStore metadata, SessionLease lease, string sessionFile,
        Fufucode.App.Abstractions.IChatSession chat, DiagnosticLog diagnosticLog)
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

    internal void UpdateBackendSessionId(string backendSessionId)
    {
        Session = Session with { BackendSessionId = backendSessionId };
    }
    public ProjectRecord Project { get; }
    public ISessionCoordinator Coordinator { get; }
    public SessionHistory History { get; }
    public Fufucode.App.Abstractions.IChatSession Chat => _chat;
    public List<IAsyncDisposable> Disposables { get; } = new();

    /// <summary>The Pi process, its diagnostics and its lease are shared, so a fork hands them over.</summary>
    internal DiagnosticLog Diagnostics => _diagnosticLog;
    public string DiagnosticFilePath => _diagnosticLog.FilePath;

    internal void ApplyConfirmedState(JsonElement state)
    {
        if (_chat is PiChatSessionAdapter pi) pi.ReadModel(state);
    }

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
        if (!response.Success) throw new RpcException($"Pi refused session name: {response.Error}");
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
            foreach (var d in Disposables)
            {
                try { await d.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            if (_chat is IAsyncDisposable disposableChat) await disposableChat.DisposeAsync();
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
