namespace Fufucode.Core;

/// <summary>
/// 内置 RPC 后端（Pi / OMP）的启动画像。
///
/// 这两个后端共用同一条 RPC 线路，但可执行文件、命令行参数、会话身份与配置目录各不相同。
/// 把这些差异集中成一份显式画像，替代调用点里的 `Backend == Pi ? A : B` 二选一：
/// 后者会让任何新增后端静默继承 OMP 的行为。
///
/// 协议插件后端（Claude / Codex / DSH）不走这条路径，FUMO 也不走；
/// 它们各自在自己的插件里给出 <see cref="BackendProcessSpec"/>。
/// </summary>
public sealed record RpcLaunchProfile(
    BackendId Backend,
    string Executable,
    IReadOnlyList<string> BaseArguments,
    string AgentDirectory,
    string ResumeFlag,
    string? SessionIdFlag,
    bool AcceptsNameFlag,
    bool AcceptsUserExtensions,
    string? ModelFlag,
    bool UsesManagedSessionId)
{
    /// <summary>二者的配置目录都通过环境变量指向一个受管目录，避免改写用户全局配置。</summary>
    public const string AgentDirectoryVariable = "PI_CODING_AGENT_DIR";

    /// <summary>Pi 的端点写入 pi models.json；OMP 写入自己隔离的 models.yml。</summary>
    public bool InstallsPiStyleEndpoint => Backend == BackendId.Pi;

    /// <summary>只有这两个后端由 Core 直接拉起 RPC 进程。</summary>
    public static bool IsBuiltInRpcBackend(BackendId backend) => backend is BackendId.Pi or BackendId.Omp;

    /// <summary>
    /// 构造启动画像。未知后端直接抛出，而不是落到某个默认分支上——缺省必须显式。
    /// </summary>
    public static RpcLaunchProfile For(BackendId backend, string sessionDirectory, string trustFlag,
        string nodePath, string cliPath, string? ompExecutablePath, string piAgentDirectory,
        string ompAgentDirectory)
    {
        switch (backend)
        {
            case BackendId.Pi:
                return new RpcLaunchProfile(
                    backend,
                    nodePath,
                    [cliPath, "--mode", "rpc", trustFlag, "--no-extensions", "--session-dir", sessionDirectory],
                    piAgentDirectory,
                    ResumeFlag: "--session",
                    SessionIdFlag: "--session-id",
                    AcceptsNameFlag: true,
                    AcceptsUserExtensions: true,
                    ModelFlag: null,
                    UsesManagedSessionId: true);

            case BackendId.Omp:
                return new RpcLaunchProfile(
                    backend,
                    ompExecutablePath ?? throw new InvalidOperationException(
                        "OMP runtime executable is unresolved; configure --omp-exe."),
                    ["--mode", "rpc", "--no-title", "--no-extensions", "--no-skills", "--no-rules", "--no-lsp",
                        "--approval-mode", "yolo", "--session-dir", sessionDirectory],
                    ompAgentDirectory,
                    ResumeFlag: "--resume",
                    SessionIdFlag: null,
                    AcceptsNameFlag: false,
                    AcceptsUserExtensions: false,
                    ModelFlag: "--model",
                    UsesManagedSessionId: false);

            default:
                throw new ArgumentOutOfRangeException(nameof(backend), backend,
                    "Built-in RPC launch only covers Pi and OMP; protocol backends build their own spec.");
        }
    }
}
