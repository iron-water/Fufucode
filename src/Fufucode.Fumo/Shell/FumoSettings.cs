namespace Fufucode.Core;

/// <summary>
/// FUMO 内核的高级设置（设置 → 高级）。
/// Shell 为空时按 PowerShell 7 → Windows PowerShell → cmd 取第一个存在的；显式设置时只用该解释器。
/// </summary>
public sealed record FumoSettings(
    string? Shell = null,
    // 是否加载解释器的用户配置（PowerShell $PROFILE、cmd AutoRun、bash --login）；默认关闭，命令可复现且启动快。
    bool LoadShellProfile = false,
    // run_command 未指定 timeout_seconds 时的超时（秒）。
    int CommandTimeoutSeconds = FumoSettings.DefaultCommandTimeoutSeconds)
{
    public const int DefaultCommandTimeoutSeconds = 300;
    public const int MaxCommandTimeoutSeconds = 3600;
}
