using Fufucode.Core;

namespace Fufucode.App.Integration;

/// <summary>会话来源：默认接真实 Pi（M2 集成）；--mock 回到脚本化演示会话（--selftest 始终走 mock）。</summary>
public enum AppSessionMode
{
    Real,
    Mock,
}

/// <summary>命令行解析结果。仅组合根消费；视图与视图模型不感知启动参数。</summary>
public sealed record AppOptions(
    AppSessionMode Mode,
    string? ProjectPath,
    string? SessionId,
    string? PiNodePath,
    string? PiCliPath,
    // 自测隔离：AppSettingsStore/诊断的根目录覆盖；产品路径为 null（默认 LocalApplicationData/Fufucode）。
    string? AppDataRootOverride = null,
    BackendId? Backend = null, string? OmpExecutablePath = null,
    string? ClaudeExecutablePath = null,
    string? CodexExecutablePath = null,
    string? DshExecutablePath = null,
    // 并行审阅：允许多个实例同时运行，并用标签区分窗口；不影响正常产品启动。
    bool ParallelReview = false,
    string? ReviewLabel = null)
{
    public static readonly AppOptions Mock = new(AppSessionMode.Mock, null, null, null, null);

    /// <summary>本进程的设置/布局根目录（供 AppSettingsStore 与诊断摘要使用）。</summary>
    public Fufucode.Core.AppDataLayout CreateAppDataLayout() => new(AppDataRootOverride);

    /// <summary>
    /// 支持开关：--mock（演示会话）、--project &lt;路径&gt;（Pi 工作目录；缺省回退最近项目，
    /// 仍无则进入“选择项目”态，不再以当前目录作缺省）、
    /// --session &lt;id&gt;（恢复指定会话，缺省恢复该项目最近会话）、--pi-node/--pi-cli（运行环境覆盖）、
    /// --omp-exe（OMP 可执行文件）、--claude-exe（Claude Code CLI 可执行文件）、
    /// --codex-exe（Codex CLI 可执行文件）、--dsh-exe（DeepSeek Harness CLI 可执行文件）。
    /// </summary>
    public static AppOptions Parse(string[] args)
    {
        var mode = AppSessionMode.Real;
        string? project = null, session = null, node = null, cli = null, dataRoot = null;
        BackendId? backend = null;
        var parallelReview = false;
        string? reviewLabel = null;
        string? omp = null;
        string? claude = null;
        string? codex = null;
        string? dsh = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("fufucode:", System.StringComparison.OrdinalIgnoreCase))
            {
                if (!SessionDeepLink.TryParse(args[i], out session, out project))
                    throw new System.ArgumentException("无效的 Fufucode 会话链接");
                continue;
            }
            switch (args[i])
            {
                case "--backend" when i + 1 < args.Length:
                    backend = BackendCapabilities.Parse(args[++i]);
                    break;
                case "--omp-exe" when i + 1 < args.Length:
                    omp = System.IO.Path.GetFullPath(args[++i]);
                    break;
                case "--claude-exe" when i + 1 < args.Length:
                    claude = System.IO.Path.GetFullPath(args[++i]);
                    break;
                case "--codex-exe" when i + 1 < args.Length:
                    codex = System.IO.Path.GetFullPath(args[++i]);
                    break;
                case "--dsh-exe" when i + 1 < args.Length:
                    dsh = System.IO.Path.GetFullPath(args[++i]);
                    break;
                case "--mock":
                    mode = AppSessionMode.Mock;
                    break;
                case "--project" when i + 1 < args.Length:
                    project = args[++i];
                    break;
                case "--session" when i + 1 < args.Length:
                    session = args[++i];
                    break;
                case "--pi-node" when i + 1 < args.Length:
                    node = args[++i];
                    break;
                case "--pi-cli" when i + 1 < args.Length:
                    cli = args[++i];
                    break;
                case "--data-root" when i + 1 < args.Length:
                    dataRoot = System.IO.Path.GetFullPath(args[++i]);
                    break;
                case "--parallel-review":
                    parallelReview = true;
                    break;
                case "--review-label" when i + 1 < args.Length:
                    reviewLabel = args[++i];
                    parallelReview = true;
                    break;
            }
        }

        return new AppOptions(mode, project, session, node, cli, dataRoot, backend, omp, claude, codex, dsh,
            parallelReview, reviewLabel);
    }
}
