using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.Codex;

/// <summary>
/// Codex 后端插件入口。会话适配由本插件携带，Core 只通过 IBackendSessionFactory 调用。
/// codex 可执行文件由用户本机安装（npm 全局、独立安装或 PATH）。
/// </summary>
public sealed class CodexBackendPluginPackage : IPluginPackage
{
    public const string PluginId = "backend.codex";

    public string Id => PluginId;
    public string Version => "1.0.0";

    public IReadOnlyList<PluginDependencyDeclaration> Dependencies =>
        Array.Empty<PluginDependencyDeclaration>();

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        BackendPluginLoader.RegisterPluginDirectory(context.PackageDirectory);
        BackendPluginLoader.RegisterSessionFactory(new CodexSessionFactory());
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        BackendPluginLoader.UnregisterSessionFactory(BackendId.Codex);
        BackendPluginLoader.ResetCache();
        return Task.CompletedTask;
    }
}

/// <summary>Codex 官方 CLI 的 `codex exec --json` 协议适配工厂。</summary>
public sealed class CodexSessionFactory : IBackendSessionFactory
{
    public BackendId Backend => BackendId.Codex;

    public async Task<BackendSessionOpenResult> OpenAsync(
        BackendSessionOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
            throw new FileNotFoundException(
                "未找到 Codex CLI。请安装官方 Codex（npm 全局或独立安装），或用 --codex-exe 指定可执行文件。");

        var chat = new CodexChatSession(
            request.ExecutablePath, request.ProjectPath, request.PersistFilePath,
            request.ResumeSessionId, request.Settings.Current.SelectedModel);
        var coordinator = new CodexSessionCoordinator(chat, settings: request.Settings,
            diagnosticLog: request.DiagnosticLog);
        await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
        if (request.SessionIdChanged is not null) chat.SessionIdChanged += id => request.SessionIdChanged(id);

        var messages = BackendSessionHistory.Load(request.PersistFilePath);
        chat.RestoreHistory(messages);
        var interrupted = request.WasActive ||
            (messages.Count > 0 && messages[^1].Role == PureAgentRole.User);

        return new BackendSessionOpenResult(coordinator, chat, messages, interrupted, chat.SessionId);
    }
}
