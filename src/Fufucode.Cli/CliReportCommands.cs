using System.Text.Json;
using Fufucode.Core;

namespace Fufucode.Cli;

/// <summary>Offline queries: do not connect Pi, migrate metadata, or change the active GUI session.</summary>
public static class CliReportCommands
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> ChangesAsync(CliOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        var args = options.Arguments;
        var scope = "workspace";
        if (args.Count >= 2 && args[0] == "diff") return await DiffAsync(options, output, cancellationToken);
        if (args.Count == 3 && args[0] == "list" && args[1] == "--scope") scope = args[2];
        else if (!args.SequenceEqual(["list"])) throw new ArgumentException("Usage: changes list [--scope workspace|session].");
        if (scope is not ("workspace" or "session")) throw new ArgumentException("Unknown changes scope.");
        string? sessionId = null; var partial = false;
        ChangeRangeReport report;
        if (scope == "workspace") report = await ChangeRangeQuery.WorkspaceAsync(options.ProjectPath, cancellationToken);
        else
        {
            var source = ReadOnlySessionReport.Load(new AppDataLayout(options.AppDataRoot), options.ProjectPath, options.SessionId);
            sessionId = source.SessionId; partial = source.PartialTail;
            report = await ChangeRangeQuery.SessionWritesAsync(options.ProjectPath, source.History.CurrentBranchMessages, cancellationToken: cancellationToken);
        }
        if (options.Json) await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, sessionId, partialTail = partial, report }, Json));
        else if (!report.Available) await output.WriteLineAsync("Changes unavailable: " + report.Error);
        else
        {
            await output.WriteLineAsync("Scope: " + report.Scope + "  Root: " + report.Root);
            foreach (var file in report.Files)
            {
                var counts = file.AddedLines is null && file.RemovedLines is null ? "lines=unknown"
                    : $"+{file.AddedLines?.ToString() ?? "?"}/-{file.RemovedLines?.ToString() ?? "?"}";
                await output.WriteLineAsync($"{file.Kind}  {file.Path}  {counts}  binary={file.IsBinary?.ToString().ToLowerInvariant() ?? "unknown"}");
            }
        }
        return report.Available ? 0 : 3;
    }

    /// <summary>Read-only unified diff for one path. Never stages, restores or reverts.</summary>
    public static async Task<int> DiffAsync(CliOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        var args = options.Arguments;
        var context = DiffQuery.DefaultContextLines;
        if (args.Count < 2 || args[0] != "diff") throw new ArgumentException("Usage: changes diff <path> [--context N].");
        var path = args[1];
        for (var i = 2; i < args.Count; i++)
        {
            if (args[i] == "--context")
            {
                if (i + 1 >= args.Count || !int.TryParse(args[i + 1], out context))
                    throw new ArgumentException("changes diff --context needs a number of lines.");
                i++;
            }
            else throw new ArgumentException($"Unknown changes diff option: {args[i]}.");
        }
        var diff = await DiffQuery.FileAsync(options.ProjectPath, path, context, cancellationToken: cancellationToken);
        if (options.Json) await output.WriteLineAsync(JsonSerializer.Serialize(new
        { schemaVersion = 1, path = diff.Path, oldPath = diff.OldPath, isBinary = diff.IsBinary,
            truncated = diff.Truncated, context, error = diff.Error,
            hunks = diff.Hunks.Select(h => new { header = h.Header, oldStart = h.OldStart, oldLines = h.OldLines,
                newStart = h.NewStart, newLines = h.NewLines,
                lines = h.Lines.Select(l => new { kind = l.Kind.ToString(), text = l.Text }) }) }, Json));
        else if (diff.Error is { } error) await output.WriteLineAsync("Diff unavailable: " + error);
        else if (diff.IsBinary == true) await output.WriteLineAsync($"{diff.Path}: binary file");
        else
        {
            await output.WriteLineAsync($"--- {diff.Path}");
            await output.WriteLineAsync($"+++ {diff.Path}");
            foreach (var hunk in diff.Hunks)
            {
                await output.WriteLineAsync(hunk.Header);
                foreach (var line in hunk.Lines) await output.WriteLineAsync(line.Kind + line.Text);
            }
            if (diff.Truncated) await output.WriteLineAsync("… diff truncated at the configured line limit");
        }
        return diff.Error is null ? 0 : 3;
    }

    public static async Task<int> UsageAsync(CliOptions options, TextWriter output)
    {
        if (!options.Arguments.SequenceEqual(["show"])) throw new ArgumentException("Usage: usage show [--session ID].");
        var source = ReadOnlySessionReport.Load(new AppDataLayout(options.AppDataRoot), options.ProjectPath, options.SessionId);
        var usage = SessionUsageQuery.QueryHistory(source.History);
        if (options.Json) await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, scope = "recorded-session-all-branches",
            sessionId = source.SessionId, partialTail = source.PartialTail, report = usage }, Json));
        else
        {
            static string Value(UsageCounter counter) => counter.Value?.ToString() ?? "unknown";
            await output.WriteLineAsync($"Session: {source.SessionId ?? "none"}  recorded total tokens: {Value(usage.Session.TotalTokens)}");
            foreach (var turn in usage.Turns) await output.WriteLineAsync($"Turn {turn.TurnNumber}: {Value(turn.Usage.TotalTokens)}");
        }
        return 0;
    }
}
