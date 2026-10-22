using System.Text;

namespace Fufucode.Fufu.Integration;

internal enum SingleInstanceAcquireResult
{
    Acquired,
    AlreadyRunning,
    Unavailable,
}

/// <summary>
/// FUFU 单实例（任务书 §六.8）：同一登录会话只允许一个 FUFU 进程。
/// 与 Fufucode 的 Local\ Mutex + 激活事件同型，但命名对象独立——
/// FUFU 与 Fufucode 互不感知，各自独立单实例。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string BaseName = @"Local\FufuPet.SingleInstance.v1";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly EventWaitHandle _shutdown;
    private readonly Thread _listener;
    private volatile bool _disposed;
    private Action? _activationHandler;
    private readonly object _gate = new();
    private bool _activationPending;

    /// <summary>第二个实例请求激活（恢复桌宠窗口）；在后台线程触发，订阅方自行切 UI 线程。</summary>
    public event Action? ActivationRequested
    {
        add
        {
            bool pending;
            lock (_gate)
            {
                _activationHandler += value;
                pending = _activationPending;
                _activationPending = false;
            }
            if (pending) value?.Invoke();
        }
        remove { lock (_gate) _activationHandler -= value; }
    }

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activation, EventWaitHandle shutdown)
    {
        _mutex = mutex;
        _activation = activation;
        _shutdown = shutdown;
        _listener = new Thread(Listen) { IsBackground = true, Name = "fufu single-instance" };
        _listener.Start();
    }

    public static SingleInstanceAcquireResult TryAcquire(out SingleInstanceGuard? guard)
    {
        guard = null;
        var name = BaseName + "." + UserScope();
        Mutex mutex;
        bool created;
        try { mutex = new Mutex(false, name, out created); }
        catch (Exception) { return SingleInstanceAcquireResult.Unavailable; }
        if (!created)
        {
            mutex.Dispose();
            return SingleInstanceAcquireResult.AlreadyRunning;
        }
        try
        {
            var activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".activate");
            var shutdown = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".shutdown");
            guard = new SingleInstanceGuard(mutex, activation, shutdown);
            return SingleInstanceAcquireResult.Acquired;
        }
        catch (Exception)
        {
            try { mutex.Dispose(); } catch { }
            return SingleInstanceAcquireResult.Unavailable;
        }
    }

    /// <summary>通知已运行实例恢复窗口；未送达不阻止第二实例退出。</summary>
    public static void SignalExisting()
    {
        var name = BaseName + "." + UserScope() + ".activate";
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(name);
                handle.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(50); }
            catch (Exception) { return; }
        }
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
            Action? handler;
            lock (_gate)
            {
                handler = _activationHandler;
                if (handler is null) { _activationPending = true; continue; }
            }
            try { handler(); } catch (Exception) { /* 激活失败不退出监听 */ }
        }
    }

    private static string UserScope()
    {
        var raw = Environment.UserDomainName + "_" + Environment.UserName;
        var builder = new StringBuilder(raw.Length);
        foreach (var c in raw)
            if (char.IsAsciiLetterOrDigit(c)) builder.Append(c);
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
