using System.Text.Json;
using Fufucode.Core;
using Fufucode.Core.Runtimes;

namespace Fufucode.Cli;

/// <summary>
/// 工具链入口：运行时（node/bun/python/git-bash）与后端 CLI（pi/omp/claude/codex/dsh）
/// 使用同一套 status / install / remove 命令和同一张目录表。
/// 系统中已有工具链时直接使用；缺失时才安装到 Fufucode 的 runtime/&lt;id&gt;。
/// </summary>
internal static class CliRuntimeCommands
{
    public static async Task<int> RunAsync(CliOptions options, TextWriter output,
        CancellationToken cancellationToken)
    {
        var layout = new AppDataLayout(options.AppDataRoot);
        var arguments = options.Arguments;

        if (arguments.Count == 0 || string.Equals(arguments[0], "status", StringComparison.OrdinalIgnoreCase))
        {
            var requested = arguments.Count > 1 ? arguments[1] : null;
            if (requested is not null)
            {
                var status = ToolchainCatalog.Detect(layout, requested);
                Print(output, options.Json, status);
                return status.Available ? 0 : 3;
            }

            var statuses = ToolchainCatalog.All.Select(item => ToolchainCatalog.Detect(layout, item.Id)).ToArray();
            if (options.Json)
            {
                output.WriteLine(JsonSerializer.Serialize(statuses));
            }
            else
            {
                output.WriteLine($"{"id",-10} {"kind",-8} {"system",-52} {"managed",-52} state");
                foreach (var status in statuses)
                {
                    output.WriteLine($"{status.Id,-10} {status.Kind,-8} " +
                        $"{(status.SystemPath ?? "-"),-52} {(status.ManagedPath ?? status.ScriptPath ?? "-"),-52} " +
                        $"{(status.Available ? "ok" : "missing")}");
                }
            }
            return 0;
        }

        if (string.Equals(arguments[0], "install", StringComparison.OrdinalIgnoreCase))
        {
            string? from = null;
            string? sha256 = null;
            string? id = null;
            for (var index = 1; index < arguments.Count; index++)
            {
                switch (arguments[index])
                {
                    case "--from": from = Require(arguments, ref index); break;
                    case "--sha256": sha256 = Require(arguments, ref index); break;
                    case "--id": id = Require(arguments, ref index); break;
                    default:
                        if (!arguments[index].StartsWith('-') && id is null) id = arguments[index];
                        else throw new ArgumentException($"Unknown runtime install option '{arguments[index]}'.");
                        break;
                }
            }
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException(
                    "Usage: fufucode runtime install <node|bun|python|git-bash|nvx|pi|omp|claude|codex|dsh> --from <zip|url> [--sha256 HEX]");
            var descriptor = ToolchainCatalog.Find(id)
                ?? throw new ArgumentException($"未知工具链 '{id}'。");
            if (string.IsNullOrWhiteSpace(from))
                throw new ArgumentException("Usage: fufucode runtime install <id> --from <zip|url> [--sha256 HEX]");
            var service = new RuntimePackageService(layout);
            var status = await service.InstallAsync(from, sha256, descriptor.Id, cancellationToken)
                .ConfigureAwait(false);
            output.WriteLine($"toolchain {descriptor.Id}: installed ({status.SizeBytes / (1024d * 1024d):N1} MB)");
            output.WriteLine(status.Directory);
            return 0;
        }

        if (string.Equals(arguments[0], "probe", StringComparison.OrdinalIgnoreCase))
        {
            var id = arguments.Count > 1 ? arguments[1] : "nvx";
            if (!string.Equals(id, "nvx", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Usage: fufucode runtime probe nvx");
            var host = NvxHostProbe.Probe();
            var toolchain = ToolchainCatalog.Detect(layout, "nvx");
            if (options.Json)
                output.WriteLine(JsonSerializer.Serialize(new { host, toolchain }));
            else
            {
                output.WriteLine($"host      : {(host.IsWindows ? "windows" : "unsupported")}");
                output.WriteLine($"whp       : {host.HypervisorAvailable}");
                output.WriteLine($"nvx       : {toolchain.Available}");
                output.WriteLine($"detail    : {host.Diagnostic}");
                if (toolchain.Diagnostic is not null) output.WriteLine($"toolchain : {toolchain.Diagnostic}");
            }
            return host.CanRunNvx && toolchain.Available ? 0 : 3;
        }

        if (string.Equals(arguments[0], "remove", StringComparison.OrdinalIgnoreCase))
        {
            var id = arguments.Count > 1 ? arguments[1] : null;
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException(
                    "Usage: fufucode runtime remove <node|bun|python|git-bash|nvx|pi|omp|claude|codex|dsh>");
            var descriptor = ToolchainCatalog.Find(id)
                ?? throw new ArgumentException($"未知工具链 '{id}'。");
            new RuntimePackageService(layout).Remove(descriptor.Id);
            output.WriteLine($"toolchain {descriptor.Id}: removed");
            return 0;
        }

        throw new ArgumentException(
            "Usage: fufucode runtime status [id] | probe nvx | install <id> --from <zip|url> [--sha256 HEX] | remove <id>");
    }

    private static void Print(TextWriter output, bool json, ToolchainStatus status)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(status));
            return;
        }
        output.WriteLine($"toolchain : {status.Id} ({status.Kind})");
        output.WriteLine($"available : {status.Available}");
        output.WriteLine($"system    : {status.SystemPath ?? "missing"}");
        output.WriteLine($"managed   : {status.ManagedPath ?? "missing"}");
        output.WriteLine($"script    : {status.ScriptPath ?? "n/a"}");
        if (status.Diagnostic is not null) output.WriteLine($"detail    : {status.Diagnostic}");
    }

    private static string Require(IReadOnlyList<string> arguments, ref int index)
    {
        if (index + 1 >= arguments.Count) throw new ArgumentException($"Missing value for '{arguments[index]}'.");
        return arguments[++index];
    }
}
