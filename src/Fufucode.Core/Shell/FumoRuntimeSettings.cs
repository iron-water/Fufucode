namespace Fufucode.Core.Shell;

/// <summary>
/// 进程内 FUMO 高级设置的读取入口。组合根（<see cref="NativeSessionFactory"/>）绑定活动的设置存储；
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

    public static void Use(AppSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _provider = () => store.Current.Fumo;
    }

    /// <summary>当前设置下 run_command 默认使用的解释器；不可用时返回 null 与原因。</summary>
    public static ResolvedCommandShell? ResolveDefaultShell(out string? error) =>
        CommandShells.Resolve(Current.Shell, out error);
}
