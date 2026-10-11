using System.Diagnostics;
using System.Text;

namespace Fufucode.Core;

/// <summary>One line of a unified diff. Kind is '+' added, '-' removed, ' ' context or '\' for a no-newline marker.</summary>
public sealed record DiffLine(char Kind, string Text);

public sealed record DiffHunk(string Header, int OldStart, int OldLines, int NewStart, int NewLines,
    IReadOnlyList<DiffLine> Lines);

/// <summary>Read-only unified diff for one file. It never writes, stages, restores or reverts anything.</summary>
public sealed record FileDiff(string Path, string? OldPath, bool? IsBinary, bool Truncated,
    IReadOnlyList<DiffHunk> Hunks, string? Error);

public static class DiffQuery
{
    public const int DefaultContextLines = 3;
    public const int MaximumContextLines = 64;
    /// <summary>A diff is a view, not a document: keep the payload bounded so a large file cannot stall the caller.</summary>
    public const int MaximumDiffLines = 20000;

    public static async Task<FileDiff> FileAsync(string projectPath, string relativePath,
        int contextLines = DefaultContextLines, int maximumLines = MaximumDiffLines,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (contextLines < 0 || contextLines > MaximumContextLines)
            throw new ArgumentOutOfRangeException(nameof(contextLines), "Context must be between 0 and 64 lines.");
        if (maximumLines < 1) throw new ArgumentOutOfRangeException(nameof(maximumLines), "The diff line limit must be positive.");
        var project = AppDataLayout.NormalizeProjectPath(projectPath);
        try
        {
            var top = await GitAsync(project, ["rev-parse", "--show-toplevel"], cancellationToken);
            if (top.Code != 0) return Failed(relativePath, false, "not-a-git-worktree");
            var root = Path.GetFullPath(top.Output.Trim());
            var full = Resolve(project, relativePath, out var display);
            if (full is null) return Failed(display, false, "path-escapes-project");
            if (HasReparsePoint(full, project)) return Failed(display, false, "reparse-point-refused");
            var head = await GitAsync(root, ["rev-parse", "--verify", "HEAD"], cancellationToken);
            var tracked = await GitAsync(root, ["ls-files", "--error-unmatch", "--", Path.GetRelativePath(root, full).Replace('\\', '/')], cancellationToken);
            if (head.Code != 0 || tracked.Code != 0)
            {
                if (!File.Exists(full)) return new(display, null, null, false, [], null);
                var added = await GitAsync(root, ["diff", "--no-index", "--no-ext-diff", "--no-textconv", "--no-color", $"-U{contextLines}", "--", OperatingSystem.IsWindows() ? "NUL" : "/dev/null", full], cancellationToken);
                return added.Code is 0 or 1 ? Parse(display, added.Output, maximumLines) : Failed(display, false, "git-diff-failed");
            }
            var args = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--no-color",
                $"-U{contextLines}" };
            args.Add("HEAD");
            args.Add("--");
            args.Add(Path.GetRelativePath(root, full).Replace('\\', '/'));
            var diff = await GitAsync(root, args, cancellationToken);
            if (diff.Code != 0) return Failed(display, false, "git-diff-failed");
            return Parse(display, diff.Output, maximumLines);
        }
        catch (System.ComponentModel.Win32Exception) { return Failed(relativePath, false, "git-unavailable"); }
    }

    private static FileDiff Failed(string path, bool truncated, string error) =>
        new(path, null, null, truncated, [], error);

    private static FileDiff Parse(string path, string output, int maximumLines)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var hunks = new List<DiffHunk>();
        var body = new List<DiffLine>();
        var header = "";
        var oldStart = 0; var oldLines = 0; var newStart = 0; var newLines = 0;
        var active = false;
        var truncated = false;
        var emitted = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("Binary files ", StringComparison.Ordinal)) return new(path, null, true, false, [], null);
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal) ||
                line.StartsWith("old mode ", StringComparison.Ordinal) || line.StartsWith("new mode ", StringComparison.Ordinal) ||
                line.StartsWith("similarity ", StringComparison.Ordinal) || line.StartsWith("rename ", StringComparison.Ordinal) ||
                line.StartsWith("deleted file", StringComparison.Ordinal) || line.StartsWith("new file", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }
            if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                var parsed = HunkHeader(line);
                header = line;
                oldStart = parsed.OldStart; oldLines = parsed.OldLines;
                newStart = parsed.NewStart; newLines = parsed.NewLines;
                active = true;
                continue;
            }
            if (!active || line.Length == 0) continue;
            if (emitted >= maximumLines) { truncated = true; break; }
            var kind = line[0] is '+' or '-' or ' ' or '\\' ? line[0] : ' ';
            body.Add(new DiffLine(kind, line[1..]));
            emitted++;
        }
        Flush();
        return new(path, null, hunks.Count == 0 ? (bool?)null : false, truncated, hunks, null);

        void Flush()
        {
            if (body.Count == 0 && header.Length == 0) return;
            hunks.Add(new DiffHunk(header, oldStart, oldLines, newStart, newLines, body.ToArray()));
            body.Clear();
            header = "";
            active = false;
        }
    }

    private static (int OldStart, int OldLines, int NewStart, int NewLines) HunkHeader(string line)
    {
        // @@ -a,b +c,d @@ optional section heading
        var end = line.IndexOf("@@", 2, StringComparison.Ordinal);
        var body = (end < 0 ? line[2..] : line[2..end]).Trim();
        var parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        static (int Start, int Count) Range(string value)
        {
            var text = value.StartsWith('-') || value.StartsWith('+') ? value[1..] : value;
            var comma = text.IndexOf(',');
            var start = int.TryParse(comma < 0 ? text : text[..comma], out var parsedStart) ? parsedStart : 0;
            var count = comma < 0 ? 1 : int.TryParse(text[(comma + 1)..], out var parsedCount) ? parsedCount : 0;
            return (start, count);
        }
        (int Start, int Count) old = parts.Length > 0 ? Range(parts[0]) : (0, 0);
        (int Start, int Count) updated = parts.Length > 1 ? Range(parts[1]) : (0, 0);
        return (old.Start, old.Count, updated.Start, updated.Count);
    }

    private static string? Resolve(string project, string relativePath, out string display)
    {
        display = relativePath.Replace('\\', '/');
        try
        {
            var full = Path.GetFullPath(Path.Combine(project, relativePath));
            // A diff is only produced for a path inside the requested project; ".." is refused.
            if (!AppDataLayout.IsUnderDirectory(full, project)) return null;
            display = Path.GetRelativePath(project, full).Replace('\\', '/');
            return full;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool HasReparsePoint(string file, string root)
    {
        try
        {
            for (var current = file; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                if (string.Equals(Path.GetFullPath(current), root, StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
        return false;
    }

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
