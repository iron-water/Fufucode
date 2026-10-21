using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Fufucode.Fufu.Integration;

namespace Fufucode.Fufu;

internal static class Program
{
    internal static SingleInstanceGuard? SingleInstance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        var options = FufuOptions.Parse(args);

        // 单实例：同一登录会话只允许一个 FUFU。第二个实例激活已有实例的桌宠后退出。
        // --no-single-instance 只供自测/诊断并行运行使用。
        if (!options.NoSingleInstance)
        {
            switch (SingleInstanceGuard.TryAcquire(out var guard))
            {
                case SingleInstanceAcquireResult.AlreadyRunning:
                    SingleInstanceGuard.SignalExisting();
                    return 0;
                case SingleInstanceAcquireResult.Acquired:
                    SingleInstance = guard;
                    break;
            }
        }

        try
        {
            BuildAvaloniaApp(options).StartWithClassicDesktopLifetime(args);
            return 0;
        }
        finally
        {
            SingleInstance?.Dispose();
            SingleInstance = null;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(FufuOptions.Mock);

    public static AppBuilder BuildAvaloniaApp(FufuOptions options)
        => AppBuilder.Configure(() => new App(options))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
