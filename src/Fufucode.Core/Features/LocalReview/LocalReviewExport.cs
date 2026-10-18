using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fufucode.Core.Features.LocalReview;

/// <summary>
/// Export shapes for one review result. Every export names the scope, the frozen commits, and the
/// staleness state, so a copied document can never be mistaken for a current-HEAD claim.
/// </summary>
public static class LocalReviewExport
{
    public sealed record FindingDto(string Priority, string Path, int Line, string Explanation,
        string Evidence);

    public sealed record ReviewDocument(string Schema, string Scope, string? RepositoryRoot,
        string? HeadCommit, string? BaseCommit, string? TargetCommit, string CapturedAtUtc,
        bool Stale, string? Model, string? RunId, string Status,
        IReadOnlyList<FindingDto> Findings, IReadOnlyList<string> Rejected)
    {
        /// <summary>Scoped files whose content never reached the reviewer. Non-empty means the
        /// document covers only part of the requested scope, so it must never read as a clean review.</summary>
        public IReadOnlyList<string> Unreviewed { get; init; } = [];
        /// <summary>True only when the whole requested scope was actually reviewed.</summary>
        public bool Complete { get; init; }
    }

    public const string SchemaId = "fufucode.local-review.v1";

    public static ReviewDocument ToDocument(LocalReviewRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var snapshot = result.Snapshot;
        return new ReviewDocument(SchemaId, result.Scope.Description,
            snapshot?.RepositoryRoot, snapshot?.HeadCommit, result.Scope.BaseCommit,
            result.Scope.TargetCommit,
            (snapshot?.CapturedAtUtc ?? result.FinishedAtUtc).ToString("O"),
            result.IsStale, result.Model, result.RunId, result.Status.ToString(),
            result.Findings.Select(f => new FindingDto(f.Priority.ToString(), f.Path, f.Line,
                f.Explanation, f.Evidence)).ToList(),
            result.Rejected.Select(r => r.Reason + ": " + (r.Path ?? "-") + ":" +
                (r.Line?.ToString() ?? "-") + (r.Detail is null ? "" : " (" + r.Detail + ")")).ToList())
        {
            Unreviewed = result.UnreviewedPaths,
            Complete = result.ReviewComplete,
        };
    }

    public static string ToJson(LocalReviewRunResult result) => ToJson(ToDocument(result));

    public static string ToJson(ReviewDocument document) =>
        JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    public static string ToMarkdown(LocalReviewRunResult result) => ToMarkdown(ToDocument(result));

    public static string ToMarkdown(ReviewDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 本地代码审查结果");
        builder.AppendLine();
        builder.AppendLine($"- 范围：{document.Scope}");
        builder.AppendLine($"- 仓库：{document.RepositoryRoot ?? "-"}");
        builder.AppendLine($"- 基准 / 目标：{Short(document.BaseCommit)} / {Short(document.TargetCommit)}");
        builder.AppendLine($"- 快照 HEAD：`{Short(document.HeadCommit)}`（捕获时间 {document.CapturedAtUtc}）");
        builder.AppendLine($"- 模型：{document.Model ?? "-"}，运行：{document.RunId ?? "-"}");
        builder.AppendLine($"- 状态：{document.Status}");
        builder.AppendLine($"- 覆盖完整性：{(document.Complete
            ? "范围内全部内容已送审"
            : $"**未完成有效审查**：{document.Unreviewed.Count} 个文件未送审（" +
              string.Join(", ", document.Unreviewed.Take(6)) +
              (document.Unreviewed.Count > 6 ? " 等" : "") + "），本结果不代表全范围干净")}");
        builder.AppendLine($"- 快照有效性：{(document.Stale
            ? "**已过期**：快照固定后仓库发生变化，以下定位不保证对应当前内容"
            : "捕获时未发现过期；当前状态需重新验证")}");
        builder.AppendLine();
        if (document.Findings.Count == 0)
            builder.AppendLine("没有可定位的发现项。模型未报告问题不等于代码正确，本导出不是正确性证明。");
        foreach (var group in document.Findings.GroupBy(f => f.Priority)
                     .OrderBy(g => LocalReviewFindingsParser.PriorityRank(
                         Enum.TryParse<LocalReviewPriority>(g.Key, true, out var p) ? p : LocalReviewPriority.Low)))
        {
            builder.AppendLine($"## {group.Key}（{group.Count()} 项）");
            foreach (var finding in group)
            {
                builder.AppendLine($"- `{finding.Path}:{finding.Line}` {finding.Explanation}");
                if (!string.IsNullOrWhiteSpace(finding.Evidence))
                    builder.AppendLine($"  - 证据：`{Collapse(finding.Evidence)}`");
            }
            builder.AppendLine();
        }
        if (document.Rejected.Count > 0)
        {
            builder.AppendLine($"## 被拒绝的无效定位（{document.Rejected.Count} 项）");
            foreach (var rejected in document.Rejected) builder.AppendLine($"- {rejected}");
            builder.AppendLine();
        }
        builder.AppendLine("> 由本地审查模块生成：只读取固定快照，未修改任何文件，未自动接受或集成。");
        return builder.ToString();
    }

    /// <summary>Default file name for an export; contains no user path material.</summary>
    public static string SuggestedFileName(LocalReviewRunResult result, string extension) =>
        "review-" + (result.Snapshot?.HeadCommit is { Length: >= 8 } head ? head[..8] : "unknown") +
        "-" + result.FinishedAtUtc.ToString("yyyyMMdd-HHmmss") + extension;

    private static string Collapse(string value) => value.ReplaceLineEndings(" ").Trim();

    private static string Short(string? sha) => sha is { Length: >= 8 } value ? value[..8] : sha ?? "-";
}
