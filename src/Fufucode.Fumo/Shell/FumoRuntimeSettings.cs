namespace Fufucode.Core.Shell;

/// <summary>
/// 进程内 FUMO 高级设置的读取入口。组合根（Core 的 NativeSessionFactory）绑定活动的设置存储；
/// 每次读取都取存储的当前快照，设置页改动对下一次工具调用立即生效。
/// 子代理 patch 模式等自行创建工具调度器的路径也经由这里拿到同一份设置。
/// </summary>
public static class FumoRuntimeSettings
{
    private static Func<FumoSettings?> _provider = static () => null;

    public static FumoSettings Current
    {
        get
        {
            try { return _provider() ?? new FumoSettings(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            { return new FumoSettings(); }
        }
    }

    /// <summary>绑定设置来源（宿主传入读取当前快照的委托；内核不依赖具体的设置存储）。</summary>
    public static void Use(Func<FumoSettings?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    /// <summary>当前设置下 run_command 默认使用的解释器；不可用时返回 null 与原因。</summary>
    public static ResolvedCommandShell? ResolveDefaultShell(out string? error) =>
        CommandShells.Resolve(Current.Shell, out error);
}
