using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;

namespace Fufucode.Core.Backend;

/// <summary>
/// 后端插件打开一个会话所需的全部宿主上下文。
/// Core 只组装请求；协议细节、进程/流启动与历史恢复由插件实现。
/// </summary>
public sealed record BackendSessionOpenRequest(
    string SessionId,
    string ProjectPath,
    string SessionDirectory,
    string PersistFilePath,
    bool CreateNew,
    bool WasActive,
    string? ResumeSessionId,
    string ExecutablePath,
    AppSettingsStore Settings,
    DiagnosticLog DiagnosticLog,
    Action<string>? SessionIdChanged = null);

/// <summary>
/// 后端插件返回的已连接会话。Chat 供外壳的事件/发送门面使用，
/// Coordinator 供 UI 与持久化层使用，Messages 用于恢复时间线。
/// </summary>
public sealed record BackendSessionOpenResult(
    ISessionCoordinator Coordinator,
    IChatSession Chat,
    IReadOnlyList<PureAgentMessage> Messages,
    bool Interrupted,
    string? BackendSessionId);

/// <summary>
/// 协议适配型后端（Claude/Codex/DSH 等）的插件契约。
/// 插件在自己的 InitializeAsync 中注册工厂；Core 不引用任何插件程序集。
/// </summary>
public interface IBackendSessionFactory
{
    BackendId Backend { get; }

    Task<BackendSessionOpenResult> OpenAsync(
        BackendSessionOpenRequest request,
        CancellationToken cancellationToken = default);
}
