using System;
using System.Threading.Tasks;
using Fufucode.Core;
using Fufucode.Core.Features.LocalReview;

namespace Fufucode.App.Features.LocalReview;

/// <summary>
/// Host requests the module must not perform itself: opening a worktree, jumping to a file:line
/// in the editor, choosing an export destination, and clipboard access. The review module only
/// raises these; the host (Codex wiring) decides what they mean and owns the windows.
/// </summary>
public interface ILocalReviewHostRequests
{
    void OpenWorktree(string worktreePath);
    void JumpToLocation(string absoluteFilePath, int line);
    Task<string?> PickExportPathAsync(string suggestedFileName);
    Task CopyToClipboardAsync(string text);
}

/// <summary>Row shown in the findings list. Line numbers always belong to the frozen snapshot.</summary>
public sealed class LocalReviewFindingItem
{
    public LocalReviewFindingItem(LocalReviewFinding finding, bool stale)
    {
        Finding = finding;
        Stale = stale;
        Priority = finding.Priority.ToString();
        Location = finding.Location;
        Explanation = finding.Explanation;
        Evidence = finding.Evidence;
    }

    public LocalReviewFinding Finding { get; }
    public bool Stale { get; }
    public string Priority { get; }
    public string Location { get; }
    public string Explanation { get; }
    public string Evidence { get; }
    public string LocationWithState => Location + (Stale ? "（快照已过期）" : string.Empty);
    /// <summary>
    /// 列表项单行文本。Avalonia 的 TextBlock.Inlines/&lt;Run&gt; 不参与绑定求值，
    /// 拆成多个 Run 会导致内容不渲染；这里用一个计算属性保证真实可见文本。
    /// </summary>
    public string Header => Priority + " " + LocationWithState;
    public string Detail => string.IsNullOrWhiteSpace(Evidence)
        ? Explanation
        : Explanation + "｜证据：" + Evidence;
}

/// <summary>Row for a response item the strict parser refused to accept as located evidence.</summary>
public sealed class LocalReviewRejectedItem
{
    public LocalReviewRejectedItem(LocalReviewParseIssue issue)
    {
        Reason = issue.Reason;
        Where = issue.Path is null ? "-" : issue.Path + ":" + (issue.Line?.ToString() ?? "-");
        Detail = issue.Detail;
    }

    public string Reason { get; }
    public string Where { get; }
    public string? Detail { get; }
    public string Summary => Where + "：" + Reason + (Detail is null ? "" : "（" + Detail + "）");
}

/// <summary>Row in the worktree list.</summary>
public sealed class LocalReviewWorktreeItem
{
    public LocalReviewWorktreeItem(GitWorktreeInfo info)
    {
        Path = info.Path;
        Head = info.HeadCommit.Length >= 8 ? info.HeadCommit[..8] : info.HeadCommit;
        Branch = info.Detached ? "detached" : info.Branch ?? "-";
        Prunable = info.Prunable;
        Locked = info.LockReason is not null;
        Label = Path + "  [" + Branch + " " + Head + "]" +
            (Prunable ? "  [目录缺失]" : string.Empty) + (Locked ? "  [locked]" : string.Empty);
    }

    public string Path { get; }
    public string Head { get; }
    public string Branch { get; }
    public bool Prunable { get; }
    public bool Locked { get; }
    public string Label { get; }
}

/// <summary>Row in the scope chooser: an explicit scope the user can select.</summary>
public sealed record LocalReviewScopeChoice(LocalReviewScopeKind Kind, string Label)
{
    public override string ToString() => Label;
}
