using Fufucode.NativeCore.Tests;

// Usage: dotnet run -- [--case NN|shared] [--all]
var requested = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--all")
    {
        requested.AddRange(Enumerable.Range(1, 15).Select(n => n.ToString("00")));
        requested.Add("shared");
        continue;
    }
    if (args[i] == "--case" && i + 1 < args.Length)
    {
        requested.Add(args[++i]);
        continue;
    }
    Console.Error.WriteLine($"Unknown argument: {args[i]}");
    return 2;
}
if (requested.Count == 0) requested.Add("12");

var implemented = new HashSet<string>(StringComparer.Ordinal)
{
    "01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12", "13", "14", "15", "shared",
};
var failures = 0;
var passed = new HashSet<string>(StringComparer.Ordinal);
foreach (var name in requested.Distinct(StringComparer.Ordinal))
{
    if (!implemented.Contains(name))
    {
        Console.Error.WriteLine(name is "03" or "06"
            ? $"CASE {name}: not implemented (pending final source)"
            : $"CASE {name}: unknown");
        failures++;
        continue;
    }

    var checks = new Checks();
    try
    {
        await RunCaseAsync(name, checks);
        passed.Add(name);
        Console.WriteLine($"CASE {name}: PASS ({checks.Passed} assertions)");
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"CASE {name}: FAIL {error.Message}");
        Console.WriteLine(error);
    }
}

if (args.Contains("--all"))
{
    var cases = passed.Where(name => name != "shared").Count();
    if (cases < 15) failures = Math.Max(failures, 1);
}
return failures == 0 ? 0 : 1;

static Task RunCaseAsync(string name, Checks checks) => name switch
{
    "01" => Case01InvalidJsonTests.RunAsync(checks),
    "02" => Case02CompactionTests.RunAsync(checks),
    "03" => Case03PlanGuardTests.RunAsync(checks),
    "04" => Case04HandoffTests.RunAsync(checks),
    "06" => Case06TailBudgetTests.RunAsync(checks),
    "05" => Case05HistoryIdentityTests.RunAsync(checks),
    "07" => Case07FactoryChainTests.RunAsync(checks),
    "08" => Case08ForkHistoryTests.RunAsync(checks),
    "09" => Case09DispatcherLockTests.RunAsync(checks),
    "10" => Case10ShutdownTests.RunAsync(checks),
    "11" => Case11TimeoutTests.RunAsync(checks),
    "12" => SseBoundaryTests.RunAsync(checks),
    "13" => Case13LedgerTests.RunAsync(checks),
    "14" => Case14RoundRecorderTests.RunAsync(checks),
    "15" => Case15BatchEditTests.RunAsync(checks),
    "shared" => RunSharedAsync(checks),
    _ => throw new InvalidOperationException("unknown case " + name),
};

static Task RunSharedAsync(Checks checks)
{
    GroupBoundaryTests.Run(checks);
    return Task.CompletedTask;
}
