namespace Fufucode.Fui;

internal sealed partial class TuiController
{
    private FuiAgentActivitySnapshot _agentActivity = FuiAgentActivitySnapshot.Empty;

    private void UpdateAgentActivity(FuiAgentActivitySnapshot snapshot)
    {
        _agentActivity = snapshot;
        if (_pickerAction == "agents") _picker?.ReplaceChoices(FuiAgentActivityRoster.Choices(snapshot));
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
        OpenPicker("agents", FuiAgentActivityRoster.HubTitle, FuiAgentActivityRoster.Choices(_agentActivity),
            options: new(
                Detail: choice => _agentActivity.Find(choice.Value) is { } job
                    ? FuiAgentActivityRoster.Detail(_agentActivity, job)
                    : null,
                GroupHeader: FuiAgentActivityRoster.IsGroupChoice,
                PreserveOrder: true));
    }

    private void ShowAgentDetail(FuiChoice choice)
    {
        if (_agentActivity.Find(choice.Value) is not { } job)
        {
            Error("该子代理活动已不存在。");
            return;
        }
        Info(FuiAgentActivityRoster.Detail(_agentActivity, job));
    }
}
