using Fufucode.App.Abstractions;

namespace Fufucode.App.Infrastructure;

/// <summary>状态色调：VM 只暴露语义种类，具体颜色由 XAML 按主题令牌映射（暗色模式自动正确）。</summary>
public enum StatusTone
{
    Neutral,
    Accent,
    Success,
    Caution,
    Critical,
}

/// <summary>把工具状态/任务终态/连接状态映射为语义色调。映射集中在这一处，VM 只暴露 tone。</summary>
public static class StatusColors
{
    public static StatusTone ForToolCall(ToolCallStatus status)
    {
        return status switch
        {
            ToolCallStatus.Completed => StatusTone.Success,
            ToolCallStatus.Failed => StatusTone.Critical,
            ToolCallStatus.Cancelled => StatusTone.Neutral,
            _ => StatusTone.Accent,
        };
    }

    public static StatusTone ForOutcome(SessionOutcome? outcome)
    {
        return outcome switch
        {
            SessionOutcome.Completed => StatusTone.Success,
            SessionOutcome.Failed => StatusTone.Critical,
            _ => StatusTone.Neutral,
        };
    }

    public static StatusTone ForConnection(SessionConnection connection)
    {
        return connection switch
        {
            SessionConnection.Connected => StatusTone.Success,
            SessionConnection.Starting or SessionConnection.Stopping => StatusTone.Caution,
            SessionConnection.Faulted => StatusTone.Critical,
            _ => StatusTone.Neutral,
        };
    }
}
