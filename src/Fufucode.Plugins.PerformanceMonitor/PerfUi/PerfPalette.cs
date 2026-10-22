using System;
using System.Globalization;
using Avalonia.Media;

namespace Fufucode.Plugins.PerformanceMonitor;

/// <summary>Perf UI 的语义色与格式化。颜色只表达类别，不随主题变化；表面与文字由主题资源提供。</summary>
internal static class PerfPalette
{
    public static readonly Color Model = Color.Parse("#4CC2FF");
    public static readonly Color Tool = Color.Parse("#6CCB5F");
    public static readonly Color Ui = Color.Parse("#B48CFF");
    public static readonly Color Persist = Color.Parse("#FF9E4C");
    public static readonly Color Approval = Color.Parse("#FFC83D");
    public static readonly Color Context = Color.Parse("#8B7CF6");
    public static readonly Color Error = Color.Parse("#E85A5A");
    public static readonly Color System = Color.Parse("#60A5FA");
    public static readonly Color Neutral = Color.Parse("#8A8A8E");

    public static readonly IBrush ModelBrush = new SolidColorBrush(Model);
    public static readonly IBrush ToolBrush = new SolidColorBrush(Tool);
    public static readonly IBrush UiBrush = new SolidColorBrush(Ui);
    public static readonly IBrush PersistBrush = new SolidColorBrush(Persist);
    public static readonly IBrush ApprovalBrush = new SolidColorBrush(Approval);
    public static readonly IBrush ContextBrush = new SolidColorBrush(Context);
    public static readonly IBrush ErrorBrush = new SolidColorBrush(Error);
    public static readonly IBrush SystemBrush = new SolidColorBrush(System);
    public static readonly IBrush NeutralBrush = new SolidColorBrush(Neutral);

    public static Color CategoryColor(string category) => category switch
    {
        "model" => Model,
        "tool" => Tool,
        "ui" => Ui,
        "persist" => Persist,
        "context" => Context,
        "error" => Error,
        "subagent" => Approval,
        "mcp" => System,
        _ => Neutral,
    };

    public static IBrush CategoryBrush(string category) => new SolidColorBrush(CategoryColor(category));

    public static string FormatMs(double ms) => ms switch
    {
        >= 3_600_000 => $"{(int)(ms / 3_600_000)}h {(int)(ms % 3_600_000 / 60_000)}m",
        >= 60_000 => $"{(int)(ms / 60_000)}m {(int)(ms % 60_000 / 1_000)}s",
        >= 1_000 => $"{ms / 1_000:0.0}s",
        >= 10 => $"{ms:0} ms",
        _ => $"{ms:0.0} ms",
    };

    public static string FormatCompactMs(double ms) => ms switch
    {
        >= 60_000 => $"{(int)(ms / 60_000)}m",
        >= 1_000 => $"{ms / 1_000:0.0}s",
        >= 10 => $"{ms:0}ms",
        _ => $"{ms:0.0}ms",
    };

    public static string FormatTokens(long tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000d:0.0}M";
        if (tokens >= 1_000) return $"{tokens / 1_000d:0.0}K";
        return tokens.ToString("N0", CultureInfo.InvariantCulture);
    }

    public static string FormatPercent(double fraction) => $"{fraction * 100:0.0}%";

    public static string ToolDisplayName(string? raw) => raw switch
    {
        "run_command" or "bash" or "shell" => "Bash",
        "read_file" or "read" => "Read",
        "write_file" or "write" => "Write",
        "list_directory" or "ls" => "LS",
        "glob_files" or "glob" => "Glob",
        "grep" or "search_files" => "Grep",
        "apply_patch" or "patch" => "Patch",
        "web_fetch" or "fetch" => "Web",
        "tool_search" => "Tool search",
        "update_plan" or "plan" => "Plan",
        "task" or "agent" => "Task",
        null or "" => "Tool",
        var value when value.StartsWith("mcp", StringComparison.OrdinalIgnoreCase) => "MCP",
        _ => raw,
    };

    public static string FormatDuration(TimeSpan value) => value.TotalMilliseconds switch
    {
        >= 3_600_000 => $"{(int)value.TotalHours}h {value.Minutes}m",
        >= 60_000 => $"{(int)value.TotalMinutes}m {value.Seconds}s",
        >= 1_000 => $"{value.TotalSeconds:0.0}s",
        _ => $"{value.TotalMilliseconds:0} ms",
    };
}
