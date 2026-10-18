using System.Text;

namespace Fufucode.Core.Features.LocalReview;

public enum LocalReviewRunStatus
{
    /// <summary>The reviewer reached a terminal state and the payload parsed strictly.</summary>
    Completed,
    /// <summary>Terminal state reached, payload parsed, and the reviewer reported nothing wrong.</summary>
    NoFindings,
    Failed,
    Cancelled,
    /// <summary>The run reached a terminal state but its payload did not parse as a review result.
    /// Reported separately from <see cref="NoFindings"/>: an unreadable answer is never "no issues",
    /// and an answer whose every finding was rejected is likewise not a finished review.</summary>
    InvalidResponse,
    /// <summary>Part of the requested scope never reached the reviewer (per-file or total byte
    /// budget, missing bytes, or binary content). The review cannot be presented as clean.</summary>
    IncompleteScope,
    /// <summary>The review itself finished, but the repository changed after the snapshot was fixed:
    /// locations are kept for reference and must be shown as stale, never as current-HEAD facts.</summary>
    StaleAfterReview,
}

/// <summary>
/// One finished local review: the resolved scope, the frozen snapshot it was based on, the
/// strictly parsed findings, and every response item we refused to accept as located evidence.
/// A model response is never a proof of completion by itself; <see cref="Status"/> comes from the
/// executor's terminal-state check, and <see cref="ReviewComplete"/> requires that the whole
/// requested scope actually reached the reviewer.
/// </summary>
public sealed record LocalReviewRunResult(
    LocalReviewScopeResolution Scope,
    FileDiffSnapshot? Snapshot,
    LocalReviewRunStatus Status,
    IReadOnlyList<LocalReviewFinding> Findings,
    IReadOnlyList<LocalReviewParseIssue> Rejected,
    string? Model,
    string? RunId,
    string? Error,
    bool ResponseTruncated,
    DateTimeOffset FinishedAtUtc)
{
    /// <summary>Paths whose content never reached the reviewer (budget, missing bytes, binary).</summary>
    public IReadOnlyList<string> UnreviewedPaths { get; init; } = [];

    /// <summary>Scratch directory kept on purpose because the run's end state is unknown; null when
    /// the run reached a definite terminal state and its scratch was released.</summary>
    public string? RetainedScratch { get; init; }

    /// <summary>True when every scoped file actually reached the reviewer.</summary>
    public bool ScopeFullySent => UnreviewedPaths.Count == 0;

    /// <summary>
    /// Staleness has two sources: the capture itself observed concurrent changes (entry Stale /
    /// concurrent-changes-detected), or a later freshness re-check against the same snapshot failed
    /// and the run was classified StaleAfterReview. Both must report stale — a result whose status
    /// says "expired" can never claim the snapshot is current.
    /// </summary>
    public bool IsStale => Status == LocalReviewRunStatus.StaleAfterReview ||
        (Snapshot is not null && (Snapshot.Entries.Any(entry => entry.Stale) ||
            Snapshot.Error == "concurrent-changes-detected"));

    /// <summary>
    /// True only when a terminal reviewer state produced a strictly parsed payload over the whole
    /// requested scope. An incomplete scope (content withheld by budget/binary/missing bytes) is
    /// never complete, even if the reviewer answered "nothing found".
    /// </summary>
    public bool ReviewComplete => ScopeFullySent && !ResponseTruncated && RunId is not null &&
        Status is (LocalReviewRunStatus.Completed or LocalReviewRunStatus.NoFindings or
            LocalReviewRunStatus.StaleAfterReview);

    public string StatusText => Status switch
    {
        LocalReviewRunStatus.Completed => "审查完成",
        LocalReviewRunStatus.NoFindings => "审查完成：没有发现项",
        LocalReviewRunStatus.Failed => "审查失败" + (Error is null ? "" : "：" + Error),
        LocalReviewRunStatus.InvalidResponse => "审查响应无效，不是完成审查" + (Error is null ? "" : "：" + Error),
        LocalReviewRunStatus.IncompleteScope => "审查不完整：" + IncompleteNote,
        LocalReviewRunStatus.Cancelled => "已取消：本次运行结果未知，需人工对账后才能重发",
        LocalReviewRunStatus.StaleAfterReview => "审查完成，但快照已过期：定位对应当前内容不可保证",
        _ => Status.ToString(),
    };

    public string IncompleteNote => UnreviewedPaths.Count == 0
        ? "有内容未送审"
        : $"{UnreviewedPaths.Count} 个文件的内容未送审（{string.Join(", ", UnreviewedPaths.Take(6))}" +
          (UnreviewedPaths.Count > 6 ? " 等" : "") + "），不能当作全范围干净审查";

    public string ScopeDescription => Scope.Description +
        (Snapshot is null ? "" : "，快照 HEAD " + Short(Snapshot.HeadCommit));

    private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "-" :
        sha.Length >= 8 ? sha[..8] : sha;
}

/// <summary>
/// Narrow executor seam for the review module. The module never assembles CLI flags and never
/// owns a runtime: the host injects an implementation bound to its registered runtime
/// (see LocalReviewHarnessExecutor for the verified harness-backed adapter).
/// </summary>
public interface ILocalReviewExecutor
{
    /// <summary>Model identifier bound to the injected runtime, shown in the UI and exports.</summary>
    string Model { get; }

    Task<LocalReviewRunResult> ReviewAsync(LocalReviewScopeResolution scope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Payload builder shared by adapters and tests. Both budgets are counted in <b>UTF-8 bytes</b>,
/// the fixed prompt and metadata are charged against the total, and anything withheld is reported
/// in <see cref="PayloadResult.UnreviewedPaths"/> instead of silently shrinking the reviewed scope.
/// </summary>
public static class LocalReviewPayload
{
    public const int PerFileLimitBytes = 64 * 1024;
    public const int TotalLimitBytes = 512 * 1024;
    private const int ReserveBytes = 8 * 1024;
    private const int MaxListedUnreviewed = 100;

    public sealed record PayloadResult(string Text, IReadOnlyList<string> UnreviewedPaths,
        long ByteCount, bool WithinBudget);

    /// <summary>Compatibility wrapper returning only the text.</summary>
    public static string Build(LocalReviewScopeResolution scope, FileDiffSnapshot snapshot) =>
        BuildResult(scope, snapshot).Text;

    public static PayloadResult BuildResult(LocalReviewScopeResolution scope, FileDiffSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(snapshot);

        var header = new StringBuilder();
        header.Append("Review these frozen repository contents. Line numbers refer to each file's captured bytes (1-based).\n\n");
        header.Append("SCOPE: ").Append(scope.Description).Append('\n');
        header.Append("REPOSITORY: ").Append(snapshot.RepositoryRoot).Append('\n');
        header.Append("SNAPSHOT HEAD: ").Append(snapshot.HeadCommit).Append('\n');
        header.Append("SOURCE: ").Append(snapshot.Source).Append("\nBASE: ").Append(snapshot.BaseCommit)
            .Append("\nTARGET: ").Append(snapshot.TargetCommit).Append('\n');
        if (!string.IsNullOrEmpty(snapshot.Error))
            header.Append("SNAPSHOT NOTE: ").Append(snapshot.Error).Append('\n');
        header.Append('\n');
        var footer = "\n" + LocalReviewFindingsParser.ResponseContract + "\n";

        // Everything that is always emitted is charged first, so content can never push the
        // assembled payload past the byte budget.
        var fixedBytes = Bytes(header) + Bytes(footer);
        var contentBudget = Math.Max(0, TotalLimitBytes - fixedBytes - ReserveBytes);

        var body = new StringBuilder();
        var unreviewed = new List<string>();
        var used = 0;
        foreach (var entry in snapshot.Entries)
        {
            var head = new StringBuilder("FILE: ").Append(entry.RelativePath)
                .Append(" [").Append(entry.ExistedAtCapture ? "present" : "absent")
                .Append(entry.TrackedAtCapture ? ", tracked" : ", untracked");
            if (entry.Stale) head.Append(", STALE");
            if (entry.Error is not null) head.Append(", error=").Append(entry.Error);
            head.Append("]\n");

            string note;
            if (entry.Bytes is null)
                note = "(no captured bytes: this entry was not reviewable)\n\n";
            else if (entry.Diff.IsBinary == true)
                note = "(binary content not sent: findings here cannot be located)\n\n";
            else
            {
                var numbered = NumberLines(entry.Bytes);
                var patch = new StringBuilder("PATCH (old removals use base line numbers; findings use target lines):\n");
                if (entry.Diff.OldMode != entry.Diff.NewMode)
                    patch.Append("mode ").Append(entry.Diff.OldMode ?? "absent").Append(" -> ")
                        .Append(entry.Diff.NewMode ?? "absent").Append('\n');
                foreach (var hunk in entry.Diff.Hunks)
                {
                    patch.Append(hunk.Header).Append('\n');
                    foreach (var line in hunk.Lines) patch.Append(line.Kind).Append(line.Text).Append('\n');
                }
                if (entry.Diff.Truncated || entry.Diff.Error is not null)
                {
                    unreviewed.Add(entry.RelativePath);
                    continue;
                }
                var content = patch + "TARGET CONTENT:\n" + numbered;
                if (Bytes(content) > PerFileLimitBytes)
                    note = "(content not sent: exceeds the per-file byte limit)\n\n";
                else
                {
                    var block = head + content + "\n";
                    var blockBytes = Bytes(block);
                    if (used + blockBytes > contentBudget)
                    {
                        // Whole file withheld: the scope is incomplete, never "clean".
                        unreviewed.Add(entry.RelativePath);
                        continue;
                    }
                    body.Append(block);
                    used += blockBytes;
                    continue;
                }
            }
            unreviewed.Add(entry.RelativePath);
            var marker = head + note;
            if (used + Bytes(marker) <= contentBudget) { body.Append(marker); used += Bytes(marker); }
        }

        var omission = new StringBuilder();
        if (unreviewed.Count > 0)
        {
            omission.Append("\nNOT REVIEWED IN THIS PAYLOAD (").Append(unreviewed.Count).Append("): ")
                .AppendJoin(", ", unreviewed.Take(MaxListedUnreviewed));
            if (unreviewed.Count > MaxListedUnreviewed)
                omission.Append(" …(+" + (unreviewed.Count - MaxListedUnreviewed) + " more)");
            omission.Append("\nThis is an incomplete review: do not report the scope as clean.\n");
        }

        var text = header.ToString() + body + omission + footer;
        if (Bytes(text) > TotalLimitBytes)
        {
            // Pathological case (very many long paths in the note): fall back to the guaranteed
            // skeleton and keep the scope marked incomplete.
            foreach (var entry in snapshot.Entries)
                if (!unreviewed.Contains(entry.RelativePath, StringComparer.Ordinal))
                    unreviewed.Add(entry.RelativePath);
            text = "Payload exceeds the byte budget, including metadata. No scope content was sent.\n"
                + footer;
        }
        var bytes = Bytes(text);
        return new PayloadResult(text, unreviewed, bytes, bytes <= TotalLimitBytes);
    }

    private static int Bytes(StringBuilder value) => Encoding.UTF8.GetByteCount(value.ToString());

    private static int Bytes(string value) => Encoding.UTF8.GetByteCount(value);

    private static string NumberLines(byte[] bytes)
    {
        var content = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Replace('\r', '\n');
        if (content.Length == 0) return "(empty file)\n";
        var lines = content.Split('\n');
        if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1];
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
            builder.Append(i + 1).Append('\t').Append(lines[i]).Append('\n');
        return builder.ToString();
    }
}
