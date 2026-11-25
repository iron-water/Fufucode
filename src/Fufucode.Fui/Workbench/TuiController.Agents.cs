namespace Fufucode.Fui;

internal sealed partial class TuiController
{
    private FuiAgentActivitySnapshot _agentActivity = FuiAgentActivitySnapshot.Empty;

    private void UpdateAgentActivity(FuiAgentActivitySnapshot snapshot)
    {
        _agentActivity = snapshot;
        Dirty = true;
    }

    private void ResetAgentActivity() => _agentActivity = FuiAgentActivitySnapshot.Empty;

    private string AgentStatusSummary => FuiAgentActivityRoster.RunningSummary(_agentActivity);

    private void Agents()
    {
        if (_agentActivity.Jobs.Count == 0)
        {
            Info("尚无子代理活动");
            return;
        }
        OpenPicker("agents", "子代理活动 · 只读", FuiAgentActivityRoster.Choices(_agentActivity));
    }

    private void ShowAgentDetail(FuiChoice choice)
    {
        if (_agentActivity.Find(choice.Value) is not { } job)
        {
            Error("该子代理活动已不存在。");
            return;
        }
        Info(FuiAgentActivityRoster.Detail(job));
    }
}
