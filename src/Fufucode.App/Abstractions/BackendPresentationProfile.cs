namespace Fufucode.App.Abstractions;

/// <summary>
/// 后端的"呈现时序配置"。
///
/// 视觉语言（颜色、圆角、字号、动画曲线与时长）对整个应用是唯一的；
/// 本配置只决定"什么时候出现什么提示"，用于吸收不同后端的固有延迟差异：
/// 纯托管内核几乎瞬时，RPC 要等回执，外部 CLI 可能要启动数秒。
///
/// 实现由后端自己提供，UI 只读取本配置，不允许在视图层判断后端身份。
/// </summary>
/// <param name="Name">面向用户的名称，用于"正在启动 …"这类必要提示。</param>
/// <param name="AwaitFirstEventGraceMs">发送后多久仍无事件才显示"等待响应"，避免快后端闪烁。</param>
/// <param name="AckHintDelayMs">发送后多久仍未确认才显示"发送中"。</param>
/// <param name="MinIndicatorVisibleMs">运行指示器最短可见时长，避免快速完成时一闪而过。</param>
/// <param name="StatusDebounceMs">动态状态文案的防抖窗口。</param>
/// <param name="HasSlowStartup">后端是否需要"正在启动"阶段（外部进程/CLI）。</param>
/// <param name="EmitsThinkingDeltas">后端是否会产生思考增量。</param>
public sealed record BackendPresentationProfile(
    string Name,
    int AwaitFirstEventGraceMs,
    int AckHintDelayMs,
    int MinIndicatorVisibleMs,
    int StatusDebounceMs,
    bool HasSlowStartup,
    bool EmitsThinkingDeltas)
{
    /// <summary>未知后端的安全默认值：不提前显示任何提示。</summary>
    public static readonly BackendPresentationProfile Default =
        new("session", 250, 500, 400, 150, false, true);

    /// <summary>FUMO：进程内执行，首事件几乎瞬时。</summary>
    public static readonly BackendPresentationProfile Fumo =
        new("FUMO", 120, 400, 250, 100, false, true);

    /// <summary>Pi：RPC 子进程，需要等 prompt 回执。</summary>
    public static readonly BackendPresentationProfile Pi =
        new("Pi", 250, 500, 400, 150, false, true);

    /// <summary>OMP：独立 CLI 进程，启动比 Pi 慢。</summary>
    public static readonly BackendPresentationProfile Omp =
        new("OMP", 350, 600, 450, 150, true, true);

    /// <summary>Claude：官方 CLI 冷启动可能数秒。</summary>
    public static readonly BackendPresentationProfile Claude =
        new("Claude", 400, 800, 500, 150, true, true);

    /// <summary>Codex：外部 harness 连接。</summary>
    public static readonly BackendPresentationProfile Codex =
        new("Codex", 600, 1000, 600, 200, true, false);

    /// <summary>DSH：外部 harness 连接。</summary>
    public static readonly BackendPresentationProfile Dsh =
        new("DSH", 600, 1000, 600, 200, true, false);

    /// <summary>自测/演示会话：事件由脚本即时产生，不需要额外等待提示。</summary>
    public static readonly BackendPresentationProfile Scripted =
        new("session", 0, 0, 200, 0, false, true);
}

/// <summary>
/// 由后端实现，向 UI 声明自己的呈现时序。
/// UI 通过接口读取，不判断具体后端类型。
/// </summary>
public interface IPresentationProfiled
{
    BackendPresentationProfile Presentation { get; }
}
