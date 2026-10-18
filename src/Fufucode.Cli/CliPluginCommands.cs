using System.Text.Json;
using Fufucode.Core;
using Fufucode.Core.Plugins;

namespace Fufucode.Cli;

/// <summary>
/// 插件分发包入口。与 runtime 命令同构：
/// 安装只部署真实载荷到 distribution/&lt;id&gt;；是否启用仍由插件市场状态决定。
/// </summary>
internal static class CliPluginCommands
{
    public static Task<int> RunAsync(CliOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        var layout = new AppDataLayout(options.AppDataRoot);
        var service = new PluginDistributionService(layout);
        var arguments = options.Arguments;

        if (arguments.Count == 0 || string.Equals(arguments[0], "status", StringComparison.OrdinalIgnoreCase))
        {
            var rows = PluginMarketplaceCatalog.All.Select(definition => new
            {
                definition.Id,
                definition.Name,
                definition.Category,
                PayloadInstalled = service.IsInstalled(definition.Id) || definition.IsEmbedded,
                definition.IsCoreBuiltin,
            }).ToArray();
            if (options.Json)
            {
                output.WriteLine(JsonSerializer.Serialize(rows));
            }
            else
            {
                foreach (var row in rows)
                {
                    output.WriteLine($"{row.Id,-28} {row.Name,-28} payload={(row.PayloadInstalled ? "present" : "missing")}");
                }
            }
            return Task.FromResult(0);
        }

        if (string.Equals(arguments[0], "install", StringComparison.OrdinalIgnoreCase))
        {
            string? id = null, from = null, sha256 = null;
            for (var index = 1; index < arguments.Count; index++)
            {
                switch (arguments[index])
                {
                    case "--from": from = Require(arguments, ref index); break;
                    case "--sha256": sha256 = Require(arguments, ref index); break;
                    default:
                        if (!arguments[index].StartsWith('-') && id is null) id = arguments[index];
                        else throw new ArgumentException($"Unknown plugins install option '{arguments[index]}'.");
                        break;
                }
            }
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(from))
                throw new ArgumentException(
                    "Usage: fufucode plugins install <id> --from <zip|url> [--sha256 HEX]");
            var result = service.InstallAsync(id, from, sha256, cancellationToken).GetAwaiter().GetResult();
            output.WriteLine($"plugin {result.PluginId}: payload installed ({result.FileCount} files)");
            output.WriteLine(result.Directory);
            return Task.FromResult(0);
        }

        if (string.Equals(arguments[0], "remove", StringComparison.OrdinalIgnoreCase))
        {
            var id = arguments.Count > 1 ? arguments[1] : null;
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Usage: fufucode plugins remove <id>");
            service.Remove(id);
            output.WriteLine($"plugin {id}: payload removed");
            return Task.FromResult(0);
        }

        throw new ArgumentException(
            "Usage: fufucode plugins status | install <id> --from <zip|url> [--sha256 HEX] | remove <id>");
    }

    private static string Require(IReadOnlyList<string> arguments, ref int index)
    {
        if (index + 1 >= arguments.Count) throw new ArgumentException($"Missing value for '{arguments[index]}'.");
        return arguments[++index];
    }
}
