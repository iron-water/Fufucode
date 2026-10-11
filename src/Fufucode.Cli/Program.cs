using System.Text.Json;
using Fufucode.Core;
using Fufucode.Pi;

namespace Fufucode.Cli;

public static class Program
{
    private const int UsageError = 2;
    private const int RuntimeError = 3;

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var options = CliOptions.Parse(args);
            if (options.Help) { await Console.Out.WriteLineAsync(CliOptions.Usage); return 0; }
            if (options.Command.Length == 0) return await RunReplAsync(options, cancellation.Token);
            if (options.Command == "chat" && options.Arguments.Count == 0 && options.SessionId is not null)
                return await RunReplAsync(options, cancellation.Token);
            if (options.Command == "chat" && !options.LiteralArguments && options.Arguments.SequenceEqual(["--continue"]))
                return await RunReplAsync(options, cancellation.Token);
            if (options.Command == "chat" && options.Arguments.Count > 0)
            {
                if (!options.LiteralArguments && options.Arguments[0].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Invalid chat option or missing prompt.");
                return await ChatAsync(options, string.Join(' ', options.Arguments), cancellation.Token);
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
            case "changes": return await CliReportCommands.ChangesAsync(options, Console.Out, cancellationToken);
            case "usage": return await CliReportCommands.UsageAsync(options, Console.Out);
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
            case "sessions" when args.Count == 2 && args[0] == "delete":
                await CliSessionCommands.DeleteAsync(layout, options.ProjectPath, args[1], options.Json, Console.Out);
                return 0;
            case "settings":
                await CliSettingsCommands.RunAsync(args, new AppSettingsStore(layout), Credentials(), options.Json, Console.Out, cancellationToken);
                return 0;
            case "models" when args.SequenceEqual(["list"]) || args.SequenceEqual(["thinking"]) ||
                (args.Count == 2 && args[0] == "set"):
                if (args.Count == 2 && args[0] == "set") SessionModelPreference.Parse(args[1]);
                if (args.SequenceEqual(["thinking"])) return await ThinkingCommandAsync(active, options, cancellationToken);
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
                project = project?.Path ?? canonical,
                lastOpenedUtc = session.LastOpened, active = session.WasActive
            })));
        }
        else if (sessions.Count == 0) Console.Out.WriteLine("No sessions for this project.");
        else foreach (var session in sessions)
            Console.Out.WriteLine($"{session.Id}  {(session.Name ?? "(unnamed)")}  {session.LastOpened.ToLocalTime():g}{(session.WasActive ? "  [interrupted]" : "")}");
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
            throw new PiRpcException($"Pi get_available_models failed: {response.Error}");
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
        var approval = session.Chat as IExtensionUiSession;
        var result = await CliChatRunner.RunAsync(session.Chat, approval, prompt, options.Json,
            Console.Out, input, cancellationToken);
        return ExitCode(result);
    }

    private static async Task<int> RunReplAsync(CliOptions options, CancellationToken cancellationToken)
    {
        using var input = new CliInputReader(Console.In);
        PersistentSession? session = null;
        try
        {
            session = await OpenSessionAsync(options, cancellationToken);
            if (!options.Json) await Console.Error.WriteLineAsync($"Fufucode CLI · {session.Chat.CurrentModel ?? "model unavailable"} · /exit to quit; /help for commands");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!options.Json) { await Console.Error.WriteAsync("> "); await Console.Error.FlushAsync(cancellationToken); }
                var line = await input.ReadLineAsync(cancellationToken);
                if (line is null || line is "/exit" or "/quit") break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var first = line.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0].TrimStart('/');
                if (first is "sessions" or "models" or "settings" or "extensions" or "diag" or "approve" or "changes" or "usage" or "help")
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
                    if (reopen || deletingCurrent) session = await OpenSessionAsync(options, cancellationToken);
                    continue;
                }
                session ??= await OpenSessionAsync(options, cancellationToken);
                if (line.TrimStart().StartsWith("/chat ", StringComparison.Ordinal) ||
                    line.TrimStart().StartsWith("/chat\t", StringComparison.Ordinal))
                    line = line.TrimStart()[5..].TrimStart();
                if (string.IsNullOrWhiteSpace(line)) throw new ArgumentException("chat needs a prompt.");
                var result = await CliChatRunner.RunAsync(session.Chat, session.Chat as IExtensionUiSession,
                    line, options.Json, Console.Out, input, cancellationToken);
                if (ExitCode(result) != 0) return ExitCode(result);
            }
            return 0;
        }
        finally { if (session is not null) await session.DisposeAsync(); }
    }

    private static int ExitCode(CliChatResult result) => result.FailureExitCode ?? (result.Outcome switch
    {
        Fufucode.App.Abstractions.SessionOutcome.Completed => 0,
        Fufucode.App.Abstractions.SessionOutcome.Interrupted => 130,
        _ => RuntimeError
    });

    private static async Task<PersistentSession> OpenSessionAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var manager = CreateManager(options);
        var projectPath = AppDataLayout.NormalizeProjectPath(options.ProjectPath);
        ProjectExtensionService.ApplyToManager(manager, projectPath);
        if (options.SessionId is { Length: > 0 } id)
        {
            CliSessionCommands.RequireProject(manager.Metadata, projectPath, id);
            return await manager.ResumeAsync(id, cancellationToken);
        }
        var project = manager.Metadata.GetOrCreateProject(projectPath);
        var sessions = manager.Metadata.ListSessions(project.Id);
        return sessions.Count == 0
            ? await manager.CreateAsync(projectPath, cancellationToken: cancellationToken)
            : await manager.ResumeAsync(sessions[0].Id, cancellationToken);
    }

    private static PersistentSessionManager CreateManager(CliOptions options)
    {
        var (node, cli) = ResolveRuntime(options);
        return new PersistentSessionManager(node, cli, new AppDataLayout(options.AppDataRoot));
    }

    private static (string Node, string Cli) ResolveRuntime(CliOptions options)
    {
        var node = options.PiNodePath;
        var cli = options.PiCliPath;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            node ??= Path.Combine(dir.FullName, "runtime", "node", "node.exe");
            cli ??= Path.Combine(dir.FullName, "runtime", "pi", "node_modules", "@earendil-works",
                "pi-coding-agent", "dist", "bundle", "cli.js");
            if (File.Exists(node) && File.Exists(cli)) return (Path.GetFullPath(node), Path.GetFullPath(cli));
            if (!File.Exists(node)) node = options.PiNodePath;
            if (!File.Exists(cli)) cli = options.PiCliPath;
        }
        throw new FileNotFoundException("Bundled Pi runtime was not found. Supply --pi-node and --pi-cli.");
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}

public sealed record CliOptions(string Command, IReadOnlyList<string> Arguments, bool Json, bool Help,
    string ProjectPath, string? SessionId, string? AppDataRoot, string? PiNodePath, string? PiCliPath,
    bool LiteralArguments = false)
{
    public const string Usage = """
        Fufucode command line

        Usage:
          fufucode [--project PATH] [--session ID] [--json] [--pi-node PATH] [--pi-cli PATH]
          fufucode sessions list | show <id> | delete <id> [options]
          fufucode chat <prompt> [options]
          fufucode chat --continue [options]
          fufucode chat --resume <id> [options]
          fufucode models list | set <provider/modelId> | thinking [options]
          fufucode settings get [key] | set <key> <value> [options]
          fufucode extensions list | enable <path> | disable <path> [options]
          fufucode diag export [path] [options]
          fufucode changes list [--scope workspace|session] [options]
          fufucode changes diff <path> [--context N] [options]
          fufucode usage show [options]
          fufucode ui snapshot | screenshot <new.png> | invoke nav.<page> [--page chat|extensions|settings] [--ui-app PATH]
        In the current chat/REPL process:
          approve list | accept <id> [value] | reject <id>
        REPL commands may use /sessions, /models, /settings, /extensions, /diag, /approve.
        In the current chat/REPL process:
          sessions fork <entryId>
        """;

    public static CliOptions Parse(string[] args)
    {
        var json = false; var help = false; var literal = false; string? project = null, session = null, data = null, node = null, cli = null;
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
                case "--resume": session = RequireValue(args, ref index); break;
                case "--app-data": data = RequireValue(args, ref index); break;
                case "--pi-node": node = RequireValue(args, ref index); break;
                case "--pi-cli": cli = RequireValue(args, ref index); break;
                default: positional.Add(args[index]); break;
            }
        }
        var command = positional.Count == 0 ? "" : positional[0];
        if (session is not null && (string.IsNullOrWhiteSpace(session) ||
            session.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-')))
            throw new ArgumentException("Invalid session ID.");
        return new CliOptions(command, positional.Skip(1).ToArray(), json, help,
            Path.GetFullPath(project ?? Environment.CurrentDirectory), session, data, node, cli, literal);
    }

    private static string RequireValue(string[] args, ref int index) =>
        ++index < args.Length ? args[index] : throw new ArgumentException($"Missing value for {args[index - 1]}.");
}
