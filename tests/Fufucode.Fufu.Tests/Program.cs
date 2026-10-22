using Fufucode.Fufu.Bot;

var root = Path.Combine(Path.GetTempPath(), "fufu-resident-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var core = new ResidentCore(root);
    Check(File.Exists(Path.Combine(root, "resident", "identity.json")), "identity metadata is created");
    Check(Enum.GetValues<ResidentMemoryScope>().All(scope =>
        File.Exists(Path.Combine(root, "resident", "memory", scope switch
        {
            ResidentMemoryScope.Identity => "IDENTITY.md",
            ResidentMemoryScope.User => "USER.md",
            ResidentMemoryScope.Project => "PROJECT.md",
            ResidentMemoryScope.Agent => "AGENTS.md",
            _ => throw new ArgumentOutOfRangeException(),
        }))), "all four memory scopes are created");

    core.WriteMemory(ResidentMemoryScope.User, "# User Memory\n\n喜欢简洁回复。\n");
    Check(core.ReadMemory(ResidentMemoryScope.User).Contains("喜欢简洁回复", StringComparison.Ordinal),
        "user memory is editable and readable");

    core.AppendMemory(ResidentMemoryScope.User, "偏好短句回复");
    Check(core.ReadMemory(ResidentMemoryScope.User).Contains("偏好短句回复", StringComparison.Ordinal),
        "remember appends a user fact");
    Check(core.ForgetMemory(ResidentMemoryScope.User, "偏好短句回复"),
        "forget removes a matching user fact");

    var updated = core.UpdateIdentity(identity => identity with { Personality = "观察优先。" });
    Check(updated.Personality == "观察优先。" && core.Identity.Personality == "观察优先。",
        "identity update persists in the current core");

    core.Observe("test", "session.completed", "fufu-main", "test completion", "corr-1");
    var observations = core.ReadObservations();
    Check(observations.Count == 1 && observations[0].CorrelationId == "corr-1",
        "observation ledger records correlation id");

    var reopened = new ResidentCore(root);
    Check(reopened.ReadMemory(ResidentMemoryScope.User).Contains("喜欢简洁回复", StringComparison.Ordinal),
        "memory survives reopening the resident core");
    Check(reopened.Identity.Personality == "观察优先。", "identity survives reopening the resident core");
    Check(reopened.ReadObservations().Count == 1, "observations survive reopening the resident core");

    Console.WriteLine("Fufu Resident Core tests passed.");
}
finally
{
    try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}
