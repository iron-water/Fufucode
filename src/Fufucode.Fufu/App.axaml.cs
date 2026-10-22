using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Fufucode.App.Abstractions;
using Fufucode.Fufu.Bot;
using Fufucode.Fufu.Integration;
using Fufucode.Fufu.Pet;
using Fufucode.Fufu.Settings;

namespace Fufucode.Fufu;

public sealed class App : Application
{
    private readonly FufuOptions _options;
    private FufuSettingsStore? _settings;
    private PetBehavior? _behavior;
    private PetWindow? _petWindow;
    private FufuTrayService? _tray;
    private WanderController? _wander;
    private BotHost? _bot;
    private FufuSessionHost? _sessionHost;
    private PetChatWindow? _chatWindow;
    private EndpointSettingsWindow? _endpointSettingsWindow;
    private FufuPipeServer? _pipeServer;
    private BridgeWindow? _bridgeWindow;
    private ProactiveEngine? _proactive;
    private TaskLog? _taskLog;
    private NoticeBubble? _bubble;
    private bool _exiting;
    private bool _petShown = true; // 显式跟踪显隐：Window.IsVisible 对顶层窗口不可靠
    private PixelPoint? _lastPos; // 显式跟踪位置：Window.Position 在 Hide/Show 往返后可能复位为 0,0

    public App() : this(FufuOptions.Mock) { }
    public App(FufuOptions options) => _options = options;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var dataRoot = _options.DataRoot;
        FufuLog.Init(dataRoot);
        FufuLog.Write($"FUFU init: dataRoot={dataRoot}");
        _settings = new FufuSettingsStore(dataRoot);

        _behavior = new PetBehavior
        {
            FreeRoam = _settings.Current.FreeRoam,
            MovementPaused = _settings.Current.MovementPaused,
        };
        _wander = new WanderController(_behavior);
        _bot = new BotHost(_behavior);
        _sessionHost = new FufuSessionHost(dataRoot, _behavior);
        _pipeServer = new FufuPipeServer();
        _taskLog = new TaskLog(dataRoot);
        _proactive = new ProactiveEngine(_pipeServer, _settings, _taskLog);
        _bubble = new NoticeBubble();
        _proactive.Noticed += text => _bubble.ShowNotice(text, _lastPos ?? _petWindow?.Position ?? new PixelPoint(0, 0));
        _proactive.StateSuggested += state => _behavior.Notify(state);
        _pipeServer.AgentEventReceived += OnBridgeEvent;
        _pipeServer.ApprovalReceived += (_, _, _, _) =>
            Dispatcher.UIThread.Post(() => _behavior.Notify(PetState.Attention));
        _pipeServer.ConnectionChanged += connected =>
            Dispatcher.UIThread.Post(() => _behavior.Notify(connected ? PetState.Success : PetState.Idle));

        var renderer = new PlaceholderPetRenderer();
        var settings = _settings.Current;
        _petWindow = new PetWindow(renderer, _behavior, _wander, settings.Scale, settings.ClickThrough)
        {
            Icon = LoadWindowIcon(),
        };

        // 恢复上次位置；越出屏幕时钳回工作区。
        if (settings.PositionX is { } px && settings.PositionY is { } py)
        {
            _petWindow.Position = new PixelPoint((int)px, (int)py);
            _lastPos = _petWindow.Position;
        }
        desktop.MainWindow = _petWindow;
        _petWindow.Closed += (_, _) => SavePosition();
        _petWindow.PositionChanged += (_, _) =>
        {
            // 隐藏/还原期间平台可能回发 (0,0)，不采信；其余都是真实位置。
            if (_petWindow.Position != new PixelPoint(0, 0))
                _lastPos = _petWindow.Position;
            _petWindow.ClampToScreen();
        };

        try
        {
            _tray = new FufuTrayService(ShowPet, FillTrayMenu);
        }
        catch (Exception ex)
        {
            FufuLog.Write($"TrayIcon failed: {ex.GetType().Name}: {ex.Message}");
        }

        // 右键桌宠 = 与托盘一致的操作菜单（点角色实体才命中）。
        renderer.View.ContextMenu = BuildContextMenu();

        // 单实例激活：恢复并显示桌宠。
        if (Program.SingleInstance is { } guard)
        {
            guard.ActivationRequested += () => Dispatcher.UIThread.Post(ShowPet);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private WindowIcon? LoadWindowIcon()
    {
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://fufu/Assets/fufu.png"));
            return new WindowIcon(stream);
        }
        catch (Exception) { return null; }
    }

    private void ShowPet()
    {
        if (_petWindow is null) return;
        _petShown = true;
        _petWindow.Show();
        _petWindow.Activate();
        _tray?.RebuildMenu();
    }

    private void HidePet()
    {
        SavePosition();
        _petShown = false;
        _petWindow?.Hide();
        _tray?.RebuildMenu();
    }

    private void SavePosition()
    {
        if (_petWindow is null || _settings is null) return;
        var pos = _lastPos ?? _petWindow.Position;
        _settings.Update(s => s with { PositionX = pos.X, PositionY = pos.Y });
    }

    private void FillTrayMenu(NativeMenu menu)
    {
        if (_settings is null || _behavior is null || _petWindow is null) return;
        var s = _settings.Current;

        var showItem = new NativeMenuItem(_petShown ? "隐藏桌宠" : "显示桌宠");
        showItem.Click += (_, _) =>
        {
            if (_petShown) HidePet(); else ShowPet();
            _tray?.RebuildMenu();
        };
        menu.Items.Add(showItem);

        var chatItem = new NativeMenuItem("和 FUFU 聊天…");
        chatItem.Click += (_, _) => ShowChat();
        menu.Items.Add(chatItem);

        var endpointItem = new NativeMenuItem("配置模型端点…");
        endpointItem.Click += (_, _) => ShowEndpointSettings();
        menu.Items.Add(endpointItem);

        var bridgeItem = new NativeMenuItem("连接 Fufucode…");
        bridgeItem.Click += (_, _) => ShowBridge();
        menu.Items.Add(bridgeItem);

        var proactiveItem = new NativeMenuItem("主动行为") { IsChecked = s.ProactiveEnabled };
        proactiveItem.Click += (_, _) =>
        {
            var on = !_settings.Current.ProactiveEnabled;
            _settings.Update(v => v with { ProactiveEnabled = on });
            _taskLog?.Append("user", "proactive", "", on ? "启用主动行为" : "停止主动行为");
            _tray?.RebuildMenu();
        };
        menu.Items.Add(proactiveItem);

        var pauseItem = new NativeMenuItem("暂停移动") { IsChecked = s.MovementPaused };
        pauseItem.Click += (_, _) =>
        {
            var paused = !_settings.Current.MovementPaused;
            _settings.Update(v => v with { MovementPaused = paused });
            _behavior.MovementPaused = paused;
            _tray?.RebuildMenu();
        };
        menu.Items.Add(pauseItem);

        var roamItem = new NativeMenuItem("自由移动") { IsChecked = s.FreeRoam };
        roamItem.Click += (_, _) =>
        {
            var on = !_settings.Current.FreeRoam;
            _settings.Update(v => v with { FreeRoam = on });
            _behavior.FreeRoam = on;
            _tray?.RebuildMenu();
        };
        menu.Items.Add(roamItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var autoItem = new NativeMenuItem("开机自启") { IsChecked = s.AutoStart };
        autoItem.Click += (_, _) =>
        {
            var on = !_settings.Current.AutoStart;
            var exe = Environment.ProcessPath;
            if (exe is not null && StartupRegistration.Set(on, exe))
                _settings.Update(v => v with { AutoStart = on });
            _tray?.RebuildMenu();
        };
        menu.Items.Add(autoItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var quitItem = new NativeMenuItem("退出 FUFU");
        quitItem.Click += (_, _) => Quit();
        menu.Items.Add(quitItem);
    }

    /// <summary>右键桌宠弹出的上下文菜单：与托盘菜单同一组动作。</summary>
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (_settings is null || _behavior is null || _petWindow is null) return;
            var s = _settings.Current;

            var hideItem = new MenuItem { Header = _petShown ? "隐藏到托盘" : "显示桌宠" };
            hideItem.Click += (_, _) => { if (_petShown) HidePet(); else ShowPet(); _tray?.RebuildMenu(); };
            menu.Items.Add(hideItem);

            var chatItem = new MenuItem { Header = "和 FUFU 聊天…" };
            chatItem.Click += (_, _) => ShowChat();
            menu.Items.Add(chatItem);

            var endpointItem = new MenuItem { Header = "配置模型端点…" };
            endpointItem.Click += (_, _) => ShowEndpointSettings();
            menu.Items.Add(endpointItem);

            var bridgeItem = new MenuItem { Header = "连接 Fufucode…" };
            bridgeItem.Click += (_, _) => ShowBridge();
            menu.Items.Add(bridgeItem);

            var proactiveItem = new MenuItem { Header = "主动行为", IsChecked = s.ProactiveEnabled };
            proactiveItem.Click += (_, _) =>
            {
                var on = !_settings.Current.ProactiveEnabled;
                _settings.Update(v => v with { ProactiveEnabled = on });
                _taskLog?.Append("user", "proactive", "", on ? "启用主动行为" : "停止主动行为");
                _tray?.RebuildMenu();
            };
            menu.Items.Add(proactiveItem);

            var pauseItem = new MenuItem { Header = "暂停移动", IsChecked = s.MovementPaused };
            pauseItem.Click += (_, _) =>
            {
                var paused = !_settings.Current.MovementPaused;
                _settings.Update(v => v with { MovementPaused = paused });
                _behavior.MovementPaused = paused;
                _tray?.RebuildMenu();
            };
            menu.Items.Add(pauseItem);

            var roamItem = new MenuItem { Header = "自由移动", IsChecked = s.FreeRoam };
            roamItem.Click += (_, _) =>
            {
                var on = !_settings.Current.FreeRoam;
                _settings.Update(v => v with { FreeRoam = on });
                _behavior.FreeRoam = on;
                _tray?.RebuildMenu();
            };
            menu.Items.Add(roamItem);

            menu.Items.Add(new Separator());

            var autoItem = new MenuItem { Header = "开机自启", IsChecked = s.AutoStart };
            autoItem.Click += (_, _) =>
            {
                var on = !_settings.Current.AutoStart;
                var exe = Environment.ProcessPath;
                if (exe is not null && StartupRegistration.Set(on, exe))
                    _settings.Update(v => v with { AutoStart = on });
                _tray?.RebuildMenu();
            };
            menu.Items.Add(autoItem);

            menu.Items.Add(new Separator());

            var quitItem = new MenuItem { Header = "退出 FUFU" };
            quitItem.Click += (_, _) => Quit();
            menu.Items.Add(quitItem);
        };
        return menu;
    }

    /// <summary>打开/聚焦端点设置窗口：密钥值仍由用户在环境变量中维护。</summary>
    private void ShowEndpointSettings()
    {
        if (_settings is null) return;
        if (_endpointSettingsWindow is { } existing) { existing.Activate(); return; }

        if (_sessionHost is null) return;
        var window = new EndpointSettingsWindow(_sessionHost.Settings, () =>
        {
            _chatWindow?.Close();
            _tray?.RebuildMenu();
        });
        window.Closed += (_, _) => _endpointSettingsWindow = null;
        window.Show();
        _endpointSettingsWindow = window;
    }

    /// <summary>打开/聚焦聊天窗：窗口锚到宠物附近。</summary>
    private void ShowChat()
    {
        if (_sessionHost is null) return;
        if (_chatWindow is { } w) { w.Activate(); return; }
        var chat = new PetChatWindow(_sessionHost);
        if (_petWindow is { } pet)
        {
            var scaling = pet.Screens.ScreenFromPoint(pet.Position)?.Scaling ?? 1.0;
            chat.Position = new PixelPoint(
                Math.Max(0, pet.Position.X + (int)(pet.Width * scaling) + 8),
                Math.Max(0, pet.Position.Y));
        }
        chat.Closed += (_, _) => _chatWindow = null;
        chat.Show();
        _chatWindow = chat;
    }

    /// <summary>桥接事件 → 宠物状态：真实 Agent 活动驱动（FUFU-04）。</summary>
    private void OnBridgeEvent(string sessionId, string type, string summary)
    {
        // 管道线程回调：跨线程触碰宠物视觉会抛异常，状态切换一律回到 UI 线程。
        Dispatcher.UIThread.Post(() => _behavior?.Notify(type switch
        {
            "activity" => summary switch
            {
                nameof(SessionActivity.Running) or nameof(SessionActivity.Compacting)
                    or nameof(SessionActivity.Cancelling) => PetState.Working,
                nameof(SessionActivity.Retrying) => PetState.Attention,
                _ => PetState.Thinking,
            },
            "settled" => summary is "已完成" or "Completed" ? PetState.Success : PetState.Error,
            "error" => PetState.Error,
            "subagent" => PetState.Working,
            _ => PetState.Thinking,
        }));
    }

    /// <summary>打开/聚焦桥接面板。</summary>
    private void ShowBridge()
    {
        if (_pipeServer is null || _proactive is null || _settings is null || _taskLog is null) return;
        if (_bridgeWindow is { } w) { w.Activate(); return; }
        var window = new BridgeWindow(_pipeServer, _proactive, _settings, _taskLog);
        if (_petWindow is { } pet)
        {
            var scaling = pet.Screens.ScreenFromPoint(pet.Position)?.Scaling ?? 1.0;
            window.Position = new PixelPoint(
                Math.Max(0, pet.Position.X + (int)(pet.Width * scaling) + 8),
                Math.Max(0, pet.Position.Y));
        }
        window.Closed += (_, _) => _bridgeWindow = null;
        window.Show();
        _bridgeWindow = window;
    }

    /// <summary>托盘退出：真正结束 FUFU 进程（任务书 §六.6）。</summary>
    private void Quit()
    {
        if (_exiting) return;
        _exiting = true;
        FufuLog.Write("FUFU quit");
        SavePosition();
        _tray?.Dispose();
        _chatWindow?.Close();
        _endpointSettingsWindow?.Close();
        _bridgeWindow?.Close();
        _bubble?.Close();
        _proactive?.Dispose();
        _pipeServer?.Dispose();
        _sessionHost?.Dispose();
        _petWindow?.Close();
        _wander?.Stop();
        _behavior?.Dispose();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Environment.Exit(0);
    }
}
