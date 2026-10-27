using System.Text.Json;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using Fufucode.Core.Runtimes;

namespace Fufucode.Cli;

/// <summary>
/// 后端内核的统一入口：与 App 的“新建会话后端”使用同一份 BackendPreferenceStore、
/// 工具链目录和插件状态；运行时或插件缺失时如实报告不可用，不做静默回退。
/// </summary>
internal static class CliBackendCommands
{
    private static readonly BackendId[] Order =
    [
        BackendId.NativeCSharp, BackendId.Pi, BackendId.Omp,
        BackendId.Claude, BackendId.Codex, BackendId.Dsh
    ];

    private sealed record Row(string Id, string Name, bool Preferred, bool Available,
        string? PluginId, bool PluginInstalled, bool PluginEnabled, bool RuntimeAvailable,
        string? RuntimePath, string? Reason);

    public static Task<int> RunAsync(CliOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        if (options.SessionId is not null)
            throw new ArgumentException("backends 命令不接受会话参数。");
        var args = options.Arguments;
        var layout = new AppDataLayout(options.AppDataRoot);
        var manager = new PersistentSessionManager(string.Empty, string.Empty, layout);

        if (args.Count == 0 || args[0] is "list")
        {
            if (args.Count > 1) throw new ArgumentException("Usage: fufucode backends list [--json].");
            var rows = BuildRows(layout, manager);
            if (options.Json)
            {
                output.WriteLine(JsonSerializer.Serialize(rows.Select(row => new
                {
                    id = row.Id,
                    name = row.Name,
                    preferred = row.Preferred,
                    available = row.Available,
                    plugin = row.PluginId is null ? null : new
                    {
                        id = row.PluginId,
                        installed = row.PluginInstalled,
                        enabled = row.PluginEnabled
                    },
                    runtime = new
                    {
                        available = row.RuntimeAvailable,
                        path = row.RuntimePath
                    },
                    reason = row.Reason
                })));
            }
            else
            {
                output.WriteLine($"{"id",-6} {"name",-8} {"default",-8} {"state",-12} plugin/runtime");
                foreach (var row in rows)
                {
                    var plugin = row.PluginId is null ? "builtin"
                        : row.PluginInstalled
                            ? (row.PluginEnabled ? "plugin=on" : "plugin=disabled")
                            : "plugin=missing";
                    output.WriteLine($"{row.Id,-6} {row.Name,-8} {(row.Preferred ? "*" : ""),-8} " +
                        $"{(row.Available ? "available" : "unavailable"),-12} {plugin}" +
                        (row.Reason is null ? "" : "  " + row.Reason));
                }
            }
            return Task.FromResult(0);
        }

        if (args.Count == 2 && args[0] is "use")
        {
            var requested = Parse(args[1]);
            var row = BuildRows(layout, manager).First(candidate => candidate.Id ==
                BackendCapabilities.StorageId(requested));
            if (!row.Available)
                throw new InvalidOperationException($"后端 {row.Name} 不可用：{row.Reason}");
            var engine = EngineIdMap.Parse(row.Id);
            new BackendPreferenceStore(layout).SetDefault(engine);
            output.WriteLine(options.Json
                ? JsonSerializer.Serialize(new { backend = row.Id, saved = true })
                : $"{row.Id} saved as the default backend.");
            return Task.FromResult(0);
        }

        throw new ArgumentException("Usage: fufucode backends list | use <fumo|pi|omp|claude|codex|dsh> [--json].");
    }

    internal sealed record BackendProbe(bool Available, bool RuntimeAvailable, bool PluginInstalled,
        bool PluginEnabled, string? RuntimePath, string? Reason);

    /// <summary>运行时 + 插件载荷 + 启用状态的单一判定；CLI 回退与 backends 列表共用。</summary>
    internal static BackendProbe Probe(AppDataLayout layout, BackendId backend)
    {
        if (backend == BackendId.NativeCSharp)
            return new BackendProbe(true, true, true, true, Environment.ProcessPath, null);

        var id = BackendCapabilities.StorageId(backend);
        var pluginId = PluginId(backend)!;
        var state = new PluginStateStore(layout).GetState(pluginId);
        // 载荷可能来自插件目录，也可能来自 CLI 的 distribution/ 安装路径。
        var pluginInstalled = state.IsInstalled || new PluginDistributionService(layout).IsInstalled(pluginId);
        var pluginEnabled = state.IsEnabled;
        var toolchain = ToolchainCatalog.Detect(layout, id);
        var runtimeAvailable = toolchain.Available;
        var available = runtimeAvailable && pluginInstalled && pluginEnabled;
        var reason = !pluginInstalled
            ? "后端插件未安装"
            : !pluginEnabled
                ? "后端插件未启用（请在插件市场或 plugins 命令中启用）"
                : !runtimeAvailable
                    ? toolchain.Diagnostic ?? "运行时不可用"
                    : null;
        return new BackendProbe(available, runtimeAvailable, pluginInstalled, pluginEnabled,
            toolchain.SystemPath ?? toolchain.ManagedPath ?? toolchain.ScriptPath, reason);
    }

    private static IReadOnlyList<Row> BuildRows(AppDataLayout layout, PersistentSessionManager manager)
    {
        var preferred = manager.PreferredBackend;
        var rows = new List<Row>(Order.Length);
        foreach (var backend in Order)
        {
            var id = BackendCapabilities.StorageId(backend);
            var probe = Probe(layout, backend);
            rows.Add(new Row(id, BackendCapabilities.For(backend).Name, backend == preferred,
                probe.Available, PluginId(backend), probe.PluginInstalled, probe.PluginEnabled,
                probe.RuntimeAvailable, probe.RuntimePath, probe.Reason));
        }
        return rows;
    }

    private static string? PluginId(BackendId backend) => backend switch
    {
        BackendId.Pi => "backend.pi",
        BackendId.Omp => "backend.omp",
        BackendId.Claude => "backend.claude",
        BackendId.Codex => "backend.codex",
        BackendId.Dsh => "backend.dsh",
        _ => null
    };

    private static BackendId Parse(string value)
    {
        try { return BackendCapabilities.Parse(value); }
        catch (InvalidOperationException error) { throw new ArgumentException(error.Message, nameof(value)); }
    }
}
