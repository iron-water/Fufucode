using Fufucode.App.Abstractions.Automation;
using Fufucode.Plugins.AutoDev;

namespace Fufucode.Cli;

/// <summary>
/// 内建自动开发命令入口。保留 control/host 的显式数据库与宿主选项协议，
/// 不从历史插件载荷加载代码，也不自动切换到 Graph 项目数据库。
/// </summary>
public static class AutomationCommands
{
    private static readonly Lazy<IAutomationCommandHost> Host = new(() => new AutoDevCommandHost());

    public static async Task<int> RunControlAsync(CliOptions options, TextWriter output, CancellationToken ct)
    {
        if (options.SessionId is not null)
            throw new ArgumentException("control 命令不接受会话参数。");
        return await Host.Value.RunControlAsync([.. options.Arguments], output, ct);
    }

    public static async Task<int> RunHostAsync(CliOptions options, TextWriter output, CancellationToken ct)
    {
        if (options.SessionId is not null)
            throw new ArgumentException("host 命令不接受会话参数。");
        return await Host.Value.RunHostAsync([.. options.Arguments], output, ct);
    }
}
