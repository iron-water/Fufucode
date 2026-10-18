using System.Text.Json;
using Fufucode.App.Features.Plugins;
using Fufucode.Core;
using Fufucode.Pi;

namespace Fufucode.App.Plugins.Tests;

internal static class PiLoadProofTests
{
    public static async Task<int> RunAsync(string? node = null, string? cli = null)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); ++count; }
        var root = Path.Combine(Path.GetTempPath(), "fufucode-pi-plugin-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("proof evidence root: " + root);
        var layout = new AppDataLayout(Path.Combine(root, "data"));
        var service = new LocalPluginService(layout);
        var source = PluginFixture.Create(root, "源码 ' 中文", "proof.plugin", "证明插件", "1.0.0", entryBody:
            "export default async function(pi) { await Promise.resolve(); }\n");
        var version = await service.ImportAsync(new LocalPluginImportRequest(source));
        var generation = service.CurrentGeneration();
        var proof = await service.PreparePiLoadProofAsync("proof.plugin", version.VersionId);
        var another = await service.PreparePiLoadProofAsync("proof.plugin", version.VersionId);
        Check(proof.CommandName != another.CommandName && proof.LoaderPath != another.LoaderPath,
            "independent preparation has unique operation identities without overwriting loaders");
        Check(PluginPackageScanner.ComputeContentHash(proof.PackageRoot) == version.ContentHash && service.CurrentGeneration() == generation,
            "preparation changes neither immutable package bytes nor selected/pending catalog state");
        Check(!File.ReadAllText(proof.LoaderPath).Contains(layout.Root, StringComparison.Ordinal) &&
              !File.ReadAllText(proof.LoaderPath).Contains(source, StringComparison.Ordinal),
            "generated loader imports a relative managed entry without machine or source paths");
        JsonElement Data(string? name = null, string sourceType = "extension", string? path = null,
            string? description = null, bool duplicate = false) => JsonSerializer.SerializeToElement(new
        {
            commands = Enumerable.Repeat(new { name = name ?? proof.CommandName,
                source = sourceType, description = description ?? proof.Description,
                sourceInfo = new { path = path ?? proof.LoaderPath } }, duplicate ? 2 : 1).ToArray(),
        });
        Check(proof.MatchesCommandData(Data(), out _), "synthetic exact identity confirms only the matcher contract");
        Check(!proof.MatchesCommandData(Data(name: another.CommandName), out _), "foreign operation command cannot confirm this candidate");
        Check(!proof.MatchesCommandData(Data(sourceType: "prompt"), out _), "a prompt template cannot impersonate extension initialization");
        Check(!proof.MatchesCommandData(Data(path: another.LoaderPath), out _), "same command at another exact loader path is rejected");
        Check(!proof.MatchesCommandData(Data(path: proof.EntryPath), out _), "original entry path is distinct from the attesting loader path");
        Check(!proof.MatchesCommandData(Data(description: "other version"), out _), "wrong frozen version/content description is rejected");
        Check(!proof.MatchesCommandData(Data(duplicate: true), out _), "ambiguous duplicate exact receipts are rejected");
        Check(!proof.MatchesCommandData(JsonSerializer.SerializeToElement(new { commands = Array.Empty<object>() }), out _),
            "loader file existence without a command receipt never confirms initialization");
        Check(!proof.MatchesCommandData(JsonSerializer.SerializeToElement(new { commands = 42 }), out _), "malformed command arrays remain unknown");
        var generationId = Guid.NewGuid();
        var response = new RpcResponse("proof-query", "get_commands", true, Data(), null);
        Check(proof.TryConfirmInitialization(response, generationId, generationId, out _),
            "host receipt entry requires a successful current-generation get_commands response");
        Check(!proof.TryConfirmInitialization(response, Guid.NewGuid(), generationId, out _),
            "same loader receipt from an old connection cannot confirm the new process");
        Check(!proof.TryConfirmInitialization(response, Guid.Empty, Guid.Empty, out _),
            "unknown empty connection identity is rejected");
        Check(!proof.TryConfirmInitialization(response with { Command = "get_state" }, generationId, generationId, out _),
            "another RPC command with similar data cannot confirm initialization");
        Check(!proof.TryConfirmInitialization(response with { Success = false }, generationId, generationId, out _),
            "failed RPC with matching data cannot confirm initialization");
        var originalLoader = File.ReadAllBytes(proof.LoaderPath);
        File.AppendAllText(proof.LoaderPath, "// changed\n");
        Check(!proof.MatchesCommandData(Data(), out _), "loader tampering invalidates even an otherwise matching response");
        File.WriteAllBytes(proof.LoaderPath, originalLoader);
        var originalEntry = File.ReadAllBytes(proof.EntryPath);
        File.AppendAllText(proof.EntryPath, "// changed\n");
        Check(!proof.MatchesCommandData(Data(), out _), "package tampering invalidates a past successful response");
        var rejected = false;
        try { await service.PreparePiLoadProofAsync("proof.plugin", version.VersionId); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "preparation rechecks managed integrity before producing any candidate loader");
        File.WriteAllBytes(proof.EntryPath, originalEntry);
        var unsupported = PluginFixture.Create(root, "omp", "omp.proof", "OMP", "1.0", backend: "omp");
        var unsupportedVersion = await service.ImportAsync(new LocalPluginImportRequest(unsupported));
        rejected = false;
        try { await service.PreparePiLoadProofAsync("omp.proof", unsupportedVersion.VersionId); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "OMP fixed copies cannot be prepared as Pi initialization proofs");

        if (node is not null && cli is not null)
        {
            var agent = Path.Combine(root, "pi-agent"); Directory.CreateDirectory(agent);
            // 无凭据、无云请求；本地 provider 只是让 Pi 解析显式最低成本模型标识。
            File.WriteAllText(Path.Combine(agent, "models.json"), JsonSerializer.Serialize(new
            {
                providers = new Dictionary<string, object> { ["proof"] = new
                {
                    baseUrl = "http://127.0.0.1:9/v1", apiKey = "offline-proof-no-credentials", api = "openai-completions",
                    models = new[] { new { id = "devin/swe-2", name = "offline initialization probe", reasoning = false,
                        input = new[] { "text" }, contextWindow = 10000, maxTokens = 1000,
                        cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 } } },
                } },
            }));
            async Task<(RpcResponse Commands, string Stderr, Guid Generation)> Query(string loader)
            {
                var options = new PiRpcOptions(Path.GetFullPath(node),
                    [Path.GetFullPath(cli), "--mode", "rpc", "--no-session", "--no-extensions", "--no-skills",
                     "--no-prompt-templates", "--provider", "proof", "--model", "devin/swe-2", "--extension", loader],
                    root, EnvironmentVariables: new Dictionary<string, string> { ["PI_CODING_AGENT_DIR"] = agent },
                    StartupTimeout: TimeSpan.FromSeconds(30));
                await using var rpc = await PiRpcClient.StartAsync(options);
                var state = await rpc.RequestAsync("get_state");
                Check(state.Success && state.Data!.Value.GetProperty("messageCount").GetInt32() == 0,
                    "real Pi is alive without any prompt/model execution");
                var commands = await rpc.RequestAsync("get_commands");
                await rpc.StopAsync();
                return (commands, rpc.StderrTail, rpc.Generation);
            }
            var success = await Query(proof.LoaderPath);
            File.WriteAllText(Path.Combine(root, "success-commands.json"), success.Commands.Data?.GetRawText());
            Check(proof.TryConfirmInitialization(success.Commands, success.Generation, success.Generation, out _),
                "real Pi 1.1 initialization commits the exact proof command after awaiting the original factory");
            Check(!another.MatchesCommandData(success.Commands.Data!.Value, out _),
                "real receipt from an earlier loader cannot confirm another prepared loader");

            var failedSource = PluginFixture.Create(root, "failed", "failed.proof", "Failure", "1.0", entryBody:
                "export default async function(pi) { pi.registerCommand('before-failure', { handler: async () => {} }); throw new Error('fixture-init-failure'); }\n");
            var failedVersion = await service.ImportAsync(new LocalPluginImportRequest(failedSource));
            var failedProof = await service.PreparePiLoadProofAsync("failed.proof", failedVersion.VersionId);
            // 固定 Pi 1.1.0 的真实 RPC 启动在显式扩展初始化失败时退出1，不能假设继续提供命令。
            var exitedOnFailure = false;
            try { await Query(failedProof.LoaderPath); }
            catch (RpcException error)
            {
                File.WriteAllText(Path.Combine(root, "failed-startup.log"), error.ToString());
                exitedOnFailure = error.ToString().Contains("exited with code 1", StringComparison.Ordinal) &&
                    error.ToString().Contains(failedProof.LoaderPath, StringComparison.Ordinal) &&
                    error.ToString().Contains("fixture-init-failure", StringComparison.Ordinal);
            }
            Check(exitedOnFailure, "real explicit failed factory exits Pi with exact candidate path and failure reason");
            Check(!failedProof.MatchesCommandData(success.Commands.Data!.Value, out _),
                "a failed startup cannot borrow the previous successful loader receipt");
            var restored = await Query(proof.LoaderPath);
            Check(proof.TryConfirmInitialization(restored.Commands, restored.Generation, restored.Generation, out _),
                "the preserved old fixed package initializes again in a fresh real Pi process after candidate failure");
            Check(!proof.TryConfirmInitialization(success.Commands, success.Generation, restored.Generation, out _),
                "real old-process receipt is rejected after restoring the same loader in a fresh generation");
            var tsSource = PluginFixture.Create(root, "typescript 中文", "typescript.proof", "TypeScript", "1.0",
                entry: "index.ts", entryBody: "export default async function(pi: any): Promise<void> { await Promise.resolve(); }\n");
            var tsVersion = await service.ImportAsync(new LocalPluginImportRequest(tsSource));
            var tsProof = await service.PreparePiLoadProofAsync("typescript.proof", tsVersion.VersionId);
            var typescript = await Query(tsProof.LoaderPath);
            Check(tsProof.TryConfirmInitialization(typescript.Commands, typescript.Generation, typescript.Generation, out _),
                "real Pi loads a TypeScript original factory through the relative owned wrapper");
            var entered = Path.Combine(root, "factory-entered.txt");
            var release = Path.Combine(root, "factory-release.txt");
            var completed = Path.Combine(root, "factory-completed.txt");
            var gatedBody = "import {existsSync, writeFileSync} from 'node:fs';\n" +
                "export default async function(pi) { writeFileSync(" + JsonSerializer.Serialize(entered) + ", 'entered');\n" +
                "while (!existsSync(" + JsonSerializer.Serialize(release) + ")) await new Promise(r => setTimeout(r, 10));\n" +
                "writeFileSync(" + JsonSerializer.Serialize(completed) + ", 'completed'); }\n";
            var gatedSource = PluginFixture.Create(root, "gated async", "gated.proof", "Gated", "1.0", entryBody: gatedBody);
            var gatedVersion = await service.ImportAsync(new LocalPluginImportRequest(gatedSource));
            var gatedProof = await service.PreparePiLoadProofAsync("gated.proof", gatedVersion.VersionId);
            var gatedQuery = Query(gatedProof.LoaderPath);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(entered) && !gatedQuery.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(20);
            await Task.Delay(100);
            var waitedForFactory = File.Exists(entered) && !File.Exists(completed) && !gatedQuery.IsCompleted;
            File.WriteAllText(release, "release original factory");
            var gated = await gatedQuery;
            Check(waitedForFactory && File.Exists(completed),
                "real get_state/get_commands remain unavailable until a separately gated async original factory finishes");
            Check(gatedProof.TryConfirmInitialization(gated.Commands, gated.Generation, gated.Generation, out _),
                "only after releasing the async original can the real current-generation proof be confirmed");
            Check((await service.ListAsync()).Items.All(plugin => !plugin.IsActive && plugin.SelectedVersionId is null),
                "real probing still does not publish a selected/loaded product catalog state");
        }
        Console.WriteLine($"PROOF ALL PASS ({count})");
        return 0;
    }
}
