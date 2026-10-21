using System.Text.Json;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using Fufucode.Core.Runtimes;

namespace Fufucode.Cli;

/// <summary>
/// 环境总览：不打开会话、不获取会话租约、不修改会话元数据。
/// 用于快速判断“默认后端是谁、是否可用、模型端点是否配好、工具链/插件是否齐”。
/// 读取设置会确保应用目录存在，但不写入任何设置值。
/// </summary>
internal static class CliStatusCommands
{
    public static async Task<int> RunAsync(CliOptions options, TextWriter output,
        CancellationToken cancellationToken)
    {
        if (options.Arguments.Count > 0)
            throw new ArgumentException("Usage: fufucode status [--json].");

        var layout = new AppDataLayout(options.AppDataRoot);
        var settings = new AppSettingsStore(layout);
        var preference = new BackendPreferenceStore(layout).DefaultBackend;
        var backend = BackendCapabilities.FromEngineId(preference) ?? BackendId.NativeCSharp;
        var backendName = BackendCapabilities.For(backend).Name;
        // FUMO 由本进程内的纯 C# 内核承载，不依赖外部工具链；只有外部后端才查询目录表。
        var backendStatus = preference == EngineId.NativeCSharp
            ? null
            : ToolchainCatalog.Detect(layout, EngineIdMap.ToStorageId(preference));
        var backendAvailable = backendStatus is null || backendStatus.Available;

        var endpoint = settings.Current.Endpoint;
        var endpointConfigured = endpoint is not null && !string.IsNullOrWhiteSpace(endpoint.BaseUrl);
        var credentialConfigured = false;
        if (endpointConfigured && !string.IsNullOrWhiteSpace(endpoint!.ApiKeyEnvironmentVariable))
        {
            ICredentialResolver resolver = OperatingSystem.IsWindows()
                ? new WindowsPasswordVaultCredentialResolver()
                : new EnvironmentCredentialResolver();
            var secret = await resolver.ResolveAsync(endpoint.ApiKeyEnvironmentVariable, cancellationToken);
            credentialConfigured = !string.IsNullOrWhiteSpace(secret);
        }

        var projectPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ProjectPath));
        var projectExists = Directory.Exists(projectPath);
        var metadata = new DesktopMetadataStore(layout);
        var project = projectExists ? metadata.TryGetProject(projectPath) : null;
        var sessions = project is null ? [] : metadata.ListSessions(project.Id);
        var latest = sessions.OrderByDescending(session => session.LastOpened).FirstOrDefault();

        var toolchains = ToolchainCatalog.All
            .Select(item => ToolchainCatalog.Detect(layout, item.Id))
            .ToArray();
        var distribution = new PluginDistributionService(layout);
        var pluginCatalog = PluginMarketplaceCatalog.All;
        var installedPayloads = pluginCatalog.Count(definition =>
            definition.IsEmbedded || distribution.IsInstalled(definition.Id));

        if (options.Json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                project = projectPath,
                projectExists,
                backend = new
                {
                    preferred = EngineIdMap.ToStorageId(preference),
                    name = backendName,
                    available = backendAvailable,
                    diagnostic = backendStatus?.Diagnostic
                },
                model = settings.Current.SelectedModel,
                thinkingLevel = settings.Current.ThinkingLevel,
                endpoint = endpoint is null ? null : new
                {
                    providerName = endpoint.ProviderName,
                    baseUrl = endpoint.BaseUrl,
                    modelId = endpoint.ModelId,
                    credentialConfigured
                },
                sessions = new
                {
                    count = sessions.Count,
                    latest = latest is null ? null : new
                    {
                        id = latest.Id,
                        name = latest.Name,
                        backend = BackendCapabilities.StorageId(latest.Backend),
                        lastOpenedUtc = latest.LastOpened
                    }
                },
                toolchains = toolchains.Select(status => new
                {
                    id = status.Id,
                    kind = status.Kind.ToString(),
                    available = status.Available,
                    systemPath = status.SystemPath,
                    managedPath = status.ManagedPath,
                    scriptPath = status.ScriptPath,
                    diagnostic = status.Diagnostic
                }),
                plugins = new { installedPayloads, catalogSize = pluginCatalog.Count }
            }));
            return backendAvailable ? 0 : 3;
        }

        await output.WriteLineAsync($"project    : {projectPath} ({(projectExists ? "exists" : "missing")})");
        await output.WriteLineAsync($"backend    : {EngineIdMap.ToStorageId(preference)} ({backendName}) " +
            (backendAvailable ? "available" : "unavailable"));
        if (!backendAvailable && backendStatus?.Diagnostic is { Length: > 0 } diagnostic)
            await output.WriteLineAsync($"             {diagnostic}");
        await output.WriteLineAsync($"model      : {settings.Current.SelectedModel ?? "unset"}");
        await output.WriteLineAsync($"thinking   : {settings.Current.ThinkingLevel ?? "unset"}");
        await output.WriteLineAsync(endpoint is null
            ? "endpoint   : unset"
            : $"endpoint   : {endpoint.ProviderName} {endpoint.BaseUrl} (credential: {(credentialConfigured ? "configured" : "missing")})");
        await output.WriteLineAsync(latest is null
            ? $"sessions   : {sessions.Count}"
            : $"sessions   : {sessions.Count}, latest {latest.Id} [{BackendCapabilities.StorageId(latest.Backend)}] " +
                latest.LastOpened.ToLocalTime().ToString("g"));
        await output.WriteLineAsync("toolchains : " + string.Join(", ",
            toolchains.Select(status => status.Id + "=" + (status.Available ? "ok" : "missing"))));
        await output.WriteLineAsync($"plugins    : {installedPayloads}/{pluginCatalog.Count} payloads present");
        return backendAvailable ? 0 : 3;
    }
}
