using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App;

public partial class App : Application
{
    private static AppSettingsStore? _settingsStore;
    private readonly AppOptions _options;

    /// <summary>应用级设置存储（M6）：按进程的 AppOptions 数据根创建；自测经 AppDataRootOverride 隔离。</summary>
    internal static AppSettingsStore SettingsStore =>
        _settingsStore ??= new AppSettingsStore(new AppDataLayout(_currentOptions?.AppDataRootOverride));

    private static AppOptions? _currentOptions;

    /// <summary>无参构造供设计器与无头自测使用（mock 会话）。</summary>
    public App() : this(AppOptions.Mock)
    {
    }

    public App(AppOptions options)
    {
        _options = options;
        _currentOptions = options;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 启动时应用持久化主题（selftest 自行控制主题，跳过）。
        if (!Program.SelfTestMode)
        {
            try
            {
                RequestedThemeVariant = SettingsPageViewModel.ThemeVariantFor(SettingsStore.Current.Theme);
            }
            catch (Exception)
            {
                // 设置存储不可用（目录权限等）：保持跟随系统，不阻止启动。
            }
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(_options);
            if (Program.NativeModuleSelfTest) desktop.MainWindow.ShowActivated = false;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
