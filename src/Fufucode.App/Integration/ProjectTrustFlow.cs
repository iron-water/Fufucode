using System;
using System.Threading.Tasks;
using Fufucode.Core;

namespace Fufucode.App.Integration;

/// <summary>用户放弃了项目信任决定：不落盘任何决定，也不重试打开。</summary>
public sealed class ProjectTrustDeclinedException : InvalidOperationException
{
    public ProjectTrustDeclinedException(ProjectTrustStatus status)
        : base($"未对项目 {status.ProjectPath} 作出信任决定。") => Status = status;

    public ProjectTrustStatus Status { get; }
}

/// <summary>
/// 项目信任引导（无 UI 依赖，可测）：打开操作遇到 <see cref="ProjectTrustDecisionRequiredException"/>
/// 时调用 ask 询问一次；放弃（null）抛 <see cref="ProjectTrustDeclinedException"/>；
/// 有决定则先 save 再重试打开恰好一次——第二次信任异常直接向上传播。
/// </summary>
public static class ProjectTrustFlow
{
    public static async Task<T> RunAsync<T>(
        Func<Task<T>> open,
        Func<ProjectTrustStatus, Task<ProjectTrustDecision?>> ask,
        Action<ProjectTrustStatus, ProjectTrustDecision> save)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(save);

        try
        {
            return await open();
        }
        catch (ProjectTrustDecisionRequiredException required)
        {
            var decision = await ask(required.Status);
            if (decision is null)
            {
                throw new ProjectTrustDeclinedException(required.Status);
            }

            save(required.Status, decision.Value);
            return await open();
        }
    }
}
