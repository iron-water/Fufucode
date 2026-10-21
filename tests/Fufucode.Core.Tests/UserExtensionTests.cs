using Fufucode.Core;

internal static class UserExtensionTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static string[] StartupArgs(PersistentSession session) =>
        session.Coordinator.QueryStateAsync().GetAwaiter().GetResult().Data!.Value
            .GetProperty("startupArgs").EnumerateArray().Select(x => x.GetString()!).ToArray();

    private static string[] ExplicitExtensions(string[] args)
    {
        var paths = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--extension") paths.Add(args[i + 1]);
        return paths.ToArray();
    }

    public static async Task RunFakeAsync(string root, string fake, string dotnet)
    {
        var scratch = Path.Combine(root, "tmp", "user-extension-fake", Guid.NewGuid().ToString("N"));
        var work = Path.Combine(scratch, "work");
        Directory.CreateDirectory(work);
        var one = Path.Combine(scratch, "one.ts");
        var two = Path.Combine(scratch, "two.ts");
        File.WriteAllText(one, "export default function() {};");
        File.WriteAllText(two, "export default function() {};");
        var bridge = Path.Combine(root, "extensions", "fufucode-bridge");
        var manager = new PersistentSessionManager(dotnet, fake,
            new AppDataLayout(Path.Combine(scratch, "appdata")), bridge, requireApprovalBridge: false);
        manager.UserExtensionPaths = [one, one, two];
        Check(manager.UserExtensionPaths.SequenceEqual([one, two]),
            "user extension configuration snapshots and deduplicates absolute paths");
        await using (var first = await manager.CreateAsync(work, backend: BackendId.Pi))
        {
            var flags = StartupArgs(first);
            Check(flags.Contains("--no-extensions") &&
                ExplicitExtensions(flags).SequenceEqual([bridge, one, two]) && flags.Contains("--no-approve"),
                "built-in bridge precedes two explicit user extensions under controlled discovery");
            manager.UserExtensionPaths = [two];
            Check(ExplicitExtensions(StartupArgs(first)).SequenceEqual([bridge, one, two]),
                "changing user extension selection does not alter a running Pi process");
        }
        await using (var next = await manager.CreateAsync(work, backend: BackendId.Pi))
            Check(ExplicitExtensions(StartupArgs(next)).SequenceEqual([bridge, two]),
                "new Pi process receives the updated user extension selection");

        var missing = Path.Combine(scratch, "missing.ts");
        manager.UserExtensionPaths = [missing];
        var projectId = manager.Metadata.GetOrCreateProject(work).Id;
        var before = manager.Metadata.ListSessions(projectId).Count;
        try { await manager.CreateAsync(work, backend: BackendId.Pi); throw new Exception("missing extension was started"); }
        catch (UserExtensionLoadException error)
        {
            Check(error.ExtensionPath == missing && error.Kind == UserExtensionLoadFailureKind.MissingPath &&
                manager.Metadata.ListSessions(projectId).Count == before,
                "missing user extension fails before session creation with a typed path");
        }

        var local = Path.Combine(work, "local.ts");
        File.WriteAllText(local, "export default function() {};");
        manager.UserExtensionPaths = [local];
        Check(manager.GetProjectTrustStatus(work).RequiresDecision &&
            manager.GetProjectTrustStatus(work).ProtectedResources.Contains(local),
            "project-local user extension participates in project trust inspection");
        try { await manager.CreateAsync(work, backend: BackendId.Pi); throw new Exception("undecided local extension started"); }
        catch (ProjectTrustDecisionRequiredException) { }
        manager.SetProjectTrustDecision(work, ProjectTrustDecision.DoNotTrust);
        try { await manager.CreateAsync(work, backend: BackendId.Pi); throw new Exception("denied local extension started"); }
        catch (ProjectExtensionNotTrustedException error)
        {
            Check(error.ExtensionPath == local && error.Status.Decision == ProjectTrustDecision.DoNotTrust,
                "denied project cannot load project-local executable extension");
        }
        manager.SetProjectTrustDecision(work, ProjectTrustDecision.Trust);
        await using (var trusted = await manager.CreateAsync(work, backend: BackendId.Pi))
            Check(StartupArgs(trusted).Contains("--approve") &&
                ExplicitExtensions(StartupArgs(trusted)).Contains(local),
                "trusted project explicitly loads its selected local extension");
    }

    public static async Task RunRealAsync(string root, string node, string cli)
    {
        var scratch = Path.Combine(root, "tmp", "user-extension-real", Guid.NewGuid().ToString("N"));
        var work = Path.Combine(scratch, "work");
        var secondDirectory = Path.Combine(scratch, "second");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(secondDirectory);
        var first = Path.Combine(scratch, "first.ts");
        File.WriteAllText(first, "export default function(pi) { pi.registerCommand('fufucode-user-one', { description: 'one', handler: async () => {} }); }");
        File.WriteAllText(Path.Combine(secondDirectory, "index.ts"),
            "export default function(pi) { pi.registerCommand('fufucode-user-two', { description: 'two', handler: async () => {} }); }");
        var manager = new PersistentSessionManager(node, cli, new AppDataLayout(Path.Combine(scratch, "appdata")));
        manager.UserExtensionPaths = [first, secondDirectory];
        await using (var session = await manager.CreateAsync(work, backend: BackendId.Pi))
        {
            var response = await session.Coordinator.QueryCommandsAsync();
            var names = response.Data!.Value.GetProperty("commands").EnumerateArray()
                .Select(x => x.GetProperty("name").GetString()).ToArray();
            Check(session.Coordinator.ApprovalAvailability == ApprovalAvailability.Verified &&
                names.Contains("fufucode-user-one") && names.Contains("fufucode-user-two"),
                "real managed Pi loads two explicit user extensions beside the approval bridge");
        }
        manager.UserExtensionPaths = [first];
        await using (var session = await manager.CreateAsync(work, backend: BackendId.Pi))
        {
            var response = await session.Coordinator.QueryCommandsAsync();
            var names = response.Data!.Value.GetProperty("commands").EnumerateArray()
                .Select(x => x.GetProperty("name").GetString()).ToArray();
            Check(names.Contains("fufucode-user-one") && !names.Contains("fufucode-user-two"),
                "real Pi restart applies disabled user extension selection");
        }
        var failing = Path.Combine(scratch, "failing.ts");
        File.WriteAllText(failing, "export default function() { throw new Error('fixture load failure'); }");
        manager.UserExtensionPaths = [failing];
        try { await manager.CreateAsync(work, backend: BackendId.Pi); throw new Exception("failing extension was accepted"); }
        catch (UserExtensionLoadException error)
        {
            Check(error.ExtensionPath == failing && error.Kind == UserExtensionLoadFailureKind.PiStartupFailure,
                "real Pi extension startup failure identifies the selected path");
        }
    }
}
