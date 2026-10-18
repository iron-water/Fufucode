using System.Text.Json;
using Fufucode.Claude;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.Plugins.Claude;

/// <summary>
/// Claude Code 官方 CLI 的会话工厂。原先位于 Core 的 Claude 分支整体搬到这里；
/// Core 只通过 <see cref="IBackendSessionFactory"/> 调用，不再引用 Fufucode.Claude。
/// </summary>
public sealed class ClaudeSessionFactory : IBackendSessionFactory
{
    public BackendId Backend => BackendId.Claude;

    public async Task<BackendSessionOpenResult> OpenAsync(
        BackendSessionOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = request.Settings.Current;
        var args = ClaudeProtocol.BuildArguments(
            model: settings.SelectedModel,
            resumeSessionId: request.ResumeSessionId,
            permissionMode: "manual",
            includePartialMessages: true);

        var executable = request.ExecutablePath;
        var runArgs = args.ToList();
        if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            runArgs.Insert(0, executable);
            executable = ResolveDotnetHost();
        }

        var options = new ClaudeStreamOptions(
            executable,
            runArgs,
            request.ProjectPath,
            DiagnosticSink: new ClaudeDiagnosticSink(request.DiagnosticLog));

        var adapter = await ClaudeChatSessionAdapter.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
        var chat = new ClaudeChatSession(adapter, persistFilePath: request.PersistFilePath);
        var coordinator = new ClaudeSessionCoordinator(chat, settings: request.Settings,
            diagnosticLog: request.DiagnosticLog);
        await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);

        if (request.SessionIdChanged is not null)
        {
            chat.SessionIdChanged += id => request.SessionIdChanged(id);
        }

        var messages = LoadHistory(request.PersistFilePath);
        chat.RestoreHistory(messages);
        var interrupted = request.WasActive ||
            (messages.Count > 0 && messages[^1].Role == PureAgentRole.User);

        return new BackendSessionOpenResult(
            coordinator,
            chat,
            messages,
            interrupted,
            chat.SessionId);
    }

    private static List<PureAgentMessage> LoadHistory(string file)
    {
        if (!File.Exists(file)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("messages", out var messages))
            {
                return JsonSerializer.Deserialize<List<PureAgentMessage>>(messages.GetRawText()) ?? [];
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Failed to restore Claude session history: {0}", ex);
            try
            {
                File.Move(file, file + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"),
                    overwrite: true);
            }
            catch { }
        }
        return [];
    }

    private static string ResolveDotnetHost()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var standard = Path.Combine(programFiles, "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(standard) ? standard : "dotnet";
    }
}
