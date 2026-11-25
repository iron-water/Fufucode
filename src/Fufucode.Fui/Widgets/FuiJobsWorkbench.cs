namespace Fufucode.Fui;

internal static class FuiJobsWorkbench
{
    internal const string Title = "后台作业 · 只读";
    internal const string Footer = "只读 · ↑↓ 选择 · Enter 查看 · Esc 关闭";
    internal const string GroupPrefix = "job-group:";

    internal static bool IsTitle(string title) =>
        string.Equals(title, Title, StringComparison.Ordinal);

    internal static bool IsGroupChoice(FuiChoice choice) =>
        choice.Value.StartsWith(GroupPrefix, StringComparison.Ordinal);

    internal static IReadOnlyList<FuiChoice> Choices(FuiAgentActivitySnapshot snapshot)
    {
        if (snapshot.Jobs.Count == 0) return [];
        var choices = new List<FuiChoice>();
        AddGroup("running", "运行中", snapshot.Jobs.Where(job => job.State == FuiAgentState.Running));
        AddGroup("failed", "失败/截断", snapshot.Jobs.Where(job =>
            job.State is FuiAgentState.Failed or FuiAgentState.Truncated));
        AddGroup("settled", "已完成/取消/未知", snapshot.Jobs.Where(job =>
            job.State is FuiAgentState.Completed or FuiAgentState.Cancelled or FuiAgentState.Unknown));
        return choices;

        void AddGroup(string key, string label, IEnumerable<FuiAgentJob> source)
        {
            var jobs = source.OrderBy(job => FuiAgentActivityRoster.StateOrder(job.State))
                .ThenByDescending(job => job.LastAt)
                .ThenBy(job => job.JobId, StringComparer.Ordinal)
                .ToArray();
            if (jobs.Length == 0) return;
            choices.Add(new FuiChoice(GroupPrefix + key, $"{label} · {jobs.Length}"));
            foreach (var job in jobs)
            {
                var agent = string.IsNullOrWhiteSpace(job.Agent) ? "未提供代理" : job.Agent;
                choices.Add(new FuiChoice(
                    job.JobId,
                    $"{FuiAgentActivityRoster.StateGlyph(job.State)} {FuiAgentActivityRoster.StateLabel(job.State)} · {agent} · {job.JobId}",
                    FuiAgentActivityRoster.JobSummary(snapshot, job),
                    SearchText: $"{job.JobId}\n{agent}\n{job.LastDetail}"));
            }
        }
    }

    internal static string Detail(FuiAgentActivitySnapshot snapshot, FuiAgentJob job) =>
        FuiAgentActivityRoster.Detail(snapshot, job) + "\n" +
        "父作业 ID · 未提供\n" +
        "取消/恢复 · 不可用（FUMO SubagentUpdated 没有控制事实源）";
}
