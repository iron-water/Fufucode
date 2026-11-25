using System.Text.Json;
using Fufucode.Fui;

namespace Fufucode.Fui.Tests;

internal static class SettingsTests
{
    internal static async Task RunAsync()
    {
        Persistence();
        CorruptFallback();
        SettingsCenter();
        ThemeAndPresentation();
        StatusSegments();
        KeyBindings();
        await NotificationsAsync();
        CapabilityMatrix();
        HyperlinkRendering();
    }

    private static void Persistence()
    {
        var root = NewRoot();
        try
        {
            var store = FuiSettingsStore.Load(root.FullName);
            Eq(FuiSettings.Default, store.Current, "missing file starts from defaults");
            Eq(null, store.LoadIssue, "missing file is not an error");

            var result = store.Update(settings => settings with
            {
                Theme = "light",
                ShowThinking = false,
                ExpandTools = true,
                Hyperlinks = "on",
                DesktopNotifications = "off",
                StatusSegments = ["model", "usage"],
                CompactCards = false,
                MouseWheel = false,
                EmojiAutocomplete = "off",
                VimMode = true,
                Keybindings = new Dictionary<string, string>(settings.Keybindings, StringComparer.Ordinal)
                {
                    [FuiKeyBindingCatalog.Commands] = "ctrl+g"
                }
            });
            Require(result.Ok, result.Error ?? "settings update failed");
            var path = Path.Combine(root.FullName, "fui", "settings.json");
            Require(File.Exists(path), "FUI settings file is under its own data-root namespace");
            Require(!File.Exists(Path.Combine(root.FullName, "settings.json")), "GUI/CLI settings file was not touched");
            Require(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Any(), "atomic write leaves no temp file");

            var second = FuiSettingsStore.Load(root.FullName);
            Eq("light", second.Current.Theme, "theme persisted");
            Eq(false, second.Current.ShowThinking, "showThinking persisted");
            Eq(true, second.Current.ExpandTools, "expandTools persisted");
            Eq("on", second.Current.Hyperlinks, "hyperlinks persisted");
            Eq("off", second.Current.DesktopNotifications, "notifications persisted");
            Eq("model,usage", string.Join(',', second.Current.StatusSegments), "status segments persisted");
            Eq(false, second.Current.CompactCards, "compact cards persisted");
            Eq(false, second.Current.MouseWheel, "mouse wheel persisted");
            Eq("off", second.Current.EmojiAutocomplete, "emoji completion setting persisted");
            Eq(true, second.Current.VimMode, "Vim mode persisted");
            Eq("ctrl+g", second.Current.Keybindings[FuiKeyBindingCatalog.Commands], "key binding persisted");

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Eq(JsonValueKind.Object, document.RootElement.ValueKind, "settings JSON root");
            Eq("light", document.RootElement.GetProperty("theme").GetString(), "JSON field names are stable");
            Eq("ctrl+g", document.RootElement.GetProperty("keybindings")
                .GetProperty(FuiKeyBindingCatalog.Commands).GetString(), "keybinding JSON shape");
        }
        finally { root.Delete(true); }
    }

    private static void CorruptFallback()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root.FullName, "fui", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ broken");
            var store = FuiSettingsStore.Load(root.FullName);
            Require(store.LoadIssue?.Contains("设置文件损坏", StringComparison.Ordinal) == true,
                "corrupt settings report a visible reason");
            Eq(FuiSettings.Default.Theme, store.Current.Theme, "corrupt settings fall back safely");

            var ui = new TuiController(new Session(), CancellationToken.None, root.FullName, store);
            Require(ui.View().Lines.Any(line => line.Kind == "error" &&
                line.Text.Contains("设置文件损坏", StringComparison.Ordinal)), "startup displays the fallback reason");
            var repair = store.Update(settings => settings with { Theme = "dark" });
            Require(repair.Ok, repair.Error ?? "repair failed");
            Eq(null, store.LoadIssue, "successful repair clears the issue");
            Require(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.before-repair-*.bak").Any(),
                "corrupt file is preserved for diagnosis before repair");
            Eq("dark", FuiSettingsStore.Load(root.FullName).Current.Theme, "repaired settings reload");
        }
        finally { root.Delete(true); }
    }

    private static void SettingsCenter()
    {
        var root = NewRoot();
        try
        {
            var store = FuiSettingsStore.Load(root.FullName);
            var ui = new TuiController(new Session(), CancellationToken.None, root.FullName, store);
            Type(ui, "/settings");
            Key(ui, ConsoleKey.Enter);
            var overlay = ui.View().Overlay!;
            Eq("设置", overlay.Title, "settings picker title");
            Require(overlay.Items.Any(item => item.Value == "settings-group:外观"), "settings are grouped");
            Require(overlay.Items.Any(item => item.Value == "theme" && item.Label.Contains("auto")),
                "settings show current values");
            Type(ui, "主题");
            Eq("theme", ui.View().Overlay!.Items[ui.View().Overlay!.Selected].Value, "search finds the theme");
            Key(ui, ConsoleKey.Enter);
            Eq("dark", store.Current.Theme, "settings center cycles and persists the theme");
            Eq("dark", ui.View().Theme, "settings change reaches the view immediately");
            Eq("设置", ui.View().Overlay?.Title, "settings center reopens after a change");

            Key(ui, ConsoleKey.U, ctrl: true);
            Type(ui, "工具");
            Key(ui, ConsoleKey.Enter);
            Eq(true, store.Current.ExpandTools, "toggle setting persists true");
            Eq(true, ui.View().ExpandTools, "toggle setting reaches transcript immediately");

            Key(ui, ConsoleKey.U, ctrl: true);
            Type(ui, "状态分段");
            Key(ui, ConsoleKey.Enter);
            Eq("设置 · 状态分段", ui.View().Overlay?.Title, "multi-select opens its own toggle list");
            Eq(6, ui.View().Overlay!.Items.Count, "all status segments are offered from the registry");
            Key(ui, ConsoleKey.Enter);
            Require(!store.Current.StatusSegments.Contains("model"), "status segment toggles off through settings center");
            Eq("设置 · 状态分段", ui.View().Overlay?.Title, "status picker stays open for multiple toggles");

            Key(ui, ConsoleKey.Escape);
            Type(ui, "/settings");
            Key(ui, ConsoleKey.Enter);
            Type(ui, "Emoji");
            Key(ui, ConsoleKey.Enter);
            Eq("off", store.Current.EmojiAutocomplete, "emoji completion toggles through settings center");
            Key(ui, ConsoleKey.U, ctrl: true);
            Type(ui, "Vim");
            Key(ui, ConsoleKey.Enter);
            Eq(true, store.Current.VimMode, "Vim mode toggles through settings center");
            Eq("INSERT", ui.View().EditorMode, "Vim mode reaches the composer immediately");
        }
        finally { root.Delete(true); }
    }

    private static void ThemeAndPresentation()
    {
        var root = NewRoot();
        try
        {
            var store = FuiSettingsStore.Load(root.FullName);
            var ui = new TuiController(new Session(), CancellationToken.None, root.FullName, store);
            Type(ui, "/theme");
            Key(ui, ConsoleKey.Enter);
            Eq("主题", ui.View().Overlay?.Title, "theme quick panel");
            Type(ui, "light");
            Key(ui, ConsoleKey.Enter);
            Eq("light", store.Current.Theme, "theme quick panel persists");
            Eq("light", ui.View().Theme, "theme quick panel applies immediately");

            Type(ui, "/settings");
            Key(ui, ConsoleKey.Enter);
            Type(ui, "紧凑卡片");
            Key(ui, ConsoleKey.Enter);
            Eq(false, store.Current.CompactCards, "compact cards setting persists");
            Eq(false, ui.View().CompactCards, "compact cards setting reaches the view");
            Key(ui, ConsoleKey.U, ctrl: true);
            Type(ui, "鼠标滚轮");
            Key(ui, ConsoleKey.Enter);
            Eq(false, store.Current.MouseWheel, "mouse wheel setting persists");
            Eq(false, ui.View().MouseWheel, "mouse wheel setting reaches the view");
            ui.Mouse(new MouseEvent(64, 0, 0, false, MouseWheel.Up, false, false), 24, 100, 12, MouseHit.None);
            Eq(0, ui.View().Scroll, "disabled mouse wheel cannot scroll through the direct route");
            var plain = new TerminalLayout().Arrange(ui.View() with
            {
                Lines = [new("user", "hello")]
            }, 80, 12);
            Require(!plain.Rows.Any(row => row.Text.StartsWith("╭─", StringComparison.Ordinal)),
                "disabled cards render without card frames");
        }
        finally { root.Delete(true); }
    }

    private static void StatusSegments()
    {
        var settings = FuiSettings.Default with { StatusSegments = ["model", "project", "usage"] };
        var rendered = FuiStatusLine.Segments(settings, new("fui", "model-a", "FUMO", 2, 1, 3, "17/4"));
        Eq("模型 model-a · 项目 fui · 用量 17/4", rendered, "status segment registry render");
        Require(!rendered.Contains("队列", StringComparison.Ordinal) &&
                !rendered.Contains("子代理", StringComparison.Ordinal),
            "disabled status segments are absent");

        settings = FuiStatusSegmentCatalog.Toggle(settings, "queue");
        Require(settings.StatusSegments.Contains("queue"), "status segment toggle enables");
        settings = FuiStatusSegmentCatalog.Toggle(settings, "project");
        Require(!settings.StatusSegments.Contains("project"), "status segment toggle disables");

        var all = FuiSettings.Default;
        var text = FuiStatusLine.Segments(all, new("fui", "m", "FUMO", 1, 2, 3, null));
        Require(text.Contains("运行时 FUMO", StringComparison.Ordinal) &&
                text.Contains("队列 1", StringComparison.Ordinal) &&
                text.Contains("子代理 2/3", StringComparison.Ordinal) &&
                text.Contains("用量 未知", StringComparison.Ordinal),
            "all configured status segments are represented honestly");

        var root = NewRoot();
        try
        {
            var store = FuiSettingsStore.Load(root.FullName);
            store.Update(current => current with { StatusSegments = ["model", "queue"] });
            var session = new Session { Queue = 2, Usage = "9/3" };
            var ui = new TuiController(session, CancellationToken.None, root.FullName, store);
            var line = string.Join(" · ", ui.View().StatusSegments ?? []);
            Require(line.Contains("模型 loopback", StringComparison.Ordinal) &&
                    line.Contains("队列 2", StringComparison.Ordinal) &&
                    !line.Contains("运行时", StringComparison.Ordinal),
                "controller status line follows persisted selection");
        }
        finally { root.Delete(true); }
    }

    private static void KeyBindings()
    {
        var root = NewRoot();
        try
        {
            var store = FuiSettingsStore.Load(root.FullName);
            var ui = new TuiController(new Session(), CancellationToken.None, root.FullName, store);
            Type(ui, "/keys");
            Key(ui, ConsoleKey.Enter);
            Eq("按键", ui.View().Overlay?.Title, "keys panel");
            Eq(4, ui.View().Overlay!.Items.Count, "four safe actions are exposed");
            Require(ui.View().Overlay!.Items.All(item => item.Label.Contains("ctrl+", StringComparison.Ordinal) ||
                                                          item.Label.Contains("f", StringComparison.Ordinal)),
                "keys panel shows current chords");

            Key(ui, ConsoleKey.Enter); // choose "commands"
            Eq("等待按键", ui.View().Status, "capture mode");
            Key(ui, ConsoleKey.C, ctrl: true);
            Require(ui.View().Lines.Any(line => line.Kind == "error" &&
                line.Text.Contains("保留键", StringComparison.Ordinal)), "reserved binding is rejected");
            Eq("ctrl+p", store.Current.Keybindings[FuiKeyBindingCatalog.Commands], "rejected binding is not persisted");

            Key(ui, ConsoleKey.K, ctrl: true);
            Require(ui.View().Lines.Any(line => line.Kind == "error" &&
                line.Text.Contains("保留键", StringComparison.Ordinal)), "composer kill key is reserved");

            Key(ui, ConsoleKey.R, ctrl: true);
            Require(ui.View().Lines.Any(line => line.Kind == "error" &&
                line.Text.Contains("保留键", StringComparison.Ordinal)), "input history key is reserved");

            Key(ui, ConsoleKey.F, ctrl: true);
            Require(ui.View().Lines.Any(line => line.Kind == "error" &&
                line.Text.Contains("冲突", StringComparison.Ordinal)), "conflicting action binding is rejected");

            Key(ui, ConsoleKey.G, ctrl: true);
            Eq("ctrl+g", store.Current.Keybindings[FuiKeyBindingCatalog.Commands], "valid binding persists");
            Eq("就绪", ui.View().Status, "capture mode closes after success");
            Eq("Ctrl+G", ui.View().CommandKey, "footer hint follows the rebound command key");
            Key(ui, ConsoleKey.G, ctrl: true);
            Eq("命令", ui.View().Overlay?.Title, "rebound command palette works immediately");

            Require(FuiKeyBindingCatalog.TrySet(store.Current, FuiKeyBindingCatalog.SearchTranscript,
                new(ConsoleKey.F, ConsoleModifiers.Control), out _, out var conflict), "same action's current key is allowed");
            Require(!FuiKeyBindingCatalog.TrySet(store.Current, FuiKeyBindingCatalog.SearchTranscript,
                new(ConsoleKey.G, ConsoleModifiers.Control), out _, out conflict) &&
                conflict?.Contains("冲突", StringComparison.Ordinal) == true, "direct API rejects duplicate bindings");
            Require(!FuiKeyChord.TryParse("shift+p", out _, out var shiftOnlyError) &&
                shiftOnlyError?.Contains("Ctrl", StringComparison.Ordinal) == true,
                "shift-only letters are not safe global shortcuts");

            var conflictRoot = NewRoot();
            try
            {
                var path = Path.Combine(conflictRoot.FullName, "fui", "settings.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, """
                    {
                      "version": 1,
                      "keybindings": {
                        "commands": "ctrl+p",
                        "search": "ctrl+p",
                        "tools": "ctrl+o",
                        "thoughts": "ctrl+t"
                      }
                    }
                    """);
                var broken = FuiSettingsStore.Load(conflictRoot.FullName);
                Require(broken.LoadIssue?.Contains("冲突", StringComparison.Ordinal) == true,
                    "conflicting key bindings are diagnosed");
                Eq("ctrl+p", broken.Current.Keybindings[FuiKeyBindingCatalog.Commands],
                    "conflicting file falls back to safe defaults");
                _ = new TuiController(new Session(), CancellationToken.None, conflictRoot.FullName, broken);
            }
            finally { conflictRoot.Delete(true); }
        }
        finally { root.Delete(true); }
    }

    private static async Task NotificationsAsync()
    {
        var settings = FuiSettings.Default;
        var windows = Env(("WT_SESSION", "fixture"), ("TERM_PROGRAM", "Windows_Terminal"),
            ("TERM_PROGRAM_VERSION", "1.0"));
        var wt = FuiTerminalDetector.Detect(windows, windows: true, windowsConsole: true);
        Eq("Windows Terminal", wt.Profile.Family, "Windows Terminal family");
        Eq(true, wt.ConPty, "Windows Terminal is reported as ConPTY");
        var wtPlan = FuiNotificationFormatter.Build("FUI", "done", settings, wt, windows);
        Require(wtPlan.Enabled && wtPlan.Protocol == FuiNotifyProtocol.Osc777 &&
                wtPlan.Sequence.StartsWith("\x1b]777;notify;", StringComparison.Ordinal),
            "Windows Terminal uses OSC 777");

        var tmuxEnv = Env(("WT_SESSION", "fixture"), ("TERM_PROGRAM", "Windows_Terminal"), ("TMUX", "fixture"));
        var tmux = FuiTerminalDetector.Detect(tmuxEnv, windows: true, windowsConsole: true);
        Require(tmux.Innermost?.Id == "tmux", "tmux is detected inside Windows Terminal");
        var tmuxPlan = FuiNotificationFormatter.Build("FUI", "done", settings, tmux, tmuxEnv);
        Require(tmuxPlan.Sequence.StartsWith("\x1bPtmux;", StringComparison.Ordinal) &&
                tmuxPlan.Sequence.Contains("\x1b\x1b]777;notify;", StringComparison.Ordinal),
            "tmux notification preserves the outer terminal's OSC 777 through DCS passthrough");

        var zellijEnv = Env(("ZELLIJ", "fixture"), ("TERM_PROGRAM", "WezTerm"));
        var zellij = FuiTerminalDetector.Detect(zellijEnv, windows: false, windowsConsole: false);
        var zellijPlan = FuiNotificationFormatter.Build("FUI", "done", settings, zellij, zellijEnv);
        Require(zellijPlan.Sequence.EndsWith("\a", StringComparison.Ordinal) &&
                zellijPlan.Sequence.Contains("\x1b]9;", StringComparison.Ordinal),
            "Zellij receives OSC plus BEL fallback");

        var screenEnv = Env(("STY", "fixture"), ("TERM_PROGRAM", "WezTerm"));
        var screen = FuiTerminalDetector.Detect(screenEnv, windows: false, windowsConsole: false);
        var screenPlan = FuiNotificationFormatter.Build("FUI", "done", settings, screen, screenEnv);
        Eq(FuiNotifyProtocol.Bell, screenPlan.Protocol, "screen is downgraded to BEL");
        Eq("\a", screenPlan.Sequence, "screen receives no fake OSC toast");

        var disabled = FuiNotificationFormatter.Build("FUI", "done",
            settings with { DesktopNotifications = "off" }, wt, windows);
        Eq(false, disabled.Enabled, "setting can disable notifications");
        var envDisabled = FuiNotificationFormatter.Build("FUI", "done", settings, wt,
            Env(("WT_SESSION", "fixture"), ("FUI_NOTIFICATIONS", "off")));
        Eq(false, envDisabled.Enabled, "environment kill switch wins");

        await NotificationControllerAsync();
    }

    private static void CapabilityMatrix()
    {
        var cases = new (string Name, IReadOnlyDictionary<string, string?> Env, bool Windows, bool Console,
            string Family, string? Multiplexer, FuiNotifyProtocol Notify)[]
        {
            ("windows-terminal", Env(("WT_SESSION", "1"), ("TERM_PROGRAM", "Windows_Terminal")), true, true,
                "Windows Terminal", null, FuiNotifyProtocol.Osc777),
            ("conpty", Env(), true, true, "Windows Console", null, FuiNotifyProtocol.Bell),
            ("tmux", Env(("TMUX", "1"), ("TERM_PROGRAM", "WezTerm")), false, false,
                "WezTerm", "tmux", FuiNotifyProtocol.Osc9),
            ("zellij", Env(("ZELLIJ", "1"), ("TERM_PROGRAM", "kitty")), false, false,
                "kitty", "zellij", FuiNotifyProtocol.Osc9),
            ("screen", Env(("STY", "1"), ("TERM_PROGRAM", "WezTerm")), false, false,
                "WezTerm", "screen", FuiNotifyProtocol.Osc9),
            ("tmux-term-fallback", Env(("TERM", "tmux-256color")), false, false,
                "未知终端", "tmux", FuiNotifyProtocol.Bell),
            ("screen-term-fallback", Env(("TERM", "screen-256color")), false, false,
                "未知终端", "screen", FuiNotifyProtocol.Bell),
            ("kitty", Env(("KITTY_WINDOW_ID", "1")), false, false,
                "kitty", null, FuiNotifyProtocol.Osc9),
            ("unknown", Env(), false, false, "未知终端", null, FuiNotifyProtocol.Bell)
        };
        foreach (var item in cases)
        {
            var detection = FuiTerminalDetector.Detect(item.Env, item.Windows, item.Console);
            Eq(item.Family, detection.Profile.Family, item.Name + " family");
            Eq(item.Multiplexer, detection.Innermost?.Id, item.Name + " multiplexer");
            Eq(item.Notify, detection.Profile.Notifications, item.Name + " notification protocol");
        }

        var wtEnv = cases[0].Env;
        var wt = FuiTerminalDetector.Detect(wtEnv, true, true);
        Eq("开启（终端配置已知）", FuiTerminalDetector.HyperlinkDecision(FuiSettings.Default, wt, wtEnv),
            "Windows Terminal hyperlinks are known");
        Eq("未知（tmux 需 ≥3.4 且外层启用 hyperlinks）",
            FuiTerminalDetector.HyperlinkDecision(FuiSettings.Default,
                FuiTerminalDetector.Detect(cases[2].Env, false, false), cases[2].Env),
            "tmux hyperlinks remain conservative");
        Eq("关闭（screen 不转发 OSC 8）",
            FuiTerminalDetector.HyperlinkDecision(FuiSettings.Default,
                FuiTerminalDetector.Detect(cases[4].Env, false, false), cases[4].Env),
            "screen disables hyperlinks");

        var runtime = new FuiTerminalRuntimeState(120, 30, 936, 65001, "utf-8", true, true,
            "未知（尚未收到 DA1 应答）", TuiTheme.Resolve("dark"), @"C:\tmp\fui-data");
        var report = FuiDoctor.Format(FuiSettings.Default, wt, runtime, wtEnv);
        Require(report.Contains("Windows Terminal", StringComparison.Ordinal) &&
                report.Contains("120×30", StringComparison.Ordinal) &&
                report.Contains("code page 65001", StringComparison.Ordinal) &&
                report.Contains("未知（尚未收到 DA1 应答）", StringComparison.Ordinal) &&
                report.Contains(@"C:\tmp\fui-data", StringComparison.Ordinal) &&
                report.Contains("FUI build", StringComparison.Ordinal),
            "doctor includes runtime capability matrix and unknown values");
        var unknown = FuiDoctor.Format(FuiSettings.Default,
            FuiTerminalDetector.Detect(Env(), false, false),
            runtime with { Columns = 0, Rows = 0 }, Env());
        Require(unknown.Contains("尺寸", StringComparison.Ordinal) &&
                unknown.Contains("未知终端", StringComparison.Ordinal) &&
                unknown.Contains("未知", StringComparison.Ordinal), "doctor does not invent unknown capabilities");

        var forcedSixel = new SixelSupport("sixel");
        Require(forcedSixel.State.Contains("未确认", StringComparison.Ordinal), "forced Sixel starts unconfirmed");
        forcedSixel.Reply('c', "?1");
        Require(forcedSixel.State.Contains("未声明", StringComparison.Ordinal), "negative DA1 keeps forced Sixel labelled");
        var confirmedSixel = new SixelSupport("auto");
        confirmedSixel.Reply('c', "?1;4");
        Eq("已确认（DA1 声明 4）", confirmedSixel.State, "positive DA1 confirms Sixel");

        var ui = new TuiController(new Session(), CancellationToken.None, terminal: wt, terminalState: runtime);
        Type(ui, "/terminal");
        Key(ui, ConsoleKey.Enter);
        Eq("终端", ui.View().Overlay?.Title, "terminal quick panel");
        Require(ui.View().Overlay!.Items.Any(item => item.Value == "links"), "terminal panel exposes links");
        Key(ui, ConsoleKey.Escape);
        Type(ui, "/doctor");
        Key(ui, ConsoleKey.Enter);
        Require(ui.View().Lines.Any(line => line.Kind == "info" && line.Text.Contains("FUI doctor", StringComparison.Ordinal) &&
            line.Text.Contains("hyperlinks", StringComparison.Ordinal)), "doctor command prints the diagnostic matrix");
    }

    private static void HyperlinkRendering()
    {
        var line = new TuiLine("assistant", "[示例](https://example.com/path)");
        var layout = new TerminalLayout();
        var view = new TuiView("/demo", "S", "M", "就绪", [line], "", 0, 0, null);
        var frame = layout.Arrange(view, 80, 16);
        Require(frame.Rows.Any(row => row.Spans is { } spans &&
            spans.Any(span => span.Link == "https://example.com/path")), "link metadata is preserved outside text");
        Require(!TerminalSurface.Diff(null, frame, TuiTheme.Resolve("dark"),
                TuiHyperlinkPolicy.FromEnabled(false)).Contains("\x1b]8;;", StringComparison.Ordinal),
            "disabled hyperlinks emit no OSC 8");

        var output = TerminalSurface.Diff(null, frame, TuiTheme.Resolve("dark"),
            TuiHyperlinkPolicy.FromEnabled(true));
        Require(output.Contains("\x1b]8;;https://example.com/path\x1b\\", StringComparison.Ordinal) &&
                output.Contains("\x1b]8;;\x1b\\", StringComparison.Ordinal),
            "enabled hyperlinks emit a balanced OSC 8 sequence");

        var settings = FuiSettings.Default;
        var wtEnv = Env(("WT_SESSION", "1"));
        var wt = FuiTerminalDetector.Detect(wtEnv, true, true);
        Require(FuiTerminalDetector.HyperlinksEnabled(settings, wt, wtEnv), "auto enables known terminal links");
        Require(!FuiTerminalDetector.HyperlinksEnabled(settings with { Hyperlinks = "off" }, wt, wtEnv),
            "off disables known terminal links");
        var unknown = FuiTerminalDetector.Detect(new Dictionary<string, string?>
        {
            ["TERM"] = "xterm-256color"
        }, windows: false, windowsConsole: false);
        var forced = new Dictionary<string, string?> { ["FUI_HYPERLINKS"] = "on" };
        Require(FuiTerminalDetector.HyperlinksEnabled(FuiSettings.Default, unknown, forced),
            "FUI_HYPERLINKS=on overrides an unknown terminal");
        Require(FuiTerminalDetector.HyperlinkDecision(FuiSettings.Default, unknown, forced)
                .Contains("FUI_HYPERLINKS", StringComparison.Ordinal),
            "forced hyperlink decision names the environment override");
        Require(!FuiTerminalDetector.HyperlinksEnabled(FuiSettings.Default with { Hyperlinks = "on" }, wt,
                new Dictionary<string, string?> { ["FUI_HYPERLINKS"] = "off" }),
            "FUI_HYPERLINKS=off overrides the persisted setting");
    }

    private static async Task NotificationControllerAsync()
    {
        var session = new Session();
        var calls = new List<string>();
        var ui = new TuiController(session, CancellationToken.None, notification: (_, _, body) =>
        {
            calls.Add(body);
            throw new IOException("notifier fixture failure");
        });
        session.OnTurn(FuiTurnOutcome.Completed, null);
        ui.Update();
        Eq(1, calls.Count, "completion invokes notification once");
        Require(calls[0].Contains("回合已完成", StringComparison.Ordinal), "completion text is truthful");
        session.OnTurn(FuiTurnOutcome.Failed, "model unavailable");
        ui.Update();
        Eq(2, calls.Count, "failure invokes notification");
        Require(calls[1].Contains("回合失败", StringComparison.Ordinal) &&
                calls[1].Contains("model unavailable", StringComparison.Ordinal), "failure summary is included");
        session.OnTurn(FuiTurnOutcome.Interrupted, "stopped");
        ui.Update();
        Eq(2, calls.Count, "interrupted turns do not claim completion");
        await ui.FinishAsync();
    }

    private static DirectoryInfo NewRoot()
    {
        var basePath = Environment.GetEnvironmentVariable("FUFUCODE_TEST_ROOT") ?? Path.GetTempPath();
        return Directory.CreateDirectory(Path.Combine(basePath, "fui-settings-" + Guid.NewGuid().ToString("N")));
    }

    private static IReadOnlyDictionary<string, string?> Env(params (string Key, string Value)[] values) =>
        values.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase);

    private static void Type(TuiController ui, string text)
    {
        foreach (var ch in text)
            ui.Key(new(ch, (ConsoleKey)0, false, false, false), 24, 100, 12);
    }

    private static void Key(TuiController ui, ConsoleKey key, bool ctrl = false, bool alt = false, bool shift = false) =>
        ui.Key(new('\0', key, shift, alt, ctrl), 24, 100, 12);

    private sealed class Session : ITuiSession, IFuiStatusProvider
    {
        private readonly System.Threading.Channels.Channel<TuiEvent> _events =
            System.Threading.Channels.Channel.CreateUnbounded<TuiEvent>();

        public string Project => @"C:\fixture\fui";
        public string Id => "session";
        public string? Model => "loopback";
        public IReadOnlyList<TuiLine> History => [];
        public System.Threading.Channels.ChannelReader<TuiEvent> Events => _events.Reader;
        public int Queue { get; init; }
        public string? Usage { get; init; }
        public int QueuedCount => Queue;
        public string? UsageSummary => Usage;
        public Task SendAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id, bool allow, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void OnTurn(FuiTurnOutcome outcome, string? summary) =>
            _events.Writer.TryWrite(new TuiEvent.TurnFinished(outcome, summary));
    }

    private static void Require(bool condition, string context)
    {
        if (!condition) throw new Exception(context);
    }

    private static void Eq<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{context}: expected [{expected}], got [{actual}]");
    }
}
