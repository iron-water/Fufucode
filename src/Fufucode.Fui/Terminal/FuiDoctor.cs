using System.Reflection;
using System.Text;
using Fufucode.Core.Shell;
using Fufucode.Core.Updates;

namespace Fufucode.Fui;

internal sealed record FuiTerminalRuntimeState(
    int Columns,
    int Rows,
    int OriginalCodePage,
    int CurrentCodePage,
    string EncodingName,
    bool BracketedPasteEnabled,
    bool MouseEnabled,
    string SixelState,
    TuiTheme Theme,
    string DataRoot);

internal static class FuiDoctor
{
    internal static string Format(FuiSettings settings, FuiTerminalDetection detection,
        FuiTerminalRuntimeState runtime, IReadOnlyDictionary<string, string?> environment)
    {
        var shell = ResolveShell();
        var build = BuildInfo.ResolveCurrent();
        var assembly = Assembly.GetEntryAssembly() ?? typeof(FuiDoctor).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
                      assembly.GetName().Version?.ToString() ?? "未知";
        var rows = new List<(string Label, string Value)>
        {
            ("终端", $"{detection.Profile.Family} · {detection.Transport}"),
            ("尺寸", runtime.Columns > 0 && runtime.Rows > 0 ? $"{runtime.Columns}×{runtime.Rows} cells" : "未知"),
            ("UTF-8", $"code page {runtime.CurrentCodePage}（启动前 {runtime.OriginalCodePage}）· {runtime.EncodingName}"),
            ("bracketed paste", runtime.BracketedPasteEnabled ? "已发送启用；终端应答未探测" : "关闭"),
            ("mouse", runtime.MouseEnabled ? "SGR 1006 + 滚轮" : "关闭"),
            ("Sixel", runtime.SixelState),
            ("truecolor", runtime.Theme.ColorDepth switch
            {
                TuiColorDepth.TrueColor => "truecolor（环境/终端已知）",
                TuiColorDepth.Ansi256 => "256 色（环境声明）",
                _ => "ANSI 16 色（未声明 truecolor）"
            }),
            ("hyperlinks", FuiTerminalDetector.HyperlinkDecision(settings, detection)),
            ("notifications", FuiTerminalDetector.NotificationDecision(settings, detection, environment)),
            ("multiplexer", detection.Multiplexers.Count == 0 ? "无" :
                string.Join(" → ", detection.Multiplexers.Select(item => item.Name))),
            ("data root", runtime.DataRoot),
            ("shell", shell),
            ("FUI build", $"{version} · {build.Commit ?? "无提交标记"} · {build.BuiltAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"),
            ("settings", settings.Theme + " · " + string.Join(',', settings.StatusSegments))
        };
        if (detection.Multiplexers.Count != 0)
        {
            rows.Add(("降级", ""));
            rows.AddRange(FuiTerminalDetector.MultiplexerDegradations(detection).Select(item => ("", item)));
        }

        var width = rows.Where(row => row.Label.Length != 0).Max(row => row.Label.Length);
        var text = new StringBuilder("FUI doctor\n");
        foreach (var (label, value) in rows)
            text.Append(label.Length == 0 ? "  " : label.PadRight(width) + "  ").Append(value).Append('\n');
        text.Append("终端标识  ").Append(detection.Profile.Evidence).Append('\n');
        text.Append("说明      未探测的项目不推断为支持；Sixel/bracketed paste 以实际握手为准。");
        return TerminalText.Clean(text.ToString()).TrimEnd();
    }

    internal static IReadOnlyList<FuiChoice> TerminalChoices(FuiSettings settings, FuiTerminalDetection detection,
        FuiTerminalRuntimeState runtime, IReadOnlyDictionary<string, string?> environment)
    {
        var choices = new List<FuiChoice>
        {
            new("family", "终端", $"{detection.Profile.Family} · {detection.Transport}"),
            new("geometry", "尺寸", runtime.Columns > 0 && runtime.Rows > 0 ? $"{runtime.Columns}×{runtime.Rows} cells" : "未知"),
            new("utf8", "UTF-8 / code page", $"{runtime.EncodingName} · 当前 {runtime.CurrentCodePage} · 启动前 {runtime.OriginalCodePage}"),
            new("paste", "bracketed paste", runtime.BracketedPasteEnabled ? "已发送启用；终端应答未探测" : "关闭"),
            new("mouse", "mouse", runtime.MouseEnabled ? "SGR 1006；滚轮由设置控制" : "关闭"),
            new("sixel", "Sixel", runtime.SixelState),
            new("color", "颜色", runtime.Theme.ColorDepth.ToString()),
            new("links", "hyperlinks", FuiTerminalDetector.HyperlinkDecision(settings, detection)),
            new("notify", "notifications", FuiTerminalDetector.NotificationDecision(settings, detection, environment)),
            new("mux", "multiplexer", detection.Multiplexers.Count == 0 ? "无" :
                string.Join(" → ", detection.Multiplexers.Select(item => item.Name))),
            new("evidence", "标识证据", detection.Profile.Evidence)
        };
        choices.AddRange(detection.Multiplexers.Select(item => new FuiChoice(
            "degrade:" + item.Id, item.Name + " 降级",
            $"通知：{item.NotificationBehavior}；图形：{item.GraphicsBehavior}；链接：{item.HyperlinkBehavior}")));
        return choices;
    }

    private static string ResolveShell()
    {
        ResolvedCommandShell? shell;
        string? error;
        try
        {
            shell = FumoRuntimeSettings.ResolveDefaultShell(out error);
        }
        catch (Exception exception)
        {
            shell = null;
            error = exception.GetBaseException().Message;
        }
        if (shell is null) return "未知（" + TerminalText.SingleLine(error ?? "FUMO 未解析") + "）";
        return $"{shell.Kind.DisplayName} · {shell.Executable}" + (shell.IsFallback ? " · 默认不可用，已回退" : "");
    }
}
