using System;
using System.Text;
using System.Threading;

namespace Fufucode.App.Integration;

internal enum SingleInstanceAcquireResult
{
    /// <summary>本进程成为唯一实例，guard 非空。</summary>
    Acquired,

    /// <summary>已有实例在运行；调用方应 SignalExisting 后退出。</summary>
    AlreadyRunning,

    /// <summary>命名内核对象不可用（权限/平台限制）；放行启动，不因单实例机制阻止应用。</summary>
    Unavailable,
}

/// <summary>
/// 同一登录会话内只允许一个 Fufucode 进程。
///
/// 实现：命名的 Mutex 表示“已有实例”，命名的自动重置事件表示“把窗口带到前台”。
/// 第二个实例发现 Mutex 已存在 → 设置激活事件 → 自己退出；第一个实例的后台线程收到事件
/// 后由 App 投递到 UI 线程恢复窗口（即使窗口藏在托盘也能恢复）。
///
/// 命名空间使用 <c>Local\</c>：普通用户无需全局对象权限，范围是当前登录会话。
/// 自测/无头模式不启用，避免并行测试互相阻塞。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string DefaultBaseName = @"Local\Fufucode.SingleInstance.v1";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly EventWaitHandle _shutdown;
    private readonly Thread _listener;
    private volatile bool _disposed;
    private readonly object _activationGate = new();
    private Action? _activationHandler;
    private bool _activationPending;

    /// <summary>
    /// 第二个实例请求激活窗口。在后台线程触发；订阅方须自行切到 UI 线程。
    /// 首个实例尚未完成 UI 初始化时到达的请求会缓存，首个订阅者接手时补发一次。
    /// </summary>
    public event Action? ActivationRequested
    {
        add
        {
            bool pending;
            lock (_activationGate)
            {
                _activationHandler += value;
                pending = _activationPending;
                _activationPending = false;
            }
            if (pending) value?.Invoke();
        }
        remove
        {
            lock (_activationGate) _activationHandler -= value;
        }
    }

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activation, EventWaitHandle shutdown)
    {
        _mutex = mutex;
        _activation = activation;
        _shutdown = shutdown;
        _listener = new Thread(Listen)
        {
            IsBackground = true,
            Name = "Fufucode single-instance activation listener",
        };
        _listener.Start();
    }

    /// <summary>
    /// 尝试成为唯一实例。<paramref name="nameOverride"/> 只供自测隔离使用，产品路径传 null。
    /// </summary>
    public static SingleInstanceAcquireResult TryAcquire(string? nameOverride, out SingleInstanceGuard? guard)
    {
        guard = null;
        var baseName = nameOverride ?? DefaultBaseName;
        if (nameOverride is null) baseName += "." + UserScope();
        var activationName = baseName + ".activate";
        var shutdownName = baseName + ".shutdown";

        Mutex mutex;
        bool created;
        try
        {
            // 不请求所有权：只要命名对象存在，就表示已有实例持有句柄。
            mutex = new Mutex(initiallyOwned: false, baseName, out created);
        }
        catch (Exception)
        {
            return SingleInstanceAcquireResult.Unavailable;
        }

        if (!created)
        {
            mutex.Dispose();
            return SingleInstanceAcquireResult.AlreadyRunning;
        }

        try
        {
            var activation = new EventWaitHandle(false, EventResetMode.AutoReset, activationName);
            var shutdown = new EventWaitHandle(false, EventResetMode.ManualReset, shutdownName);
            guard = new SingleInstanceGuard(mutex, activation, shutdown);
            return SingleInstanceAcquireResult.Acquired;
        }
        catch (Exception)
        {
            // 事件创建失败：释放已取得的 Mutex，放行启动，避免卡在不可用的单实例机制上。
            try { mutex.Dispose(); } catch { }
            return SingleInstanceAcquireResult.Unavailable;
        }
    }

    /// <summary>
    /// 通知已运行实例恢复窗口。首个实例可能还在启动，事件尚未创建：短暂重试。
    /// 返回是否成功送达（未送达不阻止第二个实例退出）。
    /// </summary>
    public static bool SignalExisting(string? nameOverride)
    {
        // OpenExisting 只在 Windows 提供；其他平台没有单实例入口，直接报告未送达。
        if (!OperatingSystem.IsWindows()) return false;
        var baseName = nameOverride ?? DefaultBaseName;
        if (nameOverride is null) baseName += "." + UserScope();
        var activationName = baseName + ".activate";

        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(activationName);
                handle.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
            catch (Exception)
            {
                return false;
            }
        }
        return false;
    }

    private void Listen()
    {
        var handles = new WaitHandle[] { _activation, _shutdown };
        while (!_disposed)
        {
            int index;
            try { index = WaitHandle.WaitAny(handles); }
            catch (Exception) { return; }
            if (index != 0 || _disposed) return;
            RaiseActivation();
        }
    }

    private void RaiseActivation()
    {
        Action? handler;
        lock (_activationGate)
        {
            handler = _activationHandler;
            if (handler is null)
            {
                _activationPending = true;
                return;
            }
        }
        try { handler(); }
        catch (Exception)
        {
            // 激活失败不能让监听线程退出：下一次第二实例启动仍需可用。
        }
    }

    /// <summary>
    /// 用户名进内核对象名：只保留 ASCII 字母数字，避免反斜杠等分隔字符改变命名空间。
    /// </summary>
    private static string UserScope()
    {
        var raw = Environment.UserDomainName + "_" + Environment.UserName;
        var builder = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsAsciiLetterOrDigit(c)) builder.Append(c);
        }
        return builder.Length == 0 ? "default" : builder.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _shutdown.Set(); } catch { }
        try { _listener.Join(TimeSpan.FromSeconds(1)); } catch { }
        try { _activation.Dispose(); } catch { }
        try { _shutdown.Dispose(); } catch { }
        try { _mutex.Dispose(); } catch { }
    }
}
