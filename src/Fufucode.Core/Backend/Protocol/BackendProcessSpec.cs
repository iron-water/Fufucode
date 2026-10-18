namespace Fufucode.Core;

/// <summary>
/// 启动一个外部后端进程所需的完整参数（可执行文件、参数、工作目录、环境变量、后端标识）。
/// Core 只组装规格；真正启动进程的代码属于后端插件。
/// </summary>
public sealed record BackendProcessSpec(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    BackendId Backend);
