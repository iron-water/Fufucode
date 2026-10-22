using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Live2D.Core;
using Microsoft.Web.WebView2.Core;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ProbeWebView2;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _sample = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _bounds = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private MetricsCollector? _metrics;
    private bool _passThrough, _occluded;
    private string _recordCsv = "", _recordJson = "";
    private DateTime _t0 = DateTime.UtcNow;
    private readonly List<string> _autoLog = new();
    private int _jsFrames;
    private readonly List<int> _jsFpsSamples = new();
    private string? _lastHit = "-";
    private int _hitSeq;

    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    const int GWL_EXSTYLE = -20;
    const nint WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        BtnMotion.Click += (_, _) => Post(new { type = "motion", group = "TapBody" });
        BtnHit.Click += (_, _) => { };
        BtnThrough.Click += (_, _) => TogglePassThrough();
        BtnOffscreen.Click += (_, _) => ToggleOcclusion();
        View.PointerMoved += OnPointerMove;
        View.PointerPressed += OnPointerDown;
        View.Ready += OnWebViewReady;
        View.Failed += m => Avalonia.Threading.Dispatcher.UIThread.Post(() => { Status.Text = "WV2-FAIL " + m; Log("wv2 init fail: " + m); });

        Opened += (_, _) =>
        {
            if (Program.RecordSeconds > 0)
            {
                _recordCsv = Path.Combine(Program.OutDir, "probe-webview2.metrics.csv");
                _recordJson = Path.Combine(Program.OutDir, "probe-webview2.metrics.json");
                _metrics = new MetricsCollector(_recordCsv, "msedgewebview2");
                _sample.Tick += AutoScript;
                _sample.Start();
            }
            _bounds.Tick += (_, _) => View.FitBounds();
            _bounds.Start();
        };
        Closing += (_, _) => FinishRecord();
    }

    private void Log(string s) => _autoLog.Add($"[{(DateTime.UtcNow - _t0).TotalSeconds:F1}] {s}");

    private void OnWebViewReady(CoreWebView2 core)
    {
        if (core == null) return;
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                var m = JsonDocument.Parse(e.WebMessageAsJson).RootElement;
                var t = m.GetProperty("type").GetString();
                switch (t)
                {
                    case "ready":
                        Log("wv2 page ready");
                        Post(new { type = "load", name = Program.ModelName });
                        break;
                    case "loaded":
                        Log($"model loaded drawables={m.GetProperty("drawables")} canvas={m.GetProperty("canvas")} ppu={m.GetProperty("ppu")}");
                        break;
                    case "metrics":
                        _jsFrames = (int)m.GetProperty("fps").GetDouble();
                        if (_jsFrames > 0) _jsFpsSamples.Add(_jsFrames);
                        _metrics?.Frame();
                        break;
                    case "hit":
                        _lastHit = m.GetProperty("name").GetString() ?? "-";
                        Log($"hitTest→{_lastHit}");
                        break;
                    case "motion": Log($"js motion {m.GetProperty("file")}"); break;
                    case "log": Log("js: " + m.GetProperty("msg").GetString()); break;
                    case "err": Log("JS-ERR " + m.GetProperty("msg").GetString()); Status.Text = "JS-ERR"; break;
                }
            }
            catch { }
        };
        Post(new { type = "load", name = Program.ModelName });
    }

    private void Post(object m) => View.Core?.PostWebMessageAsJson(JsonSerializer.Serialize(m));

    private void OnPointerMove(object? s, PointerEventArgs e)
    {
        var p = e.GetPosition(View);
        Post(new { type = "hitTest", id = ++_hitSeq, x = p.X, y = p.Y });
    }

    private void OnPointerDown(object? s, PointerPressedEventArgs e)
    {
        var p = e.GetPosition(View);
        Post(new { type = "hitTest", id = ++_hitSeq, x = p.X, y = p.Y });
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void TogglePassThrough()
    {
        var hwnd = View.ChildHwnd;
        if (hwnd == IntPtr.Zero) return;
        var style = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if (!_passThrough)
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(style | WS_EX_TRANSPARENT));
        else
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(style & ~WS_EX_TRANSPARENT));
        _passThrough = !_passThrough;
        Log($"passthrough={_passThrough}");
    }

    /// <summary>模拟遮挡：把 WebView2 子窗移到屏幕外（WebView2 收到遮挡通知→节流的实测）。</summary>
    private void ToggleOcclusion()
    {
        var hwnd = View.ChildHwnd;
        if (hwnd == IntPtr.Zero) return;
        _occluded = !_occluded;
        // 缩放为 1px 触发 WebView2 遮挡/可见性节流
        View.IsVisible = !_occluded;
        if (_occluded) { View.Height = 1; View.Width = 1; }
        else { View.Height = double.NaN; View.Width = double.NaN; }
        Log($"occluded={_occluded}");
    }

    private int _scriptStep;
    private float _lastJsFps;
    private void AutoScript(object? s, EventArgs e)
    {
        _metrics?.Sample();
        double t = (DateTime.UtcNow - _t0).TotalSeconds;
        switch (_scriptStep++)
        {
            case 3: Post(new { type = "motion", group = "TapBody" }); Log($"t{t:F0} tap"); break;
            case 6: Post(new { type = "motion", group = "Idle" }); Log($"t{t:F0} idle"); break;
            case 8: Shot("shot-t8.png"); break;
            case 9: Post(new { type = "hitTest", id = 1, x = 210, y = 300 }); Post(new { type = "hitTest", id = 2, x = 30, y = 30 }); break;
            case 10: WindowState = WindowState.Minimized; Log($"t{t:F0} minimize"); break;
            case 14: WindowState = WindowState.Normal; Log($"t{t:F0} restore"); break;
            case 16: Shot("shot-t16.png"); break;
            case 18: Post(new { type = "load", name = Program.ModelName }); Log($"t{t:F0} reload"); break;
            case 22: Shot("shot-t22.png"); break;
        }
        Status.Text = $"webview2 | {Program.ModelName} jsFps={_jsFrames} hit={_lastHit} {(_passThrough ? "THROUGH " : "")}{(_occluded ? "HIDDEN " : "")}t={t:F0}s";
        if (t >= Program.RecordSeconds) { FinishRecord(); Close(); }
    }

    private void Shot(string name)
    {
        try
        {
            var rtb = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
            rtb.Render(this);
            rtb.Save(Path.Combine(Program.OutDir, "webview2-" + name));
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
            ["probe"] = "webview2-webgl",
            ["model"] = Program.ModelName,
            ["log"] = _autoLog,
            ["js_fps_avg"] = _jsFpsSamples.Count > 0 ? _jsFpsSamples.Average() : 0,
        });
        _metrics.Dispose();
    }
}
