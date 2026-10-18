using Fufucode.App.Abstractions.Automation;

namespace Fufucode.Plugins.AutoDev;

/// <summary>
/// 宿主 CLI 的命令入口。CLI 不静态引用本程序集；安装并启用插件后按清单加载本类型。
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
