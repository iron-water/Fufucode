using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Live2D.Core;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ProbeNative;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
    private readonly DispatcherTimer _sample = new() { Interval = TimeSpan.FromSeconds(1) };
    private MetricsCollector? _metrics;
    private bool _passThrough;
    private string _recordCsv = "", _recordJson = "";
    private DateTime _t0 = DateTime.UtcNow;
    private readonly List<string> _autoLog = new();

    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    const int GWL_EXSTYLE = -20;
    const nint WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        BtnMotion.Click += (_, _) => { if (View.Model?.PlayRandomMotion("TapBody", out var f) == true) Log($"motion TapBody→{Path.GetFileName(f)}"); else if (View.Model?.PlayRandomMotion("Idle", out var f2) == true) Log($"motion Idle→{Path.GetFileName(f2)}"); };
        BtnReload.Click += (_, _) => ReloadModel();
        BtnThrough.Click += (_, _) => TogglePassThrough();
        BtnMesh.Click += (_, _) => { View.ShowHitRegions = !View.ShowHitRegions; };
        View.PointerPressed += OnPointerDown;
        View.PointerMoved += OnPointerMove;

        Opened += (_, _) =>
        {
            try { LoadModel(); } catch (Exception ex) { Status.Text = "LOAD FAIL: " + ex.Message; Log("loadfail " + ex.Message); }
            if (Program.RecordSeconds > 0)
            {
                _recordCsv = Path.Combine(Program.OutDir, "probe-native.metrics.csv");
                _recordJson = Path.Combine(Program.OutDir, "probe-native.metrics.json");
                _metrics = new MetricsCollector(_recordCsv);
                _sample.Tick += AutoScript;
                _sample.Start();
            }
            _tick.Tick += Tick;
            _tick.Start();
        };
        Closing += (_, _) => FinishRecord();
    }

    private void Log(string s) { _autoLog.Add($"[{(DateTime.UtcNow - _t0).TotalSeconds:F1}] {s}"); }

    private void LoadModel()
    {
        var specPath = VendorPaths.ResolveModelJson(Program.ModelName);
        var model = CubismModel.Load(specPath);
        View.LoadModel(model);
        // idle 循环动作
        model.PlayRandomMotion("Idle", out _);
        Log($"loaded {specPath} drawables={model.DrawableCount} canvas={model.CanvasWPx}x{model.CanvasHPx}px ppu={model.Ppu}");
        Status.Text = $"native | {Program.ModelName} drawables={model.DrawableCount}";
    }

    private void ReloadModel()
    {
        var old = View.Model;
        try { LoadModel(); Log("reload ok"); }
        catch (Exception ex) { Log("reload fail " + ex.Message); Status.Text = "RELOAD FAIL: " + ex.Message; }
        old?.Dispose();
    }

    private void Tick(object? s, EventArgs e)
    {
        View.Model?.Update(1f / 60f);
        View.RenderTick();
        _metrics?.Frame();
        if (View.Model != null)
            Status.Text = $"native | {Program.ModelName} fps~{_lastFps:F0} | hit={_lastHit ?? "-"} | {(_passThrough ? "THROUGH" : "")} {View.Model.Clock:F1}s";
    }

    private float _lastFps;
    private string? _lastHit;
    private int _frameCount;

    private void OnPointerMove(object? s, PointerEventArgs e)
    {
        _lastHit = View.HitTest(e.GetPosition(View));
    }

    private void OnPointerDown(object? s, PointerPressedEventArgs e)
    {
        var hit = View.HitTest(e.GetPosition(View));
        if (hit != null)
        {
            Log($"hit={hit}");
            if (View.Model?.PlayRandomMotion(hit == "Head" ? "TapHead" : "TapBody", out var f) == true)
                Log($"tap-motion {Path.GetFileName(f)}");
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }
        else if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void TogglePassThrough()
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        var style = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if (!_passThrough)
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(style | WS_EX_TRANSPARENT | WS_EX_LAYERED));
        else
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(style & ~WS_EX_TRANSPARENT));
        _passThrough = !_passThrough;
        Log($"passthrough={_passThrough}");
    }

    // —— 自动脚本（--record 模式）：确定性时序驱动验收项 ——
    private int _scriptStep;
    private void AutoScript(object? s, EventArgs e)
    {
        _metrics?.Sample();
        _frameCount = 0;
        double t = (DateTime.UtcNow - _t0).TotalSeconds;
        switch (_scriptStep++)
        {
            case 3: { string f = "-"; var ok = View.Model?.PlayRandomMotion("TapBody", out f) == true; Log($"t{t:F0} tap {Path.GetFileName(f)}"); break; }
            case 6: { string f = "-"; View.Model?.PlayRandomMotion("Idle", out f); Log($"t{t:F0} idle {Path.GetFileName(f)}"); break; }
            case 8: Shot("shot-t8.png"); break;
            case 9: { var h1 = View.HitTest(new Point(210, 300)); var h2 = View.HitTest(new Point(30, 30)); Log($"t{t:F0} hit={h1 ?? "null"} off={h2 ?? "null"}"); break; }
            case 10: WindowState = WindowState.Minimized; Log($"t{t:F0} minimize"); break;
            case 14: WindowState = WindowState.Normal; Log($"t{t:F0} restore"); break;
            case 16: Shot("shot-t16.png"); break;
            case 18: ReloadModel(); break;
            case 22: Shot("shot-t22.png"); break;
        }
        _lastFps = _metrics is null ? 0 : _metrics.RenderedFrames;
        if (t >= Program.RecordSeconds) { FinishRecord(); Close(); }
    }

    private void Shot(string name)
    {
        try
        {
            var rtb = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
            rtb.Render(View);
            rtb.Save(Path.Combine(Program.OutDir, "native-" + name));
            Log($"shot {name}");
        }
        catch (Exception ex) { Log("shot fail " + ex.Message); }
    }

    private bool _finished;
    private void FinishRecord()
    {
        if (_finished || _metrics == null) return;
        _finished = true;
        _metrics.WriteSummary(_recordJson, new()
        {
            ["probe"] = "native-skia",
            ["model"] = Program.ModelName,
            ["log"] = _autoLog,
            ["webview2_procs"] = (int?)null,
        });
        _metrics.Dispose();
    }
}
