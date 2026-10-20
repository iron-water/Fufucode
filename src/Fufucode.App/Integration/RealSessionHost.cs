using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;
using Fufucode.Core;

namespace Fufucode.App.Integration;

/// <summary>
/// 真实会话宿主（M4）：持有 PersistentSessionManager 与当前会话，统一生命周期——
/// 首次打开（按 --session/最近/新建）、新建、切换、重命名与按会话隔离的草稿。
/// 切换/新建先打开新会话再释放旧会话：打开失败时旧会话保持可用，不丢现场；
/// 进程与会话独占由 Core 的 SessionLease 保证，同一会话不会被两个进程写入。
/// </summary>
public sealed class RealSessionHost : IAsyncDisposable
{
    private readonly PersistentSessionManager _manager;
    private readonly ProjectRecord _project;
    private PersistentSession _current;
    private bool _disposed;
    // 安全重启“先放后开”：旧进程已释放而新进程未就绪时 host 处于无活动会话态，
    // 不得把已释放对象当作活会话继续服务（Send/Rename 等）。
    private bool _sessionReleased;
    // 关闭不能在扩展恢复仍运行时提前释放旧对象、再漏掉迟到的新进程。
    private readonly SemaphoreSlim _extensionApplyGate = new(1, 1);
    // 确定性竞争测试：真实Pi已就绪、持久选定尚未提交。产品中始终为null。
    internal Func<Task>? ExtensionsResumedForTesting;

    private RealSessionHost(PersistentSessionManager manager, ProjectRecord project, PersistentSession current)
    {
        _manager = manager;
        _project = project;
        _current = current;
    }

    /// <summary>首次打开：--session 指定 id；否则恢复该项目最近会话（失败如实诊断并新建）；无会话则新建。</summary>
    public static async Task<RealSessionHost> OpenAsync(AppOptions options, CancellationToken cancellationToken,
        AppDataLayout? layout = null)
    {
        var projectPath = AppDataLayout.NormalizeProjectPath(options.ProjectPath ?? Environment.CurrentDirectory);
        var appDataLayout = layout ?? new AppDataLayout(options.AppDataRootOverride);
        Fufucode.Core.Diagnostics.StartupProbe.Mark("host.project", projectPath);

        // FUMO 原生内核（默认）不需要 Node/Pi，因此只在显式要外部后端时才解析运行时。
        var backendPreference = new Fufucode.Core.Backend.BackendPreferenceStore(appDataLayout).DefaultBackend;
        var wantsExternal = options.Backend is { } explicitBackend
            ? explicitBackend is BackendId.Pi or BackendId.Omp or BackendId.Claude or BackendId.Codex or BackendId.Dsh
            : backendPreference is Fufucode.Core.Backend.EngineId.Pi or Fufucode.Core.Backend.EngineId.Omp or Fufucode.Core.Backend.EngineId.Claude or Fufucode.Core.Backend.EngineId.Codex or Fufucode.Core.Backend.EngineId.Dsh;
        var (node, cli) = wantsExternal
            ? RealSessionBootstrap.ResolveRuntime(options, required: false)
            : (string.Empty, string.Empty);
        Fufucode.Core.Diagnostics.StartupProbe.Mark("host.runtime", wantsExternal ? "external backend runtime resolved" : "native backend (no runtime)");

        // 产品路径默认要求审批桥接 Verified 才能提交 prompt（decisions.md [轨道A] 桥接探针）。
        // 数据根：显式 layout 优先；否则尊重 AppDataRootOverride（自测隔离），产品参数为默认根。
        var manager = new PersistentSessionManager(node, cli, appDataLayout,
            ompExecutablePath: wantsExternal ? RealSessionBootstrap.ResolveOmpRuntime(options) : null,
            clientFactoryFactory: BackendPluginLoader.CreateRpcFactory,
            claudeExecutablePath: RealSessionBootstrap.ResolveClaudeRuntime(options),
            codexExecutablePath: RealSessionBootstrap.ResolveCodexRuntime(options),
            dshExecutablePath: RealSessionBootstrap.ResolveDshRuntime(options));
        manager.NativePluginToolProviders = NativeBackendWiring.ToolProviders;
        manager.NativeSystemPromptProvider = NativeBackendWiring.SystemPromptProvider;
        Fufucode.Core.Diagnostics.StartupProbe.Mark("host.manager", "PersistentSessionManager constructed");
        var project = manager.Metadata.GetOrCreateProject(projectPath);
        Fufucode.Core.Diagnostics.StartupProbe.Mark("host.project-metadata", project.Id);

        // H4：按项目持久化的启用列表 → 过滤掉磁盘已不存在的路径（仍留在持久列表，
        // UI 显示"路径缺失，已跳过"）→ 下一次 Pi 进程显式加载。
        var enabled = ExtensionPreferences.LoadEnabled(manager.Metadata, project.Id);
        var (existingPaths, missingPaths) = FilterExisting(enabled);
        manager.UserExtensionPaths = existingPaths;
        Fufucode.Core.Diagnostics.StartupProbe.Mark("host.extensions", $"{existingPaths.Length} enabled, {missingPaths.Length} missing");

        string? diagnostic = null;
        PersistentSession session;
        Fufucode.Core.Diagnostics.StartupProbe.Mark("host.session.open.begin", options.SessionId ?? "recent-or-new");
        if (options.SessionId is { Length: > 0 } sessionId)
        {
            if (manager.Metadata.GetSession(sessionId).ProjectId != project.Id)
                throw new InvalidOperationException("会话与指定工作目录不匹配。");
            session = await manager.ResumeAsync(sessionId, cancellationToken);
        }
        else
        {
            var existing = manager.Metadata.ListSessions(project.Id).Where(s => options.Backend is null || s.Backend == options.Backend).ToArray();
            if (existing.Length == 0)
            {
                var preferredBackend = options.Backend ?? manager.PreferredBackend;
                if (!manager.IsBackendAvailable(preferredBackend))
                {
                    diagnostic = $"默认后端 {BackendCapabilities.For(preferredBackend).Name} 不可用，已回退到 FUMO 原生内核。";
                    preferredBackend = BackendId.NativeCSharp;
                }
                session = await manager.CreateAsync(projectPath, cancellationToken: cancellationToken, backend: preferredBackend);
            }
            else
            {
                try
                {
                    session = await manager.ResumeAsync(existing[0].Id, cancellationToken);
                }
                catch (Exception error) when (error is not ProjectTrustDecisionRequiredException
                                              and not UserExtensionLoadException
                                              and not ProjectExtensionNotTrustedException
                                              and not OperationCanceledException)
                {
                    // 例如会话被另一进程持有（SessionLease）或文件缺失：如实记录后新建，不冒充恢复成功。
                    // 原后端不可用时回退到 FUMO 原生内核；不再按 Pi→OMP→Pi 猜测。
                    var fallbackBackend = manager.IsBackendAvailable(existing[0].Backend)
                        ? existing[0].Backend
                        : BackendId.NativeCSharp;

                    diagnostic = $"恢复最近会话失败（{error.GetBaseException().Message}），已新建会话。";
                    session = await manager.CreateAsync(projectPath, cancellationToken: cancellationToken, backend: fallbackBackend);
                }
            }
        }

        Fufucode.Core.Diagnostics.StartupProbe.Mark("host.session.open.end", session.Session.Id);
        // 打开成功：实际加载的路径不再保留旧的加载失败记录。
        ExtensionPreferences.ClearErrors(manager.Metadata, project.Id, existingPaths);
        return new RealSessionHost(manager, project, session)
        {
            Diagnostic = diagnostic,
            EnabledExtensionPaths = enabled,
            ActiveExtensionPaths = session.Session.Backend == BackendId.Pi ? existingPaths : [],
            SkippedMissingExtensionPaths = missingPaths,
        };
    }

    /// <summary>保存项目信任决定（与 <see cref="OpenAsync"/> 相同的运行时与数据根解析），下次会话启动生效。</summary>
    public static void SaveTrustDecision(AppOptions options, ProjectTrustStatus status,
        ProjectTrustDecision decision, AppDataLayout? layout = null)
    {
        // 信任决定只写元数据，不需要任何后端运行时；不触发 Pi 解析。
        var manager = new PersistentSessionManager(string.Empty, string.Empty,
            layout ?? new AppDataLayout(options.AppDataRootOverride));
        manager.SetProjectTrustDecision(status.ProjectPath, decision);
    }

    /// <summary>是否有活动会话；重启/恢复失败时为 false（旧进程已释放）。</summary>
    public bool HasLiveSession => !_disposed && !_sessionReleased;

    // ── H4 扩展启用管理 ──

    /// <summary>当前 Pi 进程实际启动时使用的扩展路径快照（磁盘缺失已过滤）。</summary>
    public IReadOnlyList<string> ActiveExtensionPaths { get; private set; } = [];

    /// <summary>按项目持久化的启用列表（含已缺失路径；UI 用"路径缺失"呈现）。</summary>
    public IReadOnlyList<string> EnabledExtensionPaths { get; private set; } = [];

    /// <summary>持久启用列表中磁盘已不存在、本次启动被跳过的路径。</summary>
    public IReadOnlyList<string> SkippedMissingExtensionPaths { get; private set; } = [];

    /// <summary>手动添加的扩展路径（按项目持久化）。</summary>
    public IReadOnlyList<string> AddedExtensionPaths =>
        ExtensionPreferences.LoadAdded(_manager.Metadata, _project.Id);

    /// <summary>最近一次加载失败（路径 → 消息，按项目持久化）。</summary>
    public IReadOnlyDictionary<string, string> ExtensionLoadErrors =>
        ExtensionPreferences.LoadErrors(_manager.Metadata, _project.Id);

    /// <summary>记录一个手动添加的扩展路径（仅持久化记录；启用与否由待重启集合决定）。</summary>
    public void AddExtensionPath(string path)
    {
        var full = Path.GetFullPath(path);
        var added = ExtensionPreferences.LoadAdded(_manager.Metadata, _project.Id);
        if (added.Any(p => PathEquals(p, full)))
        {
            return;
        }

        ExtensionPreferences.SaveAdded(_manager.Metadata, _project.Id, [..added, full]);
    }

    /// <summary>移除手动添加记录（仅移除记录；若已启用，停用由应用变更流程负责）。</summary>
    public void RemoveAddedExtensionPath(string path)
    {
        var added = ExtensionPreferences.LoadAdded(_manager.Metadata, _project.Id)
            .Where(p => !PathEquals(p, path)).ToArray();
        ExtensionPreferences.SaveAdded(_manager.Metadata, _project.Id, added);
    }

    /// <summary>持久化启用列表（应用变更成功后调用；也用于启动失败的"停用并重试"）。</summary>
    public void SaveEnabledExtensions(IReadOnlyList<string> paths)
    {
        ExtensionPreferences.SaveEnabled(_manager.Metadata, _project.Id, paths);
        EnabledExtensionPaths = paths;
    }

    /// <summary>启动失败时：把某扩展从持久启用列表与手动添加列表中移除（host 尚未建立时的静态路径）。</summary>
    public static void DisableExtension(AppOptions options, string extensionPath,
        AppDataLayout? layout = null)
    {
        var manager = new PersistentSessionManager(string.Empty, string.Empty,
            layout ?? new AppDataLayout(options.AppDataRootOverride));
        var projectPath = AppDataLayout.NormalizeProjectPath(
            options.ProjectPath ?? Environment.CurrentDirectory);
        var project = manager.Metadata.GetOrCreateProject(projectPath);
        var enabled = ExtensionPreferences.LoadEnabled(manager.Metadata, project.Id)
            .Where(p => !PathEquals(p, extensionPath)).ToArray();
        ExtensionPreferences.SaveEnabled(manager.Metadata, project.Id, enabled);
        var added = ExtensionPreferences.LoadAdded(manager.Metadata, project.Id)
            .Where(p => !PathEquals(p, extensionPath)).ToArray();
        ExtensionPreferences.SaveAdded(manager.Metadata, project.Id, added);
    }

    /// <summary>
    /// 应用扩展变更 = 安全重启（H4）：释放旧进程 → UserExtensionPaths = 新列表 → 按同
    /// session id 恢复。信任未决先抛 <see cref="ProjectTrustDecisionRequiredException"/>
    /// （此时旧进程仍在，由外层信任流程弹窗并重试本方法）。
    /// 扩展加载失败（MissingPath/PiStartupFailure）或项目内扩展未信任：
    /// 恢复旧启用列表并重开原会话（A 交接要求），返回 <see cref="ExtensionApplyFailure"/>；
    /// 回滚重开本身失败则保持无活动会话态并上抛（外层遮罩+重试）。
    /// </summary>
    public async Task<ExtensionApplyFailure?> ApplyExtensionsAndRestartAsync(
        IReadOnlyList<string> newEnabled, CancellationToken cancellationToken = default)
    {
        await _extensionApplyGate.WaitAsync(cancellationToken);
        try { return await ApplyExtensionsCoreAsync(newEnabled, cancellationToken); }
        finally { _extensionApplyGate.Release(); }
    }

    private async Task<ExtensionApplyFailure?> ApplyExtensionsCoreAsync(
        IReadOnlyList<string> newEnabled, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_current.Session.Backend != BackendId.Pi)
            throw new System.NotSupportedException("此扩展管理入口仅支持 Pi；OMP 扩展兼容与管理尚未接入。");
        using var maintenance = !_sessionReleased ? _current.Coordinator.BeginIdleMaintenance() : null;
        var sessionId = _current.Session.Id;
        var active = ActiveExtensionPaths;
        var (existing, missing) = FilterExisting(newEnabled);
        _manager.UserExtensionPaths = existing;

        // 信任预检先于释放：RequiresDecision 时恢复旧列表并让外层弹窗；
        // 重试时该方法被整体重入，ActiveExtensionPaths 仍是真实生效的旧值。
        var status = _manager.GetProjectTrustStatus(_project.Path);
        if (status.RequiresDecision)
        {
            _manager.UserExtensionPaths = active;
            throw new ProjectTrustDecisionRequiredException(status);
        }

        if (!_sessionReleased)
        {
            await _current.DisposeAsync();
            _sessionReleased = true;
        }

        try
        {
            _current = await _manager.ResumeAsync(sessionId, cancellationToken);
            _sessionReleased = false;
            try
            {
                if (ExtensionsResumedForTesting is { } resumed) await resumed();
                // Resume已返回不代表用户仍接受此次变更。关闭/取消到达时还未发布，
                // 释放这条真实候选连接，保原配置供明确恢复，不能偷偷提交候选。
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
            }
            catch
            {
                _sessionReleased = true;
                await _current.DisposeAsync();
                throw;
            }
            ActiveExtensionPaths = existing;
            SkippedMissingExtensionPaths = missing;
            // 持久列表存完整期望集合（含缺失路径，UI 显示"路径缺失"）；
            // 实际加载成功的路径清除旧错误记录。
            SaveEnabledExtensions(newEnabled);
            ExtensionPreferences.ClearErrors(_manager.Metadata, _project.Id, existing);
            Diagnostic = null;
            return null;
        }
        catch (Exception ex) when (ex is UserExtensionLoadException
                                   or ProjectExtensionNotTrustedException)
        {
            var failure = ExtensionApplyFailure.From(ex);
            ExtensionPreferences.RecordError(_manager.Metadata, _project.Id,
                failure.ExtensionPath, failure.Message);
            // 回滚：恢复旧启用列表并重开原会话；持久列表保持旧值。
            _manager.UserExtensionPaths = active;
            _current = await _manager.ResumeAsync(sessionId, cancellationToken);
            _sessionReleased = false;
            return failure;
        }
        catch
        {
            // 取消/未确认的启动不能把候选留在Manager，导致“重试”偷偷应用它。
            // 不在已取消的窗口后自动再开进程；明确重试恢复旧列表与原session。
            _manager.UserExtensionPaths = active;
            throw;
        }
    }

    internal static (string[] Existing, string[] Missing) FilterExisting(
        IReadOnlyList<string> paths)
    {
        var existing = new List<string>();
        var missing = new List<string>();
        foreach (var path in paths)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                existing.Add(path);
            }
            else
            {
                missing.Add(path);
            }
        }

        return (existing.ToArray(), missing.ToArray());
    }

    internal static bool PathEquals(string a, string b) =>
        string.Equals(NormalizePath(a), NormalizePath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public PersistentSession Current => _current;
    public IChatSession Chat => _current.Chat;
    public string ProjectPath => _project.Path;

    /// <summary>非致命诊断（如最近会话恢复失败后已新建），为 null 表示无异常。</summary>
    public string? Diagnostic { get; private set; }

    public IReadOnlyList<SessionRecord> Sessions => _manager.Metadata.ListSessions(_project.Id);

    /// <summary>当前项目的信任状态（受保护资源清单与已保存决定）。</summary>
    public ProjectTrustStatus TrustStatus => _manager.GetProjectTrustStatus(_project.Path);

    /// <summary>传给 Pi 的 PI_CODING_AGENT_DIR（全局扩展发现位置之一）。</summary>
    public string AgentDirectory => _manager.Layout.AgentDirectory;

    /// <summary>持久会话管理器（扩展目录查询等只读诊断用途）。</summary>
    public PersistentSessionManager Manager => _manager;

    public IReadOnlyList<ChatMessage> CurrentTranscript() =>
        HistoryMapper.MapTranscript(_current.History.CurrentBranchMessages, Chat.CurrentModel);

    public async Task<SessionForkResult> ForkAsync(string entryId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await _manager.ForkAsync(_current, entryId, cancellationToken);
        _current = result.Session;
        _sessionReleased = false;
        return result;
    }

    /// <summary>新建会话并切换过去（旧会话在新会话就绪后才释放）。</summary>
    public async Task NewSessionAsync(string? name = null, CancellationToken cancellationToken = default,
        BackendId backend = BackendId.NativeCSharp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = await _manager.CreateAsync(_project.Path, name, cancellationToken, backend);
        await ReplaceAsync(next);
    }

    /// <summary>切换到项目内既有会话；目标是当前会话时不动。切换失败保留旧会话。</summary>
    public async Task SwitchToAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId == _current.Session.Id && !_sessionReleased)
        {
            return;
        }

        var next = await _manager.ResumeAsync(sessionId, cancellationToken);
        await ReplaceAsync(next);
    }

    /// <summary>
    /// 安全重启当前会话（H3/M5）：关闭旧 Pi 进程并按同一 session id 重开，
    /// 历史由 SessionHistory 恢复。同一 session 的 SessionLease 被旧进程持有，
    /// 因此重启无法"先开后放"——必须先释放旧进程再恢复。
    /// 恢复失败时 host 进入无活动会话态（<see cref="HasLiveSession"/> = false）；
    /// 再次调用本方法即重试（旧进程已释放，不再执行释放步骤）。
    /// 调用方负责守卫（回合进行中/有待决扩展请求时不得调用，重启期间禁止重入）。
    /// </summary>
    public async Task RestartCurrentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sessionId = _current.Session.Id;
        if (!_sessionReleased)
        {
            await _current.DisposeAsync();
            _sessionReleased = true;
        }

        _current = await _manager.ResumeAsync(sessionId, cancellationToken);
        _sessionReleased = false;
        Diagnostic = null;
    }

    /// <summary>重命名当前会话（经 Pi set_session_name 并写入桌面元数据）。</summary>
    public Task RenameCurrentAsync(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sessionReleased)
        {
            throw new InvalidOperationException("会话已终止（上次重启/恢复失败）；请重试重启或切换到其他会话。");
        }

        return _current.RenameAsync(name, cancellationToken);
    }

    /// <summary>当前会话的已存草稿（无则 null）。</summary>
    public string? LoadDraft() => _manager.Metadata.GetDraft(_current.Session.Id);

    /// <summary>保存当前会话草稿（按会话隔离；空文本也落盘，表示已清空）。</summary>
    public void SaveDraft(string text) => _manager.Metadata.SetDraft(_current.Session.Id, text);

    private async Task ReplaceAsync(PersistentSession next)
    {
        var previous = _current;
        _current = next;
        _sessionReleased = false;
        ActiveExtensionPaths = next.Session.Backend == BackendId.Pi ? _manager.UserExtensionPaths : [];
        Diagnostic = null;
        await previous.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _extensionApplyGate.WaitAsync();
        try { await _current.DisposeAsync(); }
        finally { _extensionApplyGate.Release(); }
    }
}
