using Fufucode.Plugins.LayerFs;
using System.Text.Json;

var failures = 0;
void Check(bool condition, string label) { Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {label}"); if (!condition) failures++; }
Check(LayerFsClient.ToLinuxPath(@"C:\My Project\中文.txt", "Ubuntu") == "/mnt/c/My Project/中文.txt", "Drive path preserves spaces and Unicode");
Check(LayerFsClient.ToLinuxPath(@"\\wsl.localhost\Ubuntu\home\a", "Ubuntu") == "/home/a", "WSL UNC maps selected distribution");
try { LayerFsClient.ToLinuxPath(@"\\wsl$\Other\home\a", "Ubuntu"); Check(false, "Cross-distribution UNC refused"); }
catch (ArgumentException) { Check(true, "Cross-distribution UNC refused"); }
try { LayerFsClient.ToLinuxPath(@"\\server\share\file", "Ubuntu"); Check(false, "Network share refused"); }
catch (ArgumentException) { Check(true, "Network share refused"); }
var settings = new LayerFsSettings();
var start = LayerFsClient.StartInfo(@"C:\My Project\bridge.mjs", settings);
Check(start.ArgumentList.Contains("/mnt/c/My Project/bridge.mjs") && !start.UseShellExecute, "Process passes path as one literal argument");
Check(start.ArgumentList.Contains("timeout") && start.ArgumentList.Contains("360s"), "Linux process lifetime bounded");
if (args.Length > 0)
{
    using var client = new LayerFsClient(Path.GetFullPath(args[0]), settings);
    var status = await client.CallAsync(new { action = "status" });
    Check(status.GetProperty("missing").GetArrayLength() == 0, "Real runtime has no missing settings");
    Check(status.GetProperty("docker").GetProperty("level").GetString() == "ok", "Docker API compatible with LayerFS");
    Console.WriteLine(JsonSerializer.Serialize(status));
    var name = "fufucode-smoke-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var source = args.Length > 1 ? args[1] : throw new ArgumentException("Pass a Linux fixture path");
    await client.CallAsync(new { action = "init", project = name, source });
    await client.CallAsync(new { action = "fork", project = name, branch = "work" });
    var sandbox = (await client.CallAsync(new { action = "createSandbox", project = name })).GetProperty("sandbox").GetString();
    var mount = await client.CallAsync(new { action = "mount", project = name, sandbox, branch = "work" });
    var path = mount.GetProperty("path").GetString();
    var exec = await client.CallAsync(new { action = "exec", project = name, sandbox, path, command = "printf 'LayerFS live verification\\n' > result.txt; test -f result.txt && cat result.txt" });
    Check(exec.GetProperty("exitCode").GetInt32() == 0 && exec.GetProperty("stdout").GetString()!.Contains("LayerFS live verification"), "Real mount, write and exec");
    await client.CallAsync(new { action = "commit", project = name, sandbox, path, message = "LayerFS smoke checkpoint" });
    await client.CallAsync(new { action = "unmount", project = name, sandbox, path });
    await client.CallAsync(new { action = "closeSandbox", project = name, sandbox });
    var content = await client.CallAsync(new { action = "read", project = name, branch = "work", path = "/result.txt" });
    Console.WriteLine(content.GetRawText());
    Check(content.GetRawText().Contains("LayerFS live verification"), "Checkpoint survives unmount and new process");
    var commits = await client.CallAsync(new { action = "commits", project = name, branch = "work" });
    Check(commits.GetProperty("items").GetArrayLength() > 0, "Committed history visible");
    var bad = await client.CallAsync(new { action = "createSandbox", project = name });
    var badSandbox = bad.GetProperty("sandbox").GetString();
    var second = await client.CallAsync(new { action = "mount", project = name, sandbox = badSandbox, branch = "work" });
    var secondPath = second.GetProperty("path").GetString();
    var failed = await client.CallAsync(new { action = "exec", project = name, sandbox = badSandbox, path = secondPath, command = "exit 17" });
    Check(failed.GetProperty("exitCode").GetInt32() == 17, "Nonzero command exit preserved");
    await client.CallAsync(new { action = "unmount", project = name, sandbox = badSandbox, path = secondPath });
    await client.CallAsync(new { action = "closeSandbox", project = name, sandbox = badSandbox });
    try { await client.CallAsync(new { action = "fork", project = name, branch = "work" }); Check(false, "Existing branch refused"); }
    catch (InvalidOperationException) { Check(true, "Existing branch refused without retry"); }
    Console.WriteLine($"Retained evidence project: {name}; no containers left running by this test.");
}
return failures == 0 ? 0 : 1;
