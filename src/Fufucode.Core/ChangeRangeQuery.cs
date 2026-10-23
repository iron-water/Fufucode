using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Fufucode.Core;

public sealed record ChangedFile(string Path, string? OldPath, string Kind, string? IndexStatus,
    string? WorktreeStatus, bool Exists, bool? IsBinary, IReadOnlyList<string> ToolCallIds,
    int? AddedLines = null, int? RemovedLines = null)
{
    public bool? IsNew => Kind is "added" or "untracked" ? true : Kind is "written" or "missing" ? null : false;
    public bool? IsDeleted => Kind == "deleted" ? true : Exists ? false : null;
}
public sealed record ChangeRangeReport(string Scope, string Root, bool Available,
    IReadOnlyList<ChangedFile> Files, string? Error, int ExcludedToolPaths = 0);

/// <summary>Read-only ranges, never patches/revert/stage. Tool evidence is explicitly bounded to write/edit.</summary>
public static class ChangeRangeQuery
{
    public static async Task<ChangeRangeReport> WorkspaceAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var project = AppDataLayout.NormalizeProjectPath(projectPath);
        try
        {
            var top = await GitAsync(project, ["rev-parse", "--show-toplevel"], cancellationToken);
            if (top.Code != 0) return new("workspace", project, false, [], "not-a-git-worktree");
            var root = Path.GetFullPath(top.Output.Trim());
            var status = await GitAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
            if (status.Code != 0) return new("workspace", root, false, [], "git-status-failed");
            var head = await GitAsync(root, ["rev-parse", "--verify", "HEAD"], cancellationToken);
            var stats = new Dictionary<string, GitLineStat>(PathComparer);
            var diff = new List<string> { "diff", "--numstat", "-z", "--no-renames", "--no-ext-diff", "--no-textconv" };
            if (head.Code == 0) diff.Add("HEAD");
            var numstat = await GitAsync(root, diff, cancellationToken);
            if (numstat.Code != 0) return new("workspace", root, false, [], "git-numstat-failed");
            AddNumstat(numstat.Output, stats);
            if (head.Code != 0)
            {
                diff.Add("--cached");
                var cached = await GitAsync(root, diff, cancellationToken);
                if (cached.Code != 0) return new("workspace", root, false, [], "git-numstat-failed");
                AddNumstat(cached.Output, stats);
            }
            var parts = status.Output.Split('\0');
            var files = new List<ChangedFile>();
            // 未跟踪文件要读内容才知道行数；大仓库里可能有成千上万个，
            // 只给前 N 个计数，其余保持未知——宁可少一个 ± 也不拖慢整个面板。
            var untrackedBudget = 200;
            for (var i = 0; i < parts.Length; i++)
            {
                var item = parts[i]; if (item.Length == 0) continue;
                if (item.Length < 4) throw new InvalidDataException("Invalid git porcelain record.");
                var x = item[0]; var y = item[1]; var path = item[3..]; string? old = null;
                if (x is 'R' or 'C' || y is 'R' or 'C')
                {
                    if (++i >= parts.Length || parts[i].Length == 0) throw new InvalidDataException("Missing git rename source.");
                    old = parts[i];
                }
                var full = Path.GetFullPath(Path.Combine(root, path));
                if (!AppDataLayout.IsUnderDirectory(full, root)) throw new InvalidDataException("Git path escaped its root.");
                var stat = stats.TryGetValue(path, out var found) ? found :
                    old is not null && stats.TryGetValue(old, out found) ? found : null;
                var binary = stat?.IsBinary ?? ProbeBinary(full, root);
                var kind = x == '?' && y == '?' ? "untracked" : x == 'U' || y == 'U' || item[..2] is "AA" or "DD" ? "unmerged" :
                    x == 'R' || y == 'R' ? "renamed" : x == 'C' || y == 'C' ? "copied" :
                    x == 'D' || y == 'D' ? "deleted" : x == 'A' || y == 'A' ? "added" : x == 'T' || y == 'T' ? "type-changed" : "modified";
                // Untracked files are absent from numstat; they are entirely new, so counting their
                // lines is a real count rather than an estimate. Anything unreadable stays null.
                var added = stat?.Added;
                var removed = stat?.Removed;
                if (stat is null && kind == "untracked" && binary == false && untrackedBudget > 0 &&
                    CountLines(full) is { } lines)
                {
                    untrackedBudget--;
                    added = lines;
                    removed = 0;
                }
                files.Add(new(path, old, kind, x.ToString(), y.ToString(), File.Exists(full), binary, [],
                    added, removed));
            }
            return new("workspace", root, true, files.OrderBy(f => f.Path, PathComparer).ToArray(), null);
        }
        catch (System.ComponentModel.Win32Exception) { return new("workspace", project, false, [], "git-unavailable"); }
    }

    public static async Task<ChangeRangeReport> SessionWritesAsync(string projectPath, IEnumerable<PiMessage> messages,
        IReadOnlyList<PiToolCard>? liveTools = null, CancellationToken cancellationToken = default)
    {
        var project = AppDataLayout.NormalizeProjectPath(projectPath);
        var calls = new Dictionary<string, (string Name, JsonElement? Args)>(StringComparer.Ordinal);
        var succeeded = new List<(string Id, string Name, JsonElement? Args)>();
        foreach (var message in messages)
        {
            if (message.Role == "assistant")
                foreach (var tool in message.Content.OfType<PiToolCallBlock>()) calls[tool.Id] = (tool.Name, tool.Arguments);
            if (message.Role == "toolResult" && message.Raw.ValueKind == JsonValueKind.Object &&
                message.Raw.TryGetProperty("toolCallId", out var id) && id.ValueKind == JsonValueKind.String &&
                message.Raw.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.False &&
                calls.TryGetValue(id.GetString()!, out var completed))
                succeeded.Add((id.GetString()!, completed.Name, completed.Args));
        }
        foreach (var tool in liveTools ?? [])
        {
            if (tool.IsFinal && tool.IsError == false) succeeded.Add((tool.ToolCallId, tool.ToolName, tool.Args));
        }
        var writes = new Dictionary<string, List<string>>(PathComparer); var excluded = 0;
        foreach (var tool in succeeded)
        {
            if (tool.Name is not ("write" or "edit") ||
                tool.Args is not { ValueKind: JsonValueKind.Object } args ||
                !args.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) continue;
            try
            {
                var full = Path.GetFullPath(path.GetString()!, project);
                if (!AppDataLayout.IsUnderDirectory(full, project)) { excluded++; continue; }
                var relative = Path.GetRelativePath(project, full).Replace('\\', '/');
                if (!writes.TryGetValue(relative, out var ids)) writes[relative] = ids = [];
                ids.Add(tool.Id);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException) { excluded++; }
        }
        var workspace = await WorkspaceAsync(project, cancellationToken);
        var changes = workspace.Files.ToDictionary(f => Path.GetFullPath(Path.Combine(workspace.Root, f.Path)), PathComparer);
        var files = writes.OrderBy(pair => pair.Key, PathComparer).Select(pair =>
        {
            var full = Path.Combine(project, pair.Key);
            changes.TryGetValue(full, out var git);
            var old = git?.OldPath is { } oldPath ? Path.GetRelativePath(project, Path.Combine(workspace.Root, oldPath)).Replace('\\', '/') : null;
            return new ChangedFile(pair.Key, old, git?.Kind ?? (File.Exists(full) ? "written" : "missing"),
                git?.IndexStatus, git?.WorktreeStatus, File.Exists(full), git?.IsBinary ?? ProbeBinary(full, project),
                pair.Value.Distinct(StringComparer.Ordinal).ToArray(), git?.AddedLines, git?.RemovedLines);
        }).ToArray();
        return new("session-writes", project, true, files, null, excluded);
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Line count of an untracked text file; null when it cannot be read cleanly.</summary>
    private static int? CountLines(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > DiffQuery.MaximumSideBytes) return null;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var count = 0;
            while (reader.ReadLine() is not null) count++;
            return count;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool? ProbeBinary(string file, string root)
    {
        try
        {
            for (var current = file; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
                if (PathComparer.Equals(current, root)) break;
            }
            if (!File.Exists(file)) return null;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[8000]; var count = stream.Read(buffer);
            return buffer.AsSpan(0, count).Contains((byte)0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>git numstat columns. A "-" column means binary, so no line count is claimed.</summary>
    private static void AddNumstat(string output, IDictionary<string, GitLineStat> values)
    {
        foreach (var item in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var first = item.IndexOf('\t'); var second = first < 0 ? -1 : item.IndexOf('\t', first + 1);
            if (second < 0) throw new InvalidDataException("Invalid git numstat record.");
            var path = item[(second + 1)..];
            var addedText = item[..first]; var removedText = item[(first + 1)..second];
            var binary = addedText == "-" || removedText == "-";
            var added = binary ? null : int.TryParse(addedText, out var parsedAdded) ? parsedAdded : (int?)null;
            var removed = binary ? null : int.TryParse(removedText, out var parsedRemoved) ? parsedRemoved : (int?)null;
            if (values.TryGetValue(path, out var previous))
                values[path] = new(binary || previous.IsBinary, added ?? previous.Added, removed ?? previous.Removed);
            else values[path] = new(binary, added, removed);
        }
    }

    internal sealed record GitLineStat(bool IsBinary, int? Added, int? Removed);

    private static async Task<(int Code, string Output)> GitAsync(string directory, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "--no-optional-locks", "-C", directory, "-c", "core.fsmonitor=false" }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start git.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        await error;
        return (process.ExitCode, await output);
    }
}
