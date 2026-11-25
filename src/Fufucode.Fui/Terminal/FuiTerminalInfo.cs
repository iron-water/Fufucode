using System.Globalization;

namespace Fufucode.Fui;

internal enum FuiNotifyProtocol
{
    Bell,
    Osc9,
    Osc777
}

internal sealed record FuiTerminalProfile(
    string Id,
    string Family,
    bool? Hyperlinks,
    FuiNotifyProtocol Notifications,
    string Evidence);

internal sealed record FuiMultiplexerInfo(
    string Id,
    string Name,
    string DetectedBy,
    string NotificationBehavior,
    string GraphicsBehavior,
    string HyperlinkBehavior,
    bool SupportsOscPassthrough);

internal sealed record FuiTerminalDetection(
    FuiTerminalProfile Profile,
    IReadOnlyList<FuiMultiplexerInfo> Multiplexers,
    bool WindowsConsole,
    bool ConPty,
    string Transport)
{
    internal FuiMultiplexerInfo? Innermost => Multiplexers.FirstOrDefault();
}

internal static class FuiTerminalDetector
{
    private sealed record ProfileDefinition(string Id, string Family, bool? Hyperlinks, FuiNotifyProtocol Notifications);

    private static readonly ProfileDefinition[] Profiles =
    [
        new("kitty", "kitty", true, FuiNotifyProtocol.Osc9),
        new("ghostty", "Ghostty", true, FuiNotifyProtocol.Osc9),
        new("wezterm", "WezTerm", true, FuiNotifyProtocol.Osc9),
        new("iterm2", "iTerm2", true, FuiNotifyProtocol.Osc9),
        new("windows-terminal", "Windows Terminal", true, FuiNotifyProtocol.Osc777),
        new("vscode", "VS Code", true, FuiNotifyProtocol.Bell),
        new("alacritty", "Alacritty", true, FuiNotifyProtocol.Bell),
        new("warp", "Warp", false, FuiNotifyProtocol.Osc9),
        new("vte", "VTE", null, FuiNotifyProtocol.Bell),
        new("conpty", "Windows Console", null, FuiNotifyProtocol.Bell),
        new("unknown", "未知终端", null, FuiNotifyProtocol.Bell)
    ];

    internal static FuiTerminalDetection Detect(IReadOnlyDictionary<string, string?> environment,
        bool windows, bool windowsConsole)
    {
        var multiplexers = Multiplexers(environment);
        var profile = Identify(environment, windows, windowsConsole);
        var conPty = windows && windowsConsole && (Has(environment, "WT_SESSION") || profile.Id == "windows-terminal");
        var transport = conPty ? "ConPTY" : windowsConsole ? "Windows Console/ConPTY（无法区分）" :
            Has(environment, "SSH_CONNECTION") || Has(environment, "SSH_TTY") || Has(environment, "SSH_CLIENT") ? "SSH" :
            "未知";
        var evidence = Evidence(profile, environment);
        return new(new FuiTerminalProfile(profile.Id, profile.Family, profile.Hyperlinks, profile.Notifications, evidence),
            multiplexers, windowsConsole, conPty, transport);
    }

    internal static FuiTerminalDetection DetectCurrent() =>
        Detect(EnvironmentDictionary(), OperatingSystem.IsWindows(), WindowsConsoleAttached());

    internal static string HyperlinkDecision(FuiSettings settings, FuiTerminalDetection detection,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        environment ??= EnvironmentDictionary();
        var overrideMode = Get(environment, "FUI_HYPERLINKS").Trim().ToLowerInvariant();
        if (Has(environment, "NO_COLOR")) return "关闭（NO_COLOR）";
        if (overrideMode is "off" or "0" or "false") return "关闭（环境变量）";
        if (overrideMode is "on" or "always" or "1" or "true")
        {
            var blocked = detection.Multiplexers.FirstOrDefault(item => item.Id == "screen");
            return blocked is null ? "开启（FUI_HYPERLINKS 强制）" : "开启（FUI_HYPERLINKS 强制；screen 会丢弃 OSC 8）";
        }
        if (settings.Hyperlinks == "off") return "关闭（设置）";
        if (settings.Hyperlinks == "on")
        {
            var blocked = detection.Multiplexers.FirstOrDefault(item => item.Id == "screen");
            return blocked is null ? "开启（强制；终端支持未探测）" : "开启（强制；screen 会丢弃 OSC 8）";
        }

        if (detection.Multiplexers.Any(item => item.Id == "screen")) return "关闭（screen 不转发 OSC 8）";
        if (detection.Multiplexers.FirstOrDefault(item => item.Id == "tmux") is { } tmux)
        {
            // tmux only forwards OSC 8 when the outer terminal enables the hyperlinks feature.
            return "未知（tmux 需 ≥3.4 且外层启用 hyperlinks）";
        }
        if (detection.Multiplexers.FirstOrDefault() is { } multiplexer)
            return $"未知（{multiplexer.Name} 的 OSC 8 转发未探测）";
        return detection.Profile.Hyperlinks switch
        {
            true => "开启（终端配置已知）",
            false => "关闭（终端不支持）",
            _ => "未知（终端未声明 OSC 8）"
        };
    }

    internal static bool HyperlinksEnabled(FuiSettings settings, FuiTerminalDetection detection,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        environment ??= EnvironmentDictionary();
        var overrideMode = Get(environment, "FUI_HYPERLINKS").Trim().ToLowerInvariant();
        if (Has(environment, "NO_COLOR")) return false;
        if (overrideMode is "off" or "0" or "false") return false;
        if (overrideMode is "on" or "always" or "1" or "true")
            return detection.Multiplexers.All(item => item.Id != "screen");
        return settings.Hyperlinks switch
        {
            "off" => false,
            "on" => detection.Multiplexers.All(item => item.Id != "screen"),
            _ => detection.Multiplexers.Count == 0 && detection.Profile.Hyperlinks == true
        };
    }

    internal static string NotificationDecision(FuiSettings settings, FuiTerminalDetection detection,
        IReadOnlyDictionary<string, string?> environment)
    {
        if (NotificationsDisabled(environment)) return "关闭（环境变量）";
        if (settings.DesktopNotifications == "off") return "关闭（设置）";
        if (detection.Multiplexers.FirstOrDefault() is { } first)
        {
            return first.Id switch
            {
                "tmux" => $"{ProtocolName(MultiplexerProtocol(detection))}（tmux passthrough）· {first.NotificationBehavior}",
                "zellij" => $"{ProtocolName(MultiplexerProtocol(detection))} + BEL（Zellij 应答有限）· {first.NotificationBehavior}",
                "screen" => "仅 BEL（screen 不保证 OSC 通知）",
                "wmux" or "cmux" => "仅 BEL（多路器未确认 OSC 转发）",
                _ => first.NotificationBehavior
            };
        }
        return detection.Profile.Notifications switch
        {
            FuiNotifyProtocol.Osc9 => "OSC 9",
            FuiNotifyProtocol.Osc777 => "OSC 777",
            _ => "BEL（终端未声明文本通知协议）"
        };
    }

    internal static IReadOnlyList<string> MultiplexerDegradations(FuiTerminalDetection detection) =>
        detection.Multiplexers.Select(item =>
            $"{item.Name}：通知 {item.NotificationBehavior}；图形 {item.GraphicsBehavior}；链接 {item.HyperlinkBehavior}").ToArray();

    internal static FuiNotifyProtocol MultiplexerProtocol(FuiTerminalDetection detection) =>
        detection.Profile.Notifications == FuiNotifyProtocol.Bell ? FuiNotifyProtocol.Osc9 : detection.Profile.Notifications;

    internal static string ProtocolName(FuiNotifyProtocol protocol) => protocol switch
    {
        FuiNotifyProtocol.Osc777 => "OSC 777",
        FuiNotifyProtocol.Osc9 => "OSC 9",
        _ => "BEL"
    };

    internal static bool NotificationsDisabled(IReadOnlyDictionary<string, string?> environment)
    {
        var setting = Get(environment, "FUI_NOTIFICATIONS")?.Trim().ToLowerInvariant();
        return setting is "off" or "0" or "false" ||
               Get(environment, "FUI_NO_NOTIFY") == "1" ||
               Get(environment, "FUI_NO_DESKTOP_NOTIFY") == "1";
    }

    internal static IReadOnlyDictionary<string, string?> EnvironmentDictionary()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key) values[key] = entry.Value?.ToString();
        return values;
    }

    internal static string Get(IReadOnlyDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out var value) ? value ?? "" : "";

    internal static bool Has(IReadOnlyDictionary<string, string?> environment, string name) =>
        !string.IsNullOrWhiteSpace(Get(environment, name));

    private static ProfileDefinition Identify(IReadOnlyDictionary<string, string?> environment, bool windows,
        bool windowsConsole)
    {
        var program = Get(environment, "TERM_PROGRAM").Trim().ToLowerInvariant();
        if (Has(environment, "KITTY_WINDOW_ID") || program == "kitty") return Profile("kitty");
        if (Has(environment, "GHOSTTY_RESOURCES_DIR") || program == "ghostty") return Profile("ghostty");
        if (Has(environment, "WEZTERM_PANE") || program is "wezterm") return Profile("wezterm");
        if (Has(environment, "ITERM_SESSION_ID") || program is "iterm.app" or "iterm2") return Profile("iterm2");
        if (Has(environment, "WT_SESSION") || program is "windows_terminal" or "windowsterminal") return Profile("windows-terminal");
        if (Has(environment, "VSCODE_PID") || program == "vscode") return Profile("vscode");
        if (Has(environment, "ALACRITTY_WINDOW_ID") || program == "alacritty") return Profile("alacritty");
        if (program is "warpterminal" or "warp") return Profile("warp");
        var term = Get(environment, "TERM").Trim().ToLowerInvariant();
        if (term.Contains("kitty", StringComparison.Ordinal)) return Profile("kitty");
        if (term.Contains("ghostty", StringComparison.Ordinal)) return Profile("ghostty");
        if (windows && windowsConsole) return Profile("conpty");
        if (term.Contains("vte", StringComparison.Ordinal) || program is "gnome-terminal" or "tilix" or "terminator" or "ptyxis")
            return Profile("vte");
        return Profile("unknown");

        static ProfileDefinition Profile(string id) => Profiles.First(item => item.Id == id);
    }

    private static IReadOnlyList<FuiMultiplexerInfo> Multiplexers(IReadOnlyDictionary<string, string?> environment)
    {
        var result = new List<FuiMultiplexerInfo>();
        if (Has(environment, "TMUX"))
        {
            var version = Get(environment, "TERM_PROGRAM") == "tmux" ? Get(environment, "TERM_PROGRAM_VERSION") : "";
            var modern = ParseVersion(version) is { } parsed && (parsed.Major > 3 || parsed.Major == 3 && parsed.Minor >= 4);
            result.Add(new("tmux", "tmux", "TMUX", "OSC 经 passthrough 转发，另发 BEL",
                "依赖外层终端与 allow-passthrough；Sixel 不保证",
                modern ? "3.4+ 可按外层 terminal-features 转发 OSC 8" : "未确认 ≥3.4，OSC 8 视为未知",
                true));
        }
        if (Has(environment, "ZELLIJ"))
        {
            result.Add(new("zellij", "Zellij", "ZELLIJ", "OSC 后补 BEL；通知 UI 取决于 Zellij/外层",
                "Sixel/内联图形不保证", "OSC 8 透传取决于外层", false));
        }
        if (Has(environment, "STY"))
        {
            result.Add(new("screen", "GNU screen", "STY", "仅 BEL 可靠", "Sixel 不保证", "screen 不支持 OSC 8，链接关闭", false));
        }
        if (Get(environment, "WMUX") == "1" || Has(environment, "WMUX_SURFACE_ID"))
            result.Add(new("wmux", "wmux", "WMUX", "xterm.js 通知行为未探测", "图形由外层 xterm.js 决定", "链接未探测", false));
        if (Has(environment, "CMUX_WORKSPACE_ID") || Has(environment, "CMUX_SURFACE_ID") || Has(environment, "CMUX_REMOTE_TRANSPORT"))
            result.Add(new("cmux", "cmux", "CMUX_*", "需 cmux CLI 定点通知，FUI 不启动外部通知器", "图形未探测", "链接未探测", false));
        if (result.Count == 0)
        {
            var term = Get(environment, "TERM").Trim().ToLowerInvariant();
            if (term.StartsWith("tmux", StringComparison.Ordinal))
                result.Add(new("tmux", "tmux", "TERM", "OSC 经 passthrough 转发，另发 BEL",
                    "依赖外层终端与 allow-passthrough；Sixel 不保证", "未确认 ≥3.4，OSC 8 视为未知", true));
            else if (term.StartsWith("screen", StringComparison.Ordinal))
                result.Add(new("screen", "GNU screen", "TERM", "仅 BEL 可靠", "Sixel 不保证",
                    "screen 不支持 OSC 8，链接关闭", false));
            else if (term.Contains("zellij", StringComparison.Ordinal))
                result.Add(new("zellij", "Zellij", "TERM", "OSC 后补 BEL；通知 UI 取决于 Zellij/外层",
                    "Sixel/内联图形不保证", "OSC 8 透传取决于外层", false));
        }
        return result;
    }

    private static string Evidence(ProfileDefinition profile, IReadOnlyDictionary<string, string?> environment)
    {
        var values = new List<string>();
        foreach (var key in new[] { "TERM_PROGRAM", "TERM_PROGRAM_VERSION", "TERM", "COLORTERM", "WT_SESSION", "KITTY_WINDOW_ID", "WEZTERM_PANE" })
        {
            var value = Get(environment, key);
            if (value.Length != 0) values.Add(key + "=" + value);
        }
        return values.Count == 0 ? "无终端标识环境变量" : string.Join(" · ", values);
    }

    private static (int Major, int Minor)? ParseVersion(string text)
    {
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor)) return null;
        return (major, minor);
    }

    private static bool WindowsConsoleAttached() =>
        OperatingSystem.IsWindows() && WindowsConsoleModes.Attached;

    private static class WindowsConsoleModes
    {
        internal static bool Attached =>
            GetConsoleMode(GetStdHandle(-10), out _) && GetConsoleMode(GetStdHandle(-11), out _);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern nint GetStdHandle(int handle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(nint handle, out uint mode);
    }
}

internal sealed record FuiNotificationPlan(bool Enabled, FuiNotifyProtocol Protocol, string Sequence, string Explanation)
{
    internal static FuiNotificationPlan Disabled(string reason) => new(false, FuiNotifyProtocol.Bell, "", reason);
}

internal static class FuiNotificationFormatter
{
    internal const string AppName = "FUI";

    internal static FuiNotificationPlan Build(string title, string body, FuiSettings settings,
        FuiTerminalDetection detection, IReadOnlyDictionary<string, string?> environment)
    {
        if (FuiTerminalDetector.NotificationsDisabled(environment))
            return FuiNotificationPlan.Disabled("环境变量已关闭通知。");
        if (settings.DesktopNotifications == "off")
            return FuiNotificationPlan.Disabled("设置已关闭通知。");

        title = Payload(title, AppName);
        body = Payload(body, "");
        var multiplexer = detection.Innermost;
        if (multiplexer?.Id == "screen")
            return new(true, FuiNotifyProtocol.Bell, "\a", "screen：仅发送 BEL，不声称文本 toast。");

        if (multiplexer?.Id is "wmux" or "cmux")
            return new(true, FuiNotifyProtocol.Bell, "\a", multiplexer.Name + "：未确认 OSC 转发，仅发送 BEL。");
        var protocol = multiplexer?.Id is "tmux" or "zellij"
            ? FuiTerminalDetector.MultiplexerProtocol(detection)
            : detection.Profile.Notifications;
        if (protocol == FuiNotifyProtocol.Bell)
            return new(true, protocol, "\a", "终端未声明 OSC 文本通知协议，仅发送 BEL。");

        var sequence = protocol == FuiNotifyProtocol.Osc777
            ? $"\x1b]777;notify;{title};{body}\a"
            : $"\x1b]9;{(body.Length == 0 ? title : title + ": " + body)}\a";
        if (multiplexer?.Id == "tmux")
        {
            sequence = "\x1bPtmux;" + sequence.Replace("\x1b", "\x1b\x1b", StringComparison.Ordinal) + "\x1b\\\a";
            return new(true, protocol, sequence,
                $"tmux：{FuiTerminalDetector.ProtocolName(protocol)} 经 passthrough；{multiplexer.NotificationBehavior}。");
        }
        if (multiplexer?.Id == "zellij")
            return new(true, protocol, sequence + "\a",
                $"Zellij：{FuiTerminalDetector.ProtocolName(protocol)} 后补 BEL；是否显示 toast 取决于宿主。");
        return new(true, protocol, sequence, detection.Profile.Family + "：终端协议通知。");
    }

    private static string Payload(string value, string fallback)
    {
        var clean = TerminalText.SingleLine(value).Replace('\a', ' ').Replace('\x1b', ' ').Trim();
        if (clean.Length == 0) clean = fallback;
        return TerminalText.Clip(clean, 640, tail: true);
    }
}
