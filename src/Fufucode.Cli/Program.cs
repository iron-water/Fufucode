using System.Text.Json;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Cli;

public static class Program
{
    private const int UsageError = 2;
    private const int RuntimeError = 3;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var dataRoot = ReadAppDataArgument(args);
            Fufucode.Core.Runtimes.ToolchainEnvironment.ApplyToProcess(new AppDataLayout(dataRoot));
        }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync("工具链 PATH 注入失败：" + error.Message);
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var options = CliOptions.Parse(args);
            if (options.Help) { await Console.Out.WriteLineAsync(CliOptions.Usage); return 0; }

            string? pipedInput = null;
            if (Console.IsInputRedirected && options.Command is "" or "chat")
            {
                var expectStdin = options.Arguments.Count == 0;
                pipedInput = await ReadPipedStdinAsync(cancellation.Token, expectStdin);
            }

            // `fufucode` 与 `fufucode chat` 都应接受管道输入；只判断无参数形式会漏掉显式 chat。
            if (options.Command is "" or "chat" && options.Arguments.Count == 0 &&
                !string.IsNullOrWhiteSpace(pipedInput))
                return await ChatAsync(options with { Command = "chat" }, pipedInput, cancellation.Token);

            if (options.Command.Length == 0) return await RunReplAsync(options, cancellation.Token);
            if (options.Command == "chat" && options.Arguments.Count == 0 && (options.SessionId is not null || options.Continue))
            {
                if (!string.IsNullOrWhiteSpace(pipedInput))
                    return await ChatAsync(options, pipedInput, cancellation.Token);
                return await RunReplAsync(options, cancellation.Token);
            }
            if (options.Command == "chat" && !options.LiteralArguments && options.Arguments.SequenceEqual(["--continue"]))
                return await RunReplAsync(options with { Continue = true }, cancellation.Token);
            if (options.Command == "chat" && options.Arguments.Count > 0)
            {
                if (!options.LiteralArguments && options.Arguments[0].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Invalid chat option or missing prompt.");
                var prompt = string.Join(' ', options.Arguments);
                if (!string.IsNullOrWhiteSpace(pipedInput))
                    prompt = $"{prompt}\n\n[Piped Input]:\n{pipedInput}";
                return await ChatAsync(options, prompt, cancellation.Token);
            }
            return await ExecuteCommandAsync(options, null, cancellation.Token);
        }
        catch (OperationCanceledException) { return 130; }
        catch (ArgumentException error) { await Console.Error.WriteLineAsync(error.Message); return UsageError; }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync(error.GetBaseException().Message);
            return RuntimeError;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task<int> ExecuteCommandAsync(CliOptions options, PersistentSession? active,
        CancellationToken cancellationToken)
    {
        var layout = new AppDataLayout(options.AppDataRoot);
        var args = options.Arguments;
        switch (options.Command)
        {
            case "open": return OpenGui(options);
            case "status": return await CliStatusCommands.RunAsync(options, Console.Out, cancellationToken);
            case "commands": return await CommandsCommandAsync(active, options, cancellationToken);
            case "changes": return await CliReportCommands.ChangesAsync(options, Console.Out, cancellationToken);
            case "usage": return await CliReportCommands.UsageAsync(options, Console.Out);
            case "control": return await AutomationPluginCommands.RunControlAsync(options, Console.Out, cancellationToken);
            case "host": return await AutomationPluginCommands.RunHostAsync(options, Console.Out, cancellationToken);
            case "ui":
                if (options.SessionId is not null) throw new ArgumentException("ui preview cannot attach a real session.");
                return await CliUiCommands.RunAsync(args, Console.Out, Console.Error, cancellationToken);
            case "sessions" when args.SequenceEqual(["list"]): return ListSessions(options);
            case "sessions" when args.Count == 2 && args[0] == "show":
                if (active?.Session.Id == args[1])
                {
                    await active.RefreshHistoryAsync(cancellationToken);
                    await CliSessionCommands.ShowAsync(active, options.Json, Console.Out);
                }
                else
                {
                    await using var shown = await OpenSessionAsync(options with { SessionId = args[1] }, cancellationToken);
                    await CliSessionCommands.ShowAsync(shown, options.Json, Console.Out);
                }
                return 0;
            case "sessions" when args.Count == 3 && args[0] == "rename":
                // 活动会话同时回写后端；未打开的会话只改桌面元数据，不因外部运行时缺失而拒绝改名。
                if (active?.Session.Id == args[1])
                {
                    await active.RenameAsync(args[2], cancellationToken);
                    await CliSessionCommands.PrintRenamedAsync(active.Session.Id, args[2], options.Json, Console.Out);
                }
                else
                {
                    await CliSessionCommands.RenameAsync(layout, options.ProjectPath, args[1], args[2],
                        options.Json, Console.Out);
                }
                return 0;
            case "sessions" when args.Count == 2 && args[0] == "delete":
                await CliSessionCommands.DeleteAsync(layout, options.ProjectPath, args[1], options.Json, Console.Out);
                return 0;
            case "settings":
                await CliSettingsCommands.RunAsync(args, new AppSettingsStore(layout), Credentials(), options.Json, Console.Out, cancellationToken);
                return 0;
            case "models" when args.SequenceEqual(["list"]) || args.SequenceEqual(["thinking"]) ||
                (args.Count == 2 && args[0] == "set") || (args.Count == 2 && args[0] == "thinking"):
                if (args.Count == 2 && args[0] == "set") SessionModelPreference.Parse(args[1]);
                if (args.SequenceEqual(["thinking"])) return await ThinkingCommandAsync(active, options, cancellationToken);
                if (args.Count == 2 && args[0] == "thinking")
                    return await SetThinkingCommandAsync(active, options, args[1], cancellationToken);
                if (active is not null) return await ModelCommandAsync(active, options, cancellationToken);
                await using (var session = await OpenSessionAsync(options, cancellationToken))
                    return await ModelCommandAsync(session, options, cancellationToken);
            case "extensions":
                // list is metadata-only; reject bad arity before runtime discovery.
                if (args.SequenceEqual(["list"]))
                    return await CliMaintenanceCommands.RunExtensionsAsync(layout, options.ProjectPath, args, options.Json,
                        Console.Out, cancellationToken: cancellationToken);
                if (args.Count != 2 || args[0] is not ("enable" or "disable"))
                    throw new ArgumentException("Usage: extensions list | enable <path> | disable <path>.");
                var manager = CreateManager(options);
                return await CliMaintenanceCommands.RunExtensionsAsync(layout, options.ProjectPath, args, options.Json,
                    Console.Out, manager, options.SessionId, cancellationToken);
            case "diag":
                return await CliMaintenanceCommands.RunDiagnosticsAsync(layout, args, options.Json, Console.Out, cancellationToken);
            case "runtime":
                return await CliRuntimeCommands.RunAsync(options, Console.Out, cancellationToken);
            case "plugins":
                return await CliPluginCommands.RunAsync(options, Console.Out, cancellationToken);
            case "approve":
                if (active?.Chat is not IExtensionUiSession ui)
                    throw new ArgumentException("approve requires the current chat/REPL process; an independent invocation cannot reply to another process.");
                await CliApprovalCommands.ExecuteAsync(ui, args, options.Json, Console.Out, cancellationToken);
                return 0;
            default: throw new ArgumentException("Unsupported or incomplete command. Run 'fufucode --help'.");
        }
    }

    private static ICredentialResolver Credentials() => OperatingSystem.IsWindows()
        ? new WindowsPasswordVaultCredentialResolver() : new EnvironmentCredentialResolver();

    private static int ListSessions(CliOptions options)
    {
        var layout = new AppDataLayout(options.AppDataRoot);
        var metadata = new DesktopMetadataStore(layout);
        // sessions list is a read-only query: it reports the canonical project path without
        // registering the project, so an unopened directory stays out of the metadata store.
        var canonical = AppDataLayout.NormalizeProjectPath(options.ProjectPath);
        var project = metadata.TryGetProject(canonical);
        var sessions = project is null ? [] : metadata.ListSessions(project.Id);
        if (options.Json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(sessions.Select(session => new
            {
                id = session.Id, name = session.Name, projectId = session.ProjectId,
                project = project?.Path ?? canonical, backend = BackendCapabilities.StorageId(session.Backend),
                lastOpenedUtc = session.LastOpened, active = session.WasActive
            })));
        }
        else if (sessions.Count == 0) Console.Out.WriteLine("No sessions for this project.");
        else foreach (var session in sessions)
            Console.Out.WriteLine($"{session.Id}  {BackendCapabilities.StorageId(session.Backend),-6}  {(session.Name ?? "(unnamed)")}  {session.LastOpened.ToLocalTime():g}{(session.WasActive ? "  [interrupted]" : "")}");
        return 0;
    }

    private static async Task<int> ThinkingCommandAsync(PersistentSession? active, CliOptions options, CancellationToken cancellationToken)
    {
        var session = active;
        var owned = session is null ? await OpenSessionAsync(options, cancellationToken) : null;
        try
        {
            var coordinator = (session ?? owned)!.Coordinator;
            var availability = await coordinator.QueryThinkingLevelsAsync(cancellationToken);
            var state = await coordinator.QueryStateAsync(cancellationToken);
            var current = state.Data is { } data ? ThinkingLevelAvailability.ReadCurrentLevel(data) : null;
            if (options.Json)
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
                { levels = availability.Levels, current, model = session?.Chat.CurrentModel ?? owned?.Chat.CurrentModel }));
            else
            {
                await Console.Out.WriteLineAsync("Available thinking levels: " + string.Join(", ", availability.Levels));
                await Console.Out.WriteLineAsync("Current level: " + (current ?? "unknown"));
            }
            return 0;
        }
        finally { if (owned is not null) await owned.DisposeAsync(); }
    }

    /// <summary>列出当前后端真实可用的命令/工具（内置、插件、MCP、子代理、技能）。</summary>
    private static async Task<int> CommandsCommandAsync(PersistentSession? active, CliOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Arguments.Count > 0) throw new ArgumentException("Usage: fufucode commands [--json].");
        var owns = active is null;
        var session = active ?? await OpenSessionAsync(options, cancellationToken);
        try
        {
            var response = await session.Coordinator.QueryCommandsAsync(cancellationToken);
            if (!response.Success || response.Data is not { } data)
                throw new RpcException($"get_commands failed: {response.Error}");
            var commands = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("commands", out var list)
                ? list : data;
            if (options.Json) { await Console.Out.WriteLineAsync(commands.GetRawText()); return 0; }
            if (commands.ValueKind != JsonValueKind.Array)
            {
                await Console.Out.WriteLineAsync(commands.GetRawText());
                return 0;
            }
            foreach (var item in commands.EnumerateArray())
            {
                var name = ReadString(item, "name") ?? "(unnamed)";
                var source = ReadString(item, "source") ?? "-";
                var description = ReadString(item, "description") ?? "";
                await Console.Out.WriteLineAsync($"{name,-36} {source,-10} {description}");
            }
            return 0;
        }
        finally { if (owns) await session.DisposeAsync(); }
    }

    private static async Task<int> SetThinkingCommandAsync(PersistentSession? active, CliOptions options,
        string level, CancellationToken cancellationToken)
    {
        var owns = active is null;
        var session = active ?? await OpenSessionAsync(options, cancellationToken);
        try
        {
            var response = await session.Coordinator.SetThinkingLevelAsync(level, cancellationToken);
            if (!response.Success)
                throw new RpcException($"后端 {BackendCapabilities.StorageId(session.Session.Backend)} 拒绝思考等级 '{level}'：{response.Error}");
            // 与 GUI 一致：确认后写入应用默认，后续会话继续使用。
            new AppSettingsStore(new AppDataLayout(options.AppDataRoot))
                .Update(current => current with { ThinkingLevel = level });
            if (options.Json) await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { thinkingLevel = level, saved = true }));
            else await Console.Out.WriteLineAsync($"{level} (saved as thinking level)");
            return 0;
        }
        finally { if (owns) await session.DisposeAsync(); }
    }

    private static async Task<int> ModelCommandAsync(PersistentSession session, CliOptions options, CancellationToken cancellationToken)
    {
        if (options.Arguments[0] == "set")
        {
            await CliSettingsCommands.SetModelAsync(session, new AppSettingsStore(new AppDataLayout(options.AppDataRoot)),
                options.Arguments[1], options.Json, Console.Out, cancellationToken);
            return 0;
        }
        var response = await session.Coordinator.QueryAvailableModelsAsync(cancellationToken);
        if (!response.Success || response.Data is not { } data)
            throw new RpcException($"Pi get_available_models failed: {response.Error}");
        if (options.Json) Console.Out.WriteLine(data.GetRawText());
        else PrintModels(data);
        return 0;
    }

    private static void PrintModels(JsonElement data)
    {
        var models = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("models", out var list) ? list : data;
        if (models.ValueKind != JsonValueKind.Array) { Console.Out.WriteLine(models.GetRawText()); return; }
        foreach (var model in models.EnumerateArray())
        {
            var provider = ReadString(model, "provider");
            var id = ReadString(model, "id") ?? ReadString(model, "modelId") ?? "(unknown model)";
            Console.Out.WriteLine(provider is null ? id : $"{provider}/{id}");
        }
    }

    private static async Task<int> ChatAsync(CliOptions options, string prompt, CancellationToken cancellationToken)
    {
        using var input = new CliInputReader(Console.In);
        await using var session = await OpenSessionAsync(options, cancellationToken);
        await ApplySessionOverridesAsync(session, options, cancellationToken);
        var approval = session.Chat as IExtensionUiSession;
        var result = await CliChatRunner.RunAsync(session.Chat, approval, prompt, options.Json,
            Console.Out, input, cancellationToken, autoApprove: options.AutoApprove);
        return ExitCode(result);
    }

    private static async Task<int> RunReplAsync(CliOptions options, CancellationToken cancellationToken)
    {
        using var input = new CliInputReader(Console.In);
        PersistentSession? session = null;
        try
        {
            session = await OpenSessionAsync(options, cancellationToken);
            await ApplySessionOverridesAsync(session, options, cancellationToken);
            if (!options.Json) await Console.Error.WriteLineAsync($"Fufucode CLI · {session.Chat.CurrentModel ?? "model unavailable"} · /exit to quit; /help for commands");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!options.Json) { await Console.Error.WriteAsync("> "); await Console.Error.FlushAsync(cancellationToken); }
                var line = await input.ReadLineAsync(cancellationToken);
                if (line is null || line is "/exit" or "/quit") break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var first = line.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0].TrimStart('/');
                if (first is "sessions" or "models" or "settings" or "extensions" or "diag" or "approve"
                    or "changes" or "usage" or "help" or "open" or "status" or "commands")
                {
                    if (first == "help") { await Console.Error.WriteLineAsync(CliOptions.Usage); continue; }
                    // Fork rebinds the running Pi process; only the REPL owns a live session.
                    var forkTokens = CliCommandLine.Split(line.TrimStart().TrimStart('/'));
                    if (first == "sessions" && forkTokens.Length == 3 && forkTokens[1] == "fork")
                    {
                        if (session is null) throw new ArgumentException("sessions fork needs the current session.");
                        var forked = await CreateManager(options).ForkAsync(session, forkTokens[2], cancellationToken);
                        session = forked.Session;
                        options = options with { SessionId = forked.Session.Session.Id };
                        await Console.Out.WriteLineAsync(options.Json
                            ? JsonSerializer.Serialize(new { id = forked.Session.Session.Id,
                                parentSessionId = forked.ParentSessionId, forkEntryId = forked.ForkEntryId,
                                parentFileUnchanged = forked.ParentFileUnchanged })
                            : $"Forked {forked.ParentSessionId} at {forked.ForkEntryId} -> {forked.Session.Session.Id}" +
                                $" (parent file unchanged: {(forked.ParentFileUnchanged ? "yes" : "unknown or changed")})");
                        continue;
                    }
                    var tokens = CliCommandLine.Split(line.TrimStart().TrimStart('/'));
                    var command = options with { Command = tokens[0], Arguments = tokens.Skip(1).ToArray(), SessionId = session?.Session.Id ?? options.SessionId };
                    // A change verifies a fresh Pi process; release this process's session lease first.
                    var reopen = first == "extensions" && tokens.Length == 3 && tokens[1] is "enable" or "disable";
                    var deletingCurrent = first == "sessions" && tokens.Length == 3 && tokens[1] == "delete" && tokens[2] == session?.Session.Id;
                    if (reopen || deletingCurrent)
                    {
                        await session!.DisposeAsync(); session = null;
                    }
                    await ExecuteCommandAsync(command, session, cancellationToken);
                    if (deletingCurrent) options = options with { SessionId = null };
                    if (reopen || deletingCurrent)
                    {
                        session = await OpenSessionAsync(options, cancellationToken);
                        await ApplySessionOverridesAsync(session, options, cancellationToken);
                    }
                    continue;
                }
                if (session is null)
                {
                    session = await OpenSessionAsync(options, cancellationToken);
                    await ApplySessionOverridesAsync(session, options, cancellationToken);
                }
                if (line.TrimStart().StartsWith("/chat ", StringComparison.Ordinal) ||
                    line.TrimStart().StartsWith("/chat\t", StringComparison.Ordinal))
                    line = line.TrimStart()[5..].TrimStart();
                if (string.IsNullOrWhiteSpace(line)) throw new ArgumentException("chat needs a prompt.");
                var result = await CliChatRunner.RunAsync(session.Chat, session.Chat as IExtensionUiSession,
                    line, options.Json, Console.Out, input, cancellationToken, autoApprove: options.AutoApprove);
                if (ExitCode(result) != 0) return ExitCode(result);
            }
            return 0;
        }
        finally { if (session is not null) await session.DisposeAsync(); }
    }

    private static async Task<string?> ReadPipedStdinAsync(CancellationToken cancellationToken, bool expectStdin = true)
    {
        if (!Console.IsInputRedirected) return null;
        try
        {
            if (!expectStdin)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                var text = await Console.In.ReadToEndAsync(linked.Token);
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            else
            {
                var text = await Console.In.ReadToEndAsync(cancellationToken);
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 单次运行参数必须通过统一协调器落到当前后端；不能只对 FUMO 生效后静默忽略。
    /// 权限模式无法真正应用时直接失败，避免用户以为在只读计划模式下却仍可写文件。
    /// </summary>
    private static async Task ApplySessionOverridesAsync(PersistentSession session, CliOptions options,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.ModelOverride))
        {
            RpcResponse response;
            if (session.Coordinator is NativeSessionCoordinator)
            {
                // FUMO 的模型 ID 本身可以带组织前缀（如 antigravity/gemini-3.8-flash），整体传入。
                response = await session.Coordinator.SetModelAsync(string.Empty, options.ModelOverride, cancellationToken);
            }
            else
            {
                var (provider, modelId) = SessionModelPreference.Parse(options.ModelOverride);
                response = await session.Coordinator.SetModelAsync(provider, modelId, cancellationToken);
            }
            if (!response.Success)
                throw new RpcException($"后端 {BackendCapabilities.StorageId(session.Session.Backend)} 拒绝模型 '{options.ModelOverride}'：{response.Error}");
        }

        if (!string.IsNullOrWhiteSpace(options.PermissionMode))
        {
            var mode = options.PermissionMode;
            var supported = session.Coordinator switch
            {
                NativeSessionCoordinator => true,
                ChatSessionCoordinator pi => pi.SupportsExecutionModes,
                _ => false
            };
            // default/manual 不改变权限，后端无实现时按现状继续；plan/auto/full 必须真正生效。
            if (!supported && mode is not ("default" or "manual"))
                throw new InvalidOperationException(
                    $"后端 {BackendCapabilities.StorageId(session.Session.Backend)} 不支持执行模式 '{mode}'；" +
                    "为免以可写权限运行，已在发送前终止。");
            if (supported)
                await session.Coordinator.SetExecutionModeAsync(mode, cancellationToken);
        }

        if (options.MaxIterations is { } maxIter && maxIter > 0)
        {
            if (session.Chat is NativeCSharpChatSession nativeChat)
            {
                nativeChat.MaxIterations = maxIter;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ThinkingLevel))
        {
            var response = await session.Coordinator.SetThinkingLevelAsync(options.ThinkingLevel, cancellationToken);
            if (!response.Success)
                throw new RpcException($"后端拒绝思考等级 '{options.ThinkingLevel}'：{response.Error}");
        }
    }

    private static int OpenGui(CliOptions options)
    {
        var targetPath = options.Arguments.Count > 0
            ? Path.GetFullPath(options.Arguments[0])
            : options.ProjectPath;
        if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
            throw new DirectoryNotFoundException($"Target path does not exist: {targetPath}");

        var app = CliUiCommands.ResolveApp();
        var start = new System.Diagnostics.ProcessStartInfo(app)
        {
            UseShellExecute = true,
        };
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(targetPath);
        if (options.SessionId is not null)
        {
            start.ArgumentList.Add("--session");
            start.ArgumentList.Add(options.SessionId);
        }
        System.Diagnostics.Process.Start(start);
        Console.Out.WriteLine($"Opened Fufucode GUI for: {targetPath}");
        return 0;
    }

    private static int ExitCode(CliChatResult result) => result.FailureExitCode ?? (result.Outcome switch
    {
        Fufucode.App.Abstractions.SessionOutcome.Completed => 0,
        Fufucode.App.Abstractions.SessionOutcome.Truncated => 2,
        Fufucode.App.Abstractions.SessionOutcome.Interrupted => 130,
        _ => RuntimeError
    });

    private static async Task<PersistentSession> OpenSessionAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var layout = new AppDataLayout(options.AppDataRoot);
        var manager = CreateManager(options);
        var projectPath = AppDataLayout.NormalizeProjectPath(options.ProjectPath);
        ProjectExtensionService.ApplyToManager(manager, projectPath);

        // 初始化已安装插件（如性能监控与子代理）并把插件工具注册表接入 FUMO
        var stateStore = new PluginStateStore(layout);
        var gateway = new PluginCapabilityGateway(stateStore);
        var toolRegistry = new PluginToolRegistry(gateway);
        var pluginManager = new PluginPackageManager(layout, toolRegistry, stateStore);
        try
        {
            pluginManager.InitializeInstalledPlugins(projectPath);
            foreach (var (k, v) in pluginManager.LoadErrors)
                Console.Error.WriteLine($"[Plugin load error: {k}] {v}");
            manager.NativePluginToolProviders = () => [toolRegistry];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Plugin initialization error] {ex.Message}");
        }

        PersistentSession session;
        if (options.SessionId is { Length: > 0 } id)
        {
            CliSessionCommands.RequireProject(manager.Metadata, projectPath, id);
            session = await manager.ResumeAsync(id, cancellationToken);
        }
        else
        {
            var project = manager.Metadata.GetOrCreateProject(projectPath);
            var sessions = manager.Metadata.ListSessions(project.Id);
            var availableSession = sessions.FirstOrDefault(s => manager.IsBackendAvailable(s.Backend));
            session = availableSession is null
                ? await manager.CreateAsync(projectPath, cancellationToken: cancellationToken)
                : await manager.ResumeAsync(availableSession.Id, cancellationToken);
        }
        session.Disposables.Add(pluginManager);
        return session;
    }

    /// <summary>启动早期读取 --app-data，用于注入用户安装的工具链目录。</summary>
    private static string? ReadAppDataArgument(string[] args)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index] == "--app-data") return args[index + 1];
        }
        return null;
    }

    private static PersistentSessionManager CreateManager(CliOptions options)
    {
        // 默认后端是 FUMO 原生内核，不需要 Pi 运行时；仅在显式给出 --pi-node/--pi-cli
        // 或持久偏好指向 Pi/OMP 时才解析外部运行时，缺文件也不再阻止 CLI 启动。
        var layout = new AppDataLayout(options.AppDataRoot);
        var preference = new Fufucode.Core.Backend.BackendPreferenceStore(layout).DefaultBackend;
        var needsExternal = options.PiNodePath is not null || options.PiCliPath is not null ||
            preference is Fufucode.Core.Backend.EngineId.Pi or Fufucode.Core.Backend.EngineId.Omp;
        var (node, cli) = needsExternal
            ? TryResolveRuntime(options)
            : (string.Empty, string.Empty);
        return new PersistentSessionManager(node, cli, layout);
    }

    private static (string Node, string Cli) TryResolveRuntime(CliOptions options)
    {
        var layout = new AppDataLayout(options.AppDataRoot);
        // 开发布局：仓库自带 runtime/ 优先，与其它后端的“仓库内置”层对等。
        for (var cursor = new DirectoryInfo(AppContext.BaseDirectory); cursor is not null; cursor = cursor.Parent)
        {
            var candidateNode = Path.Combine(cursor.FullName, "runtime", "node", "node.exe");
            var candidateCli = Path.Combine(cursor.FullName, "runtime", "pi", "node_modules", "@earendil-works",
                "pi-coding-agent", "dist", "bundle", "cli.js");
            if (File.Exists(candidateNode) && File.Exists(candidateCli))
                return (Path.GetFullPath(candidateNode), Path.GetFullPath(candidateCli));
        }
        // 显式参数 > 统一运行时目录（系统 Node/Pi 或插件自带的 runtime/）。
        var detection = Fufucode.Core.Runtimes.ToolchainCatalog.Detect(layout, "pi");
        string? node = options.PiNodePath ?? detection.ManagedPath ?? detection.SystemPath;
        string? cli = options.PiCliPath ?? detection.ScriptPath;
        // 找不到时返回空路径：Pi 后端会在真正需要时给出明确错误；FUMO 路径不受影响。
        return (node is not null && File.Exists(node) ? Path.GetFullPath(node) : string.Empty,
                cli is not null && File.Exists(cli) ? Path.GetFullPath(cli) : string.Empty);
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}

public sealed record CliOptions(string Command, IReadOnlyList<string> Arguments, bool Json, bool Help,
    string ProjectPath, string? SessionId, string? AppDataRoot, string? PiNodePath, string? PiCliPath,
    bool LiteralArguments = false,
    bool AutoApprove = false,
    string? ModelOverride = null,
    string? PermissionMode = null,
    bool Continue = false,
    int? MaxIterations = null,
    string? ThinkingLevel = null)
{
    private static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "chat", "sessions", "models", "settings", "extensions", "diag", "approve",
        "changes", "usage", "control", "host", "ui", "open", "runtime", "plugins",
        "status", "commands"
    };

    public const string Usage = """
        Fufucode command line

        Usage:
          fufucode [prompt] [options]
          fufucode chat <prompt> [options]
          fufucode status [--json]
          fufucode commands [--json]
          fufucode open [path] [--session ID]
          fufucode sessions list | show <id> | rename <id> <name> | delete <id> [options]
          fufucode models list | set <provider/modelId> | thinking [level] [options]
          fufucode settings get [key] | set <key> <value> [options]
          fufucode extensions list | enable <path> | disable <path> [options]
          fufucode diag export [path] [options]
          fufucode runtime status [id] | install <id> --from <zip|url> [--sha256 HEX] | remove <id>
          fufucode plugins status | install <id> --from <zip|url> [--sha256 HEX] | remove <id>
          fufucode changes list [--scope workspace|session] [options]
          fufucode changes diff <path> [--context N] [options]
          fufucode usage show [options]
          fufucode control init|snapshot|state|groups|inbox|export <absolute.db>
          fufucode host serve|status|stop <absolute-host-options.json>
          fufucode control apply|relation|knowledge|restructure|integration|review|reconsider|repair|consume-repair|archive <absolute.db> <command.json>
          fufucode ui snapshot | screenshot <new.png> | invoke nav.<page> [--page chat|extensions|settings] [--ui-app PATH]

        Chat & Session Options:
          -c, --continue                                   Continue the most recent conversation
          -r, --resume [id]                                Resume conversation by ID (or recent if omitted)
          -m, --model <provider/modelId>                   Temporarily override model for this run
          -y, --yes, --auto-approve                        Auto-approve tool execution (non-interactive / script safe)
          --plan                                           Run in read-only planning mode
          --permission-mode <default|plan|auto|full>       Execution permission mode
          --max-iterations, --max-turns <N>                Optional per-turn iteration budget (default: unlimited)
          --thinking, --thinking-level <level>             Thinking level (off|low|medium|high|max)
          --project PATH                                   Project working directory (default: current directory)
          --session ID                                     Specific session ID
          --json                                           Output machine-readable JSON
          --help, -h                                       Show this help message

        Piping Examples:
          git diff | fufucode "explain this diff"
          cat error.log | fufucode -y "fix the error"

        In the current chat/REPL process:
          approve list | accept <id> [value] | reject <id>
          sessions fork <entryId>
        REPL commands may use /sessions, /models, /settings, /extensions, /diag, /approve,
        /changes, /usage, /status, /commands, /open.
        """;

    public static CliOptions Parse(string[] args)
    {
        var json = false; var help = false; var literal = false;
        var autoApprove = false; var continueLast = false;
        string? project = null, session = null, data = null, node = null, cli = null;
        string? modelOverride = null, permissionMode = null;
        int? maxIterations = null; string? thinkingLevel = null;
        var positional = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--") { literal = true; positional.AddRange(args.Skip(index + 1)); break; }
            switch (args[index])
            {
                case "--json": json = true; break;
                case "--help" or "-h": help = true; break;
                case "--project": project = RequireValue(args, ref index); break;
                case "--session": session = RequireValue(args, ref index); break;
                case "--resume" or "-r":
                    if (index + 1 < args.Length && !args[index + 1].StartsWith('-'))
                        session = RequireValue(args, ref index);
                    else
                        continueLast = true;
                    break;
                case "--continue" or "-c": continueLast = true; break;
                case "-y" or "--yes" or "--auto-approve" or "--dangerously-skip-permissions":
                    autoApprove = true; break;
                case "-m" or "--model":
                    modelOverride = RequireValue(args, ref index); break;
                case "--plan":
                    permissionMode = "plan"; break;
                case "--permission-mode":
                    permissionMode = RequireValue(args, ref index); break;
                case "--max-iterations" or "--max-turns":
                    if (int.TryParse(RequireValue(args, ref index), out var mi) && mi > 0)
                        maxIterations = mi;
                    break;
                case "--thinking" or "--thinking-level":
                    thinkingLevel = RequireValue(args, ref index);
                    break;
                case "--app-data": data = RequireValue(args, ref index); break;
                case "--pi-node": node = RequireValue(args, ref index); break;
                case "--pi-cli": cli = RequireValue(args, ref index); break;
                default: positional.Add(args[index]); break;
            }
        }
        string command;
        IReadOnlyList<string> commandArgs;
        if (positional.Count == 0)
        {
            command = "";
            commandArgs = [];
        }
        else if (KnownCommands.Contains(positional[0]))
        {
            command = positional[0];
            commandArgs = positional.Skip(1).ToArray();
        }
        else
        {
            command = "chat";
            commandArgs = positional.ToArray();
        }
        if (session is not null && (string.IsNullOrWhiteSpace(session) ||
            session.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-')))
            throw new ArgumentException("Invalid session ID.");
        return new CliOptions(command, commandArgs, json, help,
            Path.GetFullPath(project ?? Environment.CurrentDirectory), session, data, node, cli, literal,
            autoApprove, modelOverride, permissionMode, continueLast, maxIterations, thinkingLevel);
    }

    private static string RequireValue(string[] args, ref int index) =>
        ++index < args.Length ? args[index] : throw new ArgumentException($"Missing value for {args[index - 1]}.");
}
