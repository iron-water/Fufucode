using Fufucode.Core.Automation;
using System.Text.Json;

if (args.Length > 0 && args[0] == "fixture")
{
    switch (args[1])
    {
        case "write": File.WriteAllText(args[2], args[3]); return 0;
        case "echo": Console.Write(args[2]); return 0;
        case "fail": Console.Error.Write("failure"); return 7;
        case "child": await Task.Delay(1500); File.WriteAllText(args[2], "survived"); return 0;
        case "tree":
            var child = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            if (Environment.ProcessPath!.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)) child.ArgumentList.Add(typeof(ManagedValidation).Assembly.Location);
            foreach (var arg in new[] { "fixture", "child", args[2] }) child.ArgumentList.Add(arg);
            System.Diagnostics.Process.Start(child);
            await Task.Delay(30000); return 0;
        case "wait": await Task.Delay(30000); return 0;
        case "tamper": File.WriteAllText("candidate.txt", "changed"); return 0;
        case "flood": Console.Write(new string('x', 10000)); return 0;
        default: Console.Write("hello with spaces & ;"); return 0;
    }
}
var root = Path.Combine(Environment.GetEnvironmentVariable("FUFUCODE_TEST_ROOT") ?? Path.GetTempPath(), "fufucode-managed-val-" + Guid.NewGuid().ToString("N"));
var tree = Path.Combine(root, "tree");
Directory.CreateDirectory(tree);
File.WriteAllText(Path.Combine(tree, "candidate.txt"), "original");
var exe = Environment.ProcessPath!;
var prefix = exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) ? new[] { typeof(ManagedValidation).Assembly.Location } : Array.Empty<string>();
ManagedValidationContract Contract(string mode = "pass") => new(Guid.NewGuid().ToString("N"), "ticket", "run", "acceptance-sha", tree,
    ManagedValidation.Fingerprint(tree), exe, [..prefix, "fixture", mode], TimeSpan.FromSeconds(10), "test-env");
int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); checks++; }
var runner = new ManagedValidation(Path.Combine(root, "artifacts"), "test-env", new("ticket", "run", "acceptance-sha"), (request, _) =>
{
    Check(Path.IsPathFullyQualified(request.Executable) && request.WorkingDirectory == Path.GetFullPath(tree) && request.ContractHash.Length == 64, "bound permission request");
    return Task.FromResult(true);
}, 128);
var success = await runner.RunAsync(Contract());
Console.WriteLine(JsonSerializer.Serialize(success));
Check(success.Status == ManagedValidationStatus.Passed && success.ExitCode == 0 && runner.VerifyCurrent(success), "real success");
var literal = "literal spaces ; & $(not-a-shell) \"quoted\"";
var echoed = await runner.RunAsync(Contract("echo") with { Arguments = [..prefix, "fixture", "echo", literal] });
Check(echoed.Status == ManagedValidationStatus.Passed && File.ReadAllText(echoed.StandardOutput!.Path) == literal, "argument list preserves shell metacharacters literally");
try
{
    await new ManagedValidation(Path.Combine(tree, "artifacts"), "test-env", new("ticket", "run", "acceptance-sha"), (_, _) => Task.FromResult(true)).RunAsync(Contract());
    throw new Exception("nested artifacts accepted");
}
catch (ArgumentException) { Check(true, "artifacts cannot pollute candidate tree"); }
Check((await runner.RunAsync(Contract("fail"))).ExitCode == 7, "real failure exit");
Check((await new ManagedValidation(Path.Combine(root, "deny"), "test-env", new("ticket", "run", "acceptance-sha"), null).RunAsync(Contract())).Status == ManagedValidationStatus.Denied, "missing gate denies");
Check((await new ManagedValidation(Path.Combine(root, "deny"), "test-env", new("ticket", "run", "acceptance-sha"), (_, _) => Task.FromResult(false)).RunAsync(Contract())).Status == ManagedValidationStatus.Denied, "false gate denies");
Check((await runner.RunAsync(Contract() with { CandidateHash = "old" })).Status == ManagedValidationStatus.Stale, "stale candidate");
Check((await runner.RunAsync(Contract() with { RunId = "old-run" })).Status == ManagedValidationStatus.Stale, "stale run");
Check((await runner.RunAsync(Contract() with { AcceptanceDigest = "old-acceptance" })).Status == ManagedValidationStatus.Stale, "stale acceptance");
Check((await runner.RunAsync(Contract() with { EnvironmentIdentity = "old" })).Status == ManagedValidationStatus.Stale, "stale environment");
using (var cancel = new CancellationTokenSource(300))
    Check((await runner.RunAsync(Contract("wait"), cancel.Token)).Status == ManagedValidationStatus.Cancelled, "cancel process tree");
Check((await runner.RunAsync(Contract("wait") with { Timeout = TimeSpan.FromMilliseconds(300) })).Status == ManagedValidationStatus.TimedOut, "timeout process tree");
var marker = Path.Combine(root, "child-survived.txt");
using (var cancelTree = new CancellationTokenSource(650))
    Check((await runner.RunAsync(Contract("tree") with { Arguments = [..prefix, "fixture", "tree", marker] }, cancelTree.Token)).Status == ManagedValidationStatus.Cancelled, "cancel spawned tree");
await Task.Delay(1700);
Check(!File.Exists(marker), "descendant killed before marker write");
var flood = await runner.RunAsync(Contract("flood"));
Check(flood.StandardOutput is { Truncated: true, StoredBytes: 128, TotalBytes: 10000 }, "bounded raw artifact");
File.AppendAllText(flood.StandardOutput!.Path, "forged");
Check(!runner.VerifyCurrent(flood), "artifact tampering rejected");
Check(!runner.VerifyCurrent(JsonSerializer.Deserialize<ManagedValidationReceipt>(JsonSerializer.Serialize(success))!), "historical json is not live authority");
Check((await runner.RunAsync(Contract("tamper"))).Status == ManagedValidationStatus.IntegrityFailure, "candidate contamination rejected");
Check(!runner.VerifyCurrent(success), "receipt invalidated by later workspace changes");
File.WriteAllText(Path.Combine(tree, "candidate.txt"), "original");
var gateMutation = new ManagedValidation(Path.Combine(root, "gate-mutation"), "test-env", new("ticket", "run", "acceptance-sha"), (_, _) =>
{
    File.WriteAllText(Path.Combine(tree, "candidate.txt"), "modified while approval pending");
    return Task.FromResult(true);
});
Check((await gateMutation.RunAsync(Contract())).Status == ManagedValidationStatus.Stale, "candidate rechecked after permission await");
// Host-owned policy supports a real .NET build without treating generated bin/obj as source changes.
var buildTree = Path.Combine(root, "build-tree");
Directory.CreateDirectory(buildTree);
File.WriteAllText(Path.Combine(buildTree, "Fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
File.WriteAllText(Path.Combine(buildTree, "Program.cs"), "System.Console.WriteLine(\"fixture\");");
File.WriteAllText(Path.Combine(buildTree, ".git"), "gitdir: ../host-owned-pointer");
var directoryRules = new[] { "bin", "obj" };
var buildPolicy = new ManagedValidationPolicy(directoryRules, protectedFiles: ["Program.cs", "Fixture.csproj", "bin/acceptance.txt"]);
directoryRules[0] = "Program.cs";
Check(buildPolicy.GeneratedDirectories.All(path => !path.Equals("Program.cs", StringComparison.OrdinalIgnoreCase)), "host policy copies mutable input");
try
{
    ((IList<string>)buildPolicy.GeneratedDirectories)[0] = "Program.cs";
    throw new Exception("mutable policy exposed");
}
catch (NotSupportedException) { Check(true, "policy exposed to gate is immutable"); }
foreach (var unsafePath in new[] { "../src", ".", "bin/**", "C:/source", "bin/../Program.cs" })
{
    try { _ = new ManagedValidationPolicy([unsafePath]); throw new Exception("unsafe rule accepted"); }
    catch (ArgumentException) { Check(true, "unsafe policy path rejected " + unsafePath); }
}
var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.Combine(
    Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"), "dotnet.exe");
ManagedValidationContract BuildContract(ManagedValidationPolicy policy) => new(Guid.NewGuid().ToString("N"), "ticket", "run", "acceptance-sha",
    buildTree, ManagedValidation.Fingerprint(buildTree, policy), Path.GetFullPath(dotnet), ["build", "Fixture.csproj", "--nologo", "-v:q"],
    TimeSpan.FromSeconds(90), "test-env", policy.Digest);
var strictBuildRunner = new ManagedValidation(Path.Combine(root, "strict-build"), "test-env", new("ticket", "run", "acceptance-sha"), (_, _) => Task.FromResult(true));
var strictBuild = await strictBuildRunner.RunAsync(BuildContract(ManagedValidationPolicy.Strict));
Console.WriteLine("Strict build: " + JsonSerializer.Serialize(strictBuild));
Check(strictBuild.ExitCode == 0 && strictBuild.Status == ManagedValidationStatus.IntegrityFailure, "default strict policy detects real build generated files");
int buildApprovals = 0;
var buildRunner = new ManagedValidation(Path.Combine(root, "policy-build"), "test-env", new("ticket", "run", "acceptance-sha"), (request, _) =>
{
    buildApprovals++;
    Check(request.Policy.Digest == buildPolicy.Digest && request.Policy.GeneratedDirectories.Count == 2, "permission gate receives exact host policy");
    return Task.FromResult(true);
}, policy: buildPolicy);
var legalBuild = await buildRunner.RunAsync(BuildContract(buildPolicy));
Console.WriteLine("Allowed build: " + JsonSerializer.Serialize(legalBuild));
Check(legalBuild.Status == ManagedValidationStatus.Passed && legalBuild.ExitCode == 0 && buildRunner.VerifyCurrent(legalBuild), "real dotnet build passes with host bin obj allowance");
var legalContract = BuildContract(buildPolicy);
var expandedByModel = new ManagedValidationPolicy(["bin", "obj"], ["Program.cs"]);
var approvalsBefore = buildApprovals;
var forgedPolicy = await buildRunner.RunAsync(legalContract with
{
    ValidationPolicyDigest = expandedByModel.Digest,
    CandidateHash = ManagedValidation.Fingerprint(buildTree, expandedByModel)
});
Check(forgedPolicy.Status == ManagedValidationStatus.Stale && buildApprovals == approvalsBefore && forgedPolicy.ExitCode is null,
    "model cannot expand host exclusions by changing policy digest and candidate hash");
var omittedPolicy = await buildRunner.RunAsync(legalContract with { ValidationPolicyDigest = null });
Check(omittedPolicy.Status == ManagedValidationStatus.Stale && buildApprovals == approvalsBefore, "nondefault policy cannot be omitted from contract");
var bindingContract = BuildContract(buildPolicy) with { Id = "same-contract-id", Arguments = ["--version"] };
var digestStrict = await strictBuildRunner.RunAsync(bindingContract with { ValidationPolicyDigest = ManagedValidationPolicy.Strict.Digest });
var digestAllowed = await buildRunner.RunAsync(bindingContract);
Check(digestStrict.ContractHash != digestAllowed.ContractHash && digestAllowed.ValidationPolicyDigest == buildPolicy.Digest, "policy is bound into contract hash and receipt");
ManagedValidationContract WriteContract(string relative, string value) => BuildContract(buildPolicy) with
{
    Executable = exe, Arguments = [..prefix, "fixture", "write", relative, value]
};
Check((await buildRunner.RunAsync(WriteContract("bin/new-artifact.txt", "valid new output"))).Status == ManagedValidationStatus.Passed,
    "new files under explicit generated directory pass");
Directory.CreateDirectory(Path.Combine(buildTree, "source", "bin"));
File.WriteAllText(Path.Combine(buildTree, "source", "bin", "input.cs"), "// protected by exact-path policy");
Check((await buildRunner.RunAsync(WriteContract("source/bin/input.cs", "// source changed"))).Status == ManagedValidationStatus.IntegrityFailure,
    "allowance is exact path not arbitrary bin basename");
Check((await buildRunner.RunAsync(WriteContract("Program.cs", "// unauthorized source edit"))).Status == ManagedValidationStatus.IntegrityFailure,
    "source mutation fails despite generated output allowance");
Check((await buildRunner.RunAsync(WriteContract("bin/acceptance.txt", "altered acceptance"))).Status == ManagedValidationStatus.IntegrityFailure,
    "protected acceptance file inside generated directory remains fingerprinted");
Check((await buildRunner.RunAsync(WriteContract(".git", "gitdir: ../changed"))).Status == ManagedValidationStatus.IntegrityFailure,
    "git pointer is not silently excluded");
var beforeMetadataPolicy = new ManagedValidationPolicy(["bin", "obj"], [".git"]);
Check(beforeMetadataPolicy.Digest != buildPolicy.Digest, "git metadata exclusion requires separate explicit host policy");
Console.WriteLine($"{checks} checks passed; artifacts: {root}");
return 0;
