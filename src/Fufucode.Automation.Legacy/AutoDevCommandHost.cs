using Fufucode.App.Abstractions.Automation;

namespace Fufucode.Plugins.AutoDev;

/// <summary>
/// Explicit-database control/host entry point, not part of the Graph execution authority.
/// </summary>
public sealed class AutoDevCommandHost : IAutomationCommandHost
{
    public AutoDevCommandHost()
    {
        // CLI 进程没有插件包初始化流程：执行/控制命令需要协议适配器先登记。
        Engine.Harnesses.BatchHarnessAdapters.Register();
    }

    public Task<int> RunControlAsync(string[] arguments, TextWriter output,
        CancellationToken cancellationToken = default) =>
        Cli.CliControlCommands.RunAsync(arguments, output, cancellationToken);

    public Task<int> RunHostAsync(string[] arguments, TextWriter output,
        CancellationToken cancellationToken = default) =>
        Cli.CliHostCommands.RunAsync(arguments, output, cancellationToken);
}
