using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.Dsh;

/// <summary>
/// DeepSeek Harness 后端插件入口。会话适配由本插件携带，Core 只通过 IBackendSessionFactory 调用。
/// dsh 可执行文件由用户本机安装（npm 全局、独立安装或 PATH）。
/// </summary>
public sealed class DshBackendPluginPackage : IPluginPackage
{
    public const string PluginId = "backend.dsh";

    public string Id => PluginId;
    public string Version => "1.0.0";

    public IReadOnlyList<PluginDependencyDeclaration> Dependencies =>
        Array.Empty<PluginDependencyDeclaration>();

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        BackendPluginLoader.RegisterPluginDirectory(context.PackageDirectory);
        BackendPluginLoader.RegisterSessionFactory(new DshSessionFactory());
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        BackendPluginLoader.UnregisterSessionFactory(BackendId.Dsh);
        BackendPluginLoader.ResetCache();
        return Task.CompletedTask;
    }
}

/// <summary>DeepSeek Harness 官方 CLI 的 `--profile headless --json` 协议适配工厂。</summary>
public sealed class DshSessionFactory : IBackendSessionFactory
{
    public BackendId Backend => BackendId.Dsh;

    public async Task<BackendSessionOpenResult> OpenAsync(
        BackendSessionOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
            throw new FileNotFoundException(
                "未找到 DeepSeek Harness CLI。请安装 DSH，或用 --dsh-exe 指定可执行文件。");

        var chat = new DshChatSession(
            request.ExecutablePath, request.ProjectPath, request.PersistFilePath,
            request.ResumeSessionId);
        var coordinator = new DshSessionCoordinator(chat, settings: request.Settings,
            diagnosticLog: request.DiagnosticLog);
        await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
        if (request.SessionIdChanged is not null) chat.SessionIdChanged += id => request.SessionIdChanged(id);

        var messages = DshSessionHistory.Load(request.PersistFilePath);
        chat.RestoreHistory(messages);
        var interrupted = request.WasActive ||
            (messages.Count > 0 && messages[^1].Role == PureAgentRole.User);

        return new BackendSessionOpenResult(coordinator, chat, messages, interrupted, chat.SessionId);
    }
}
