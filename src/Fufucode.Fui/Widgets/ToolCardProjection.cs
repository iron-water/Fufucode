using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fufucode.App.Abstractions;

namespace Fufucode.Fui;

internal enum ToolCardKind
{
    Read,
    Write,
    Edit,
    List,
    Grep,
    Glob,
    Command,
    Plan,
    Task,
    Plugin,
    WebSearch,
    Fetch,
    Unknown
}

/// <summary>The facts FUI actually received for one tool call. No display-only facts are inferred here.</summary>
internal sealed record ToolCallSnapshot(
    string Name,
    string ArgumentsJson,
    ToolCallStatus? Status = null,
    string? StatusText = null,
    string? Output = null,
    bool IsErrorOutput = false,
    long? DurationMs = null);

internal sealed record ToolCardText(string Text, Ink Ink = Ink.Tool, string? Language = null);

internal sealed record ToolCardSection(
    string Title,
    IReadOnlyList<ToolCardText> Lines,
    bool Markdown = false,
    bool Truncated = false);

internal sealed record ToolCardView(
    ToolCardKind Kind,
    string ToolName,
    string Verb,
    string? Target,
    ToolCallStatus? Status,
    string StatusLabel,
    string? StatusText,
    IReadOnlyList<string> Stats,
    IReadOnlyList<ToolCardSection> Sections,
    bool IsError);

/// <summary>Tool-name to semantic-card mapping. Unknown names remain first-class fallback cards.</summary>
internal static class ToolCardRegistry
{
    private static readonly IReadOnlyDictionary<string, ToolCardKind> Exact =
        new Dictionary<string, ToolCardKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["read"] = ToolCardKind.Read,
            ["read_file"] = ToolCardKind.Read,
            ["readfile"] = ToolCardKind.Read,
            ["read_text_file"] = ToolCardKind.Read,
            ["open_file"] = ToolCardKind.Read,

            ["write"] = ToolCardKind.Write,
            ["write_file"] = ToolCardKind.Write,
            ["writefile"] = ToolCardKind.Write,
            ["create_file"] = ToolCardKind.Write,
            ["createfile"] = ToolCardKind.Write,

            ["edit"] = ToolCardKind.Edit,
            ["edit_file"] = ToolCardKind.Edit,
            ["editfile"] = ToolCardKind.Edit,
            ["apply_patch"] = ToolCardKind.Edit,
            ["replace_in_file"] = ToolCardKind.Edit,
            ["str_replace"] = ToolCardKind.Edit,
            ["str_replace_editor"] = ToolCardKind.Edit,
            ["multiedit"] = ToolCardKind.Edit,

            ["list"] = ToolCardKind.List,
            ["list_dir"] = ToolCardKind.List,
            ["ls"] = ToolCardKind.List,
            ["list_directory"] = ToolCardKind.List,

            ["grep"] = ToolCardKind.Grep,
            ["grep_search"] = ToolCardKind.Grep,
            ["search_files"] = ToolCardKind.Grep,
            ["search_content"] = ToolCardKind.Grep,
            ["find"] = ToolCardKind.Grep,

            ["glob"] = ToolCardKind.Glob,
            ["glob_files"] = ToolCardKind.Glob,
            ["find_files"] = ToolCardKind.Glob,

            ["run_command"] = ToolCardKind.Command,
            ["bash"] = ToolCardKind.Command,
            ["shell"] = ToolCardKind.Command,
            ["exec"] = ToolCardKind.Command,
            ["command"] = ToolCardKind.Command,
            ["run_shell_command"] = ToolCardKind.Command,
            ["terminal"] = ToolCardKind.Command,

            ["update_plan"] = ToolCardKind.Plan,
            ["todo_write"] = ToolCardKind.Plan,
            ["todo"] = ToolCardKind.Plan,
            ["plan"] = ToolCardKind.Plan,

            ["task"] = ToolCardKind.Task,
            ["subagent"] = ToolCardKind.Task,
            ["subagent_task"] = ToolCardKind.Task,
            ["spawn_agent"] = ToolCardKind.Task,
            ["agent"] = ToolCardKind.Task,
            ["delegate"] = ToolCardKind.Task,
            ["task_agent"] = ToolCardKind.Task,

            ["web_search"] = ToolCardKind.WebSearch,
            ["websearch"] = ToolCardKind.WebSearch,
            ["search_web"] = ToolCardKind.WebSearch,
            ["browser_search"] = ToolCardKind.WebSearch,
            ["web_search_preview"] = ToolCardKind.WebSearch,

            ["fetch"] = ToolCardKind.Fetch,
            ["fetch_url"] = ToolCardKind.Fetch,
            ["read_url"] = ToolCardKind.Fetch,
            ["http_get"] = ToolCardKind.Fetch,
            ["browser_open"] = ToolCardKind.Fetch,
            ["browse_url"] = ToolCardKind.Fetch
        };

    internal static ToolCardKind Classify(string? toolName)
    {
        var name = TerminalText.SingleLine(toolName ?? "").Trim();
        if (name.Length == 0) return ToolCardKind.Unknown;
        if (Exact.TryGetValue(name, out var kind)) return kind;

        var normalized = name.ToLowerInvariant();
        return normalized.StartsWith("mcp__", StringComparison.Ordinal) ||
               normalized.StartsWith("mcp.", StringComparison.Ordinal) ||
               normalized.StartsWith("mcp_", StringComparison.Ordinal) ||
               normalized.StartsWith("plugin__", StringComparison.Ordinal) ||
               normalized.StartsWith("plugin.", StringComparison.Ordinal) ||
               normalized.StartsWith("plugin_", StringComparison.Ordinal)
            ? ToolCardKind.Plugin
            : ToolCardKind.Unknown;
    }
}

/// <summary>
/// Pure projection from received tool facts to a bounded semantic card. It performs no I/O,
/// no tool dispatch, no transcript writes and no timing estimates.
/// </summary>
internal static class ToolCallProjection
{
    private const int ArgumentLineLimit = 72;
    private const int ArgumentCharLimit = 8 * 1024;
    private const int OutputLineLimit = 140;
    private const int OutputCharLimit = 20 * 1024;

    private static readonly Regex ReplacementCount = new(
        @"(?:替换了|共替换)\s*(?<count>\d+)\s*处",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static ToolCardView Project(ToolCallSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var name = SafeSingle(snapshot.Name, "工具");
        var kind = ToolCardRegistry.Classify(name);
        var args = TerminalText.Clean(snapshot.ArgumentsJson ?? "");
        var output = snapshot.Output is null ? null : TerminalText.Clean(snapshot.Output);
        var statusText = string.IsNullOrWhiteSpace(snapshot.StatusText)
            ? null
            : TerminalText.Clean(snapshot.StatusText).Trim();
        using var json = ParseJson(args);
        var root = json?.RootElement;

        var plan = kind == ToolCardKind.Plan ? ReadPlan(root, output) : PlanFacts.Empty;
        var command = kind == ToolCardKind.Command ? ReadCommand(output) : CommandFacts.Empty;
        var list = kind == ToolCardKind.List ? ReadList(output) : ListFacts.Empty;
        var grep = kind == ToolCardKind.Grep ? ReadGrep(output) : GrepFacts.Empty;
        var glob = kind == ToolCardKind.Glob ? ReadGlob(output) : GlobFacts.Empty;

        var target = ReadTarget(kind, root, plan, command);
        var stats = ReadStats(snapshot, kind, root, output, plan, command, list, grep, glob);
        var sections = new List<ToolCardSection>();
        if (statusText is not null && !ContainsStatus(output, statusText))
            sections.Add(Bound("状态", statusText, maxLines: 12, maxChars: 2 * 1024));

        AddArgumentSections(sections, kind, root, args, plan);
        AddResultSections(sections, kind, root, output, plan, command, list, grep, glob);

        return new(
            kind,
            name,
            Verb(kind, name),
            target,
            snapshot.Status,
            StatusLabel(snapshot.Status),
            statusText,
            stats,
            sections,
            snapshot.Status == ToolCallStatus.Failed || snapshot.IsErrorOutput);
    }

    internal static ToolCardView ProjectLegacy(string text)
    {
        var clean = TerminalText.Clean(text);
        var newline = clean.IndexOf('\n');
        var first = (newline < 0 ? clean : clean[..newline]).Trim();
        var body = newline < 0 ? "" : clean[(newline + 1)..];
        if (first.Length == 0) return Project(new ToolCallSnapshot("工具", "", Output: body));

        var split = first.IndexOf(' ');
        var name = split < 0 ? first : first[..split];
        var rest = split < 0 ? "" : first[(split + 1)..].Trim();
        ToolCallStatus? status = null;
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close > 1)
            {
                status = rest[1..close].Trim() switch
                {
                    "执行中" => ToolCallStatus.Running,
                    "完成" => ToolCallStatus.Completed,
                    "失败" => ToolCallStatus.Failed,
                    "已取消" => ToolCallStatus.Cancelled,
                    "待执行" => ToolCallStatus.Pending,
                    _ => null
                };
                rest = rest[(close + 1)..].Trim();
            }
        }

        return Project(new ToolCallSnapshot(name, rest, status, Output: body,
            IsErrorOutput: status == ToolCallStatus.Failed));
    }

    private static void AddArgumentSections(List<ToolCardSection> sections, ToolCardKind kind,
        JsonElement? root, string args, PlanFacts plan)
    {
        if (kind == ToolCardKind.Plan)
        {
            if (plan.HasItems)
            {
                sections.Add(new("计划", plan.Items.Select(item => new ToolCardText(
                    $"{item.Glyph} {item.Number}. {item.Title} · {item.StateLabel}",
                    PlanInk(item.State))).ToArray()));
                if (!string.IsNullOrWhiteSpace(plan.Notes))
                    sections.Add(Bound("备注", plan.Notes, maxLines: 20, maxChars: 4 * 1024));
            }
            else
            {
                AddRawArguments(sections, args, root);
            }
            return;
        }

        if (root is not { } value)
        {
            AddRawArguments(sections, args, root: null);
            return;
        }

        switch (kind)
        {
            case ToolCardKind.Write:
            {
                var parameters = new List<ToolCardText>();
                AddFact(parameters, "path", StringArg(value, "path", "file_path", "file"));
                var content = StringArg(value, "content", "text");
                if (content is not null)
                    AddFact(parameters, "content", $"{LineCount(content)} 行 / {content.Length} 字符");
                AddBoolFact(parameters, "replace", value, "replace");
                AddTextSection(sections, "参数", parameters);
                if (!string.IsNullOrEmpty(content))
                    sections.Add(Bound("写入内容", content, LanguageForPath(PathArg(value)),
                        maxLines: ArgumentLineLimit, maxChars: ArgumentCharLimit));
                break;
            }
            case ToolCardKind.Edit:
            {
                var parameters = new List<ToolCardText>();
                AddFact(parameters, "path", PathArg(value));
                if (TryArrayLength(value, "edits", out var editCount))
                    AddFact(parameters, "edits", $"{editCount} 条");
                AddBoolFact(parameters, "replace_all", value, "replace_all");
                AddTextSection(sections, "参数", parameters);
                var preview = EditArgumentPreview(value);
                if (preview.Length > 0)
                    sections.Add(Bound("改动参数", preview, "diff",
                        maxLines: ArgumentLineLimit, maxChars: ArgumentCharLimit));
                break;
            }
            case ToolCardKind.Command:
            {
                var parameters = new List<ToolCardText>();
                AddFact(parameters, "cwd", StringArg(value, "cwd", "working_directory", "workdir"));
                AddFact(parameters, "shell", StringArg(value, "shell"));
                AddNumberFact(parameters, "timeout_seconds", value, "timeout_seconds");
                AddTextSection(sections, "参数", parameters);
                var command = StringArg(value, "command", "cmd", "script");
                if (!string.IsNullOrEmpty(command))
                    sections.Add(Bound("命令", command, LanguageForShell(StringArg(value, "shell")),
                        maxLines: ArgumentLineLimit, maxChars: ArgumentCharLimit));
                else
                    AddRawArguments(sections, args, value);
                break;
            }
            case ToolCardKind.Task:
            {
                var task = new List<ToolCardText>();
                AddFact(task, "agent", StringArg(value, "agent", "subagent", "name"));
                AddFact(task, "job", StringArg(value, "job_id", "jobId", "id"));
                AddFact(task, "description", StringArg(value, "description", "task"));
                var prompt = StringArg(value, "prompt", "message", "instructions");
                if (!string.IsNullOrEmpty(prompt))
                    task.Add(new("prompt · " + prompt));
                if (task.Count == 0) AddRawArguments(sections, args, value);
                else sections.Add(Bound("任务", string.Join('\n', task.Select(line => line.Text)),
                    maxLines: ArgumentLineLimit, maxChars: ArgumentCharLimit));
                break;
            }
            default:
                AddRawArguments(sections, args, value);
                break;
        }
    }

    private static void AddResultSections(List<ToolCardSection> sections, ToolCardKind kind,
        JsonElement? root, string? output, PlanFacts plan, CommandFacts command,
        ListFacts list, GrepFacts grep, GlobFacts glob)
    {
        if (output is null) return;
        if (kind == ToolCardKind.Plan && plan.HasItems) return;

        if (kind == ToolCardKind.Command && command.Structured)
        {
            if (!string.IsNullOrWhiteSpace(command.Prefix))
                sections.Add(Bound("状态", command.Prefix, maxLines: 16, maxChars: 3 * 1024));
            if (command.ExitLabel is not null)
                sections.Add(TextSection("ExitCode", [new(command.ExitLabel,
                    command.ExitCode == 0 ? Ink.DiffAdded : Ink.Error)]));
            AddCommandStream(sections, "STDOUT", command.Stdout);
            AddCommandStream(sections, "STDERR", command.Stderr, stderr: true);
            return;
        }

        var language = kind switch
        {
            ToolCardKind.Read => LanguageForPath(PathArg(root)),
            ToolCardKind.Edit => LooksLikeDiff(output) ? "diff" : null,
            ToolCardKind.Command => LooksLikeDiff(output) ? "diff" : null,
            _ => null
        };
        var title = kind switch
        {
            ToolCardKind.Read => "内容",
            ToolCardKind.List => "条目",
            ToolCardKind.Grep => "结果",
            ToolCardKind.Glob => "匹配",
            ToolCardKind.Write or ToolCardKind.Edit => "结果",
            ToolCardKind.Command => "输出",
            ToolCardKind.Task => "结果",
            ToolCardKind.WebSearch => "结果",
            ToolCardKind.Fetch => "内容",
            ToolCardKind.Plugin => "输出",
            _ => "输出"
        };
        var markdown = kind is ToolCardKind.Task or ToolCardKind.WebSearch;
        var lineInk = kind == ToolCardKind.Grep
            ? GrepInk
            : kind == ToolCardKind.List
                ? ListInk
                : kind == ToolCardKind.Glob
                    ? GlobInk
                    : (Func<string, Ink>?)null;
        if (output.Length == 0)
            sections.Add(TextSection(title, [new("无输出", Ink.Quiet)]));
        else
            sections.Add(Bound(title, output, language, markdown,
                OutputLineLimit, OutputCharLimit, lineInk));
    }

    private static void AddCommandStream(List<ToolCardSection> sections, string title, string? text, bool stderr = false)
    {
        if (text is null) return;
        if (text.Length == 0)
        {
            sections.Add(TextSection(title, [new("无输出", Ink.Quiet)]));
            return;
        }
        sections.Add(Bound(title, text, LooksLikeDiff(text) ? "diff" : null,
            maxLines: OutputLineLimit, maxChars: OutputCharLimit,
            lineInk: stderr ? StderrInk : LogInk));
    }

    private static void AddRawArguments(List<ToolCardSection> sections, string args, JsonElement? root)
    {
        if (string.IsNullOrWhiteSpace(args)) return;
        var title = root is null ? "参数（未完整）" : "参数";
        sections.Add(Bound(title, args, "json", maxLines: ArgumentLineLimit, maxChars: ArgumentCharLimit));
    }

    private static string? ReadTarget(ToolCardKind kind, JsonElement? root, PlanFacts plan, CommandFacts command) =>
        kind switch
        {
            ToolCardKind.Read or ToolCardKind.Write or ToolCardKind.Edit or ToolCardKind.List =>
                root is { } r ? PathArg(r) : null,
            ToolCardKind.Grep => root is { } r ? StringArg(r, "pattern", "query") : null,
            ToolCardKind.Glob => root is { } r ? StringArg(r, "pattern", "glob") : null,
            ToolCardKind.Command => command.Structured && !string.IsNullOrWhiteSpace(command.Prefix)
                ? null
                : root is { } r
                    ? FirstLine(StringArg(r, "command", "cmd", "script"))
                    : null,
            ToolCardKind.Plan => plan.Active?.Title,
            ToolCardKind.Task => root is { } r
                ? FirstLine(StringArg(r, "agent", "subagent", "name", "description", "task", "job_id", "jobId"))
                : null,
            ToolCardKind.WebSearch => root is { } r
                ? FirstLine(StringArg(r, "query", "q", "search_query"))
                : null,
            ToolCardKind.Fetch => root is { } r
                ? FirstLine(StringArg(r, "url", "uri", "path"))
                : null,
            ToolCardKind.Plugin or ToolCardKind.Unknown => root is { } r
                ? FirstLine(FirstMeaningfulString(r))
                : null,
            _ => null
        };

    private static IReadOnlyList<string> ReadStats(ToolCallSnapshot snapshot, ToolCardKind kind,
        JsonElement? root, string? output, PlanFacts plan, CommandFacts command,
        ListFacts list, GrepFacts grep, GlobFacts glob)
    {
        var stats = new List<string>();
        switch (kind)
        {
            case ToolCardKind.Read:
                if (output is not null) stats.Add($"内容 {LineCount(output)} 行");
                break;
            case ToolCardKind.Write:
                if (root is { } writeRoot && StringArg(writeRoot, "content", "text") is { } content)
                    stats.Add($"内容 {LineCount(content)} 行");
                break;
            case ToolCardKind.Edit:
                if (root is { } editRoot)
                {
                    if (TryArrayLength(editRoot, "edits", out var editCount)) stats.Add($"{editCount} 条编辑");
                    else if (StringArg(editRoot, "old_string") is not null) stats.Add("1 处编辑");
                }
                if (output is { } editOutput)
                {
                    var match = ReplacementCount.Match(editOutput);
                    if (match.Success) stats.Add($"替换 {match.Groups["count"].Value} 处");
                    var (added, removed) = DiffStats(editOutput);
                    if (added + removed > 0) stats.Add($"+{added} / -{removed}");
                }
                break;
            case ToolCardKind.List:
                if (list.Known) stats.Add($"目录 {list.Directories} · 文件 {list.Files}");
                if (list.Truncated) stats.Add("含截断标记");
                break;
            case ToolCardKind.Grep:
                if (grep.Known) stats.Add($"{grep.Matches} 处 · {grep.Files} 文件");
                if (grep.Truncated) stats.Add("含截断标记");
                break;
            case ToolCardKind.Glob:
                if (glob.Known) stats.Add($"{glob.Count} 个文件");
                if (glob.Truncated) stats.Add("含截断标记");
                break;
            case ToolCardKind.Command:
                if (command.ExitCode is { } exit) stats.Add($"退出码 {exit}");
                else if (command.ExitLabel is not null) stats.Add("退出 " + command.ExitLabel);
                if (command.Stdout is not null) stats.Add($"stdout {LineCount(command.Stdout)} 行");
                if (command.Stderr is not null) stats.Add($"stderr {LineCount(command.Stderr)} 行");
                break;
            case ToolCardKind.Plan:
                if (plan.HasItems) stats.Add($"计划 {plan.Completed}/{plan.Items.Count}");
                break;
            case ToolCardKind.Task:
            case ToolCardKind.WebSearch:
            case ToolCardKind.Fetch:
            case ToolCardKind.Plugin:
            case ToolCardKind.Unknown:
                if (output is not null) stats.Add($"输出 {LineCount(output)} 行");
                break;
        }

        if (snapshot.DurationMs is { } duration)
            stats.Add("耗时 " + FormatDuration(duration));
        return stats.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static PlanFacts ReadPlan(JsonElement? root, string? output)
    {
        if (!string.IsNullOrWhiteSpace(output))
        {
            var parsed = FuiPlanProjection.FromSnippet(output);
            if (parsed.HasItems) return new(parsed.Items, parsed.Notes);
        }

        if (root is not { } value || !value.TryGetProperty("steps", out var steps) ||
            steps.ValueKind != JsonValueKind.Array) return PlanFacts.Empty;
        var items = new List<FuiPlanItem>();
        var number = 0;
        foreach (var step in steps.EnumerateArray())
        {
            number++;
            var title = StringProperty(step, "title");
            var status = StringProperty(step, "status")?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(title) || status is not ("pending" or "in_progress" or "completed")) continue;
            items.Add(new(number, SafeSingle(title, "步骤"), status switch
            {
                "completed" => FuiPlanItemState.Completed,
                "in_progress" => FuiPlanItemState.InProgress,
                _ => FuiPlanItemState.Pending
            }));
        }
        var notes = StringProperty(value, "notes");
        return items.Count == 0 ? PlanFacts.Empty : new(items, notes);
    }

    private static CommandFacts ReadCommand(string? output)
    {
        if (string.IsNullOrEmpty(output)) return CommandFacts.Empty;
        var marker = output.IndexOf("ExitCode:", StringComparison.Ordinal);
        if (marker < 0 || marker > 0 && output[marker - 1] != '\n') return CommandFacts.Empty;
        var codeStart = marker + "ExitCode:".Length;
        var codeEnd = output.IndexOf('\n', codeStart);
        if (codeEnd < 0) return CommandFacts.Empty;
        var codeText = output[codeStart..codeEnd].Trim();
        const string stdoutHeader = "\nSTDOUT:\n";
        if (!output.AsSpan(codeEnd).StartsWith(stdoutHeader, StringComparison.Ordinal)) return CommandFacts.Empty;
        var stdoutStart = codeEnd + stdoutHeader.Length;
        var stderrHead = output.IndexOf("\nSTDERR:\n", stdoutStart, StringComparison.Ordinal);
        if (stderrHead < 0) return CommandFacts.Empty;
        var stderrStart = stderrHead + "\nSTDERR:\n".Length;
        var stdout = output[stdoutStart..stderrHead];
        var stderr = output[stderrStart..];
        int? exit = int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
            ? code
            : null;
        var label = exit is { } known ? known.ToString(CultureInfo.InvariantCulture) : codeText;
        var prefix = output[..marker].Trim();
        return new(true, exit, label.Length == 0 ? "未知" : label, stdout, stderr, prefix);
    }

    private static ListFacts ReadList(string? output)
    {
        if (string.IsNullOrEmpty(output)) return ListFacts.Empty;
        var lines = output.Split('\n');
        var dirs = lines.Count(line => line.StartsWith("[DIR]", StringComparison.Ordinal));
        var files = lines.Count(line => line.StartsWith("[FILE]", StringComparison.Ordinal));
        if (dirs + files == 0) return ListFacts.Empty;
        return new(true, dirs, files, lines.Any(line => line.Contains("已截断", StringComparison.Ordinal)));
    }

    private static GrepFacts ReadGrep(string? output)
    {
        if (string.IsNullOrEmpty(output)) return GrepFacts.Empty;
        var files = 0;
        var matches = 0;
        foreach (var line in output.Split('\n'))
        {
            if (!line.StartsWith("## ", StringComparison.Ordinal)) continue;
            files++;
            var open = line.LastIndexOf(" (", StringComparison.Ordinal);
            var close = line.LastIndexOf(" 处命中)", StringComparison.Ordinal);
            if (open < 0 || close <= open) continue;
            if (int.TryParse(line[(open + 2)..close], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                matches += count;
        }
        if (files == 0 && !output.Contains("未找到匹配项", StringComparison.Ordinal)) return GrepFacts.Empty;
        var truncated = output.Contains("上限", StringComparison.Ordinal) ||
                        output.Contains("已截断", StringComparison.Ordinal) ||
                        output.Contains("未参与检索", StringComparison.Ordinal);
        return new(true, files, matches, truncated);
    }

    private static GlobFacts ReadGlob(string? output)
    {
        if (string.IsNullOrEmpty(output)) return GlobFacts.Empty;
        if (output.Contains("未找到匹配文件", StringComparison.Ordinal)) return new(true, 0, false);
        var count = output.Split('\n').Count(line =>
            line.Length > 0 && !line.StartsWith('[') && !line.Contains("已截断", StringComparison.Ordinal));
        return count == 0 ? GlobFacts.Empty : new(true, count,
            output.Contains("截断", StringComparison.Ordinal));
    }

    private static string EditArgumentPreview(JsonElement root)
    {
        var builder = new StringBuilder();
        if (root.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var edit in edits.EnumerateArray())
            {
                if (index++ >= 8) break;
                AppendDiff(builder, "edits[" + (index - 1).ToString(CultureInfo.InvariantCulture) + "]",
                    StringProperty(edit, "old_string"), StringProperty(edit, "new_string"));
            }
            return builder.ToString().TrimEnd();
        }

        AppendDiff(builder, "edit", StringProperty(root, "old_string"), StringProperty(root, "new_string"));
        return builder.ToString().TrimEnd();
    }

    private static void AppendDiff(StringBuilder builder, string title, string? oldText, string? newText)
    {
        if (oldText is null && newText is null) return;
        if (builder.Length > 0) builder.AppendLine();
        builder.Append("@@ ").Append(title).AppendLine(" @@");
        if (oldText is not null)
            foreach (var line in oldText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                builder.Append("- ").AppendLine(line);
        if (newText is not null)
            foreach (var line in newText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                builder.Append("+ ").AppendLine(line);
    }

    private static ToolCardSection Bound(string title, string text, string? language = null,
        bool markdown = false, int maxLines = 40, int maxChars = 8 * 1024,
        Func<string, Ink>? lineInk = null)
    {
        text = TerminalText.Clean(text).TrimEnd('\n');
        if (text.Length == 0)
            return new(title, [new("", lineInk?.Invoke("") ?? Ink.Tool)], markdown);
        var source = text.Split('\n');
        var totalChars = text.Length;
        if (source.Length <= maxLines && totalChars <= maxChars)
            return new(title, source.Select(line => Line(line, language, lineInk)).ToArray(), markdown);

        var headLines = new List<string>();
        var tailLines = new List<string>();
        var headLimit = Math.Max(1, maxLines * 2 / 3);
        var tailLimit = Math.Max(0, maxLines - headLimit);
        var headBudget = Math.Max(1, maxChars * 2 / 3);
        var tailBudget = Math.Max(0, maxChars - headBudget);
        var headChars = 0;
        var tailChars = 0;
        var clipped = false;

        for (var i = 0; i < source.Length && headLines.Count < headLimit; i++)
        {
            var line = source[i];
            var cost = line.Length + 1;
            if (headChars + cost <= headBudget)
            {
                headLines.Add(line);
                headChars += cost;
                continue;
            }
            if (headLines.Count == 0)
            {
                headLines.Add(TerminalText.Clip(line, Math.Max(1, headBudget - 1)));
                clipped = true;
            }
            break;
        }

        for (var i = source.Length - 1; i >= 0 && tailLines.Count < tailLimit; i--)
        {
            if (i < headLines.Count)
            {
                clipped = true;
                break;
            }
            var line = source[i];
            var cost = line.Length + 1;
            if (tailChars + cost <= tailBudget)
            {
                tailLines.Insert(0, line);
                tailChars += cost;
                continue;
            }
            if (tailLines.Count == 0)
            {
                tailLines.Add(TerminalText.Clip(line, Math.Max(1, tailBudget - 1), tail: true));
                clipped = true;
            }
            break;
        }

        if (headLines.Count + tailLines.Count >= source.Length && !clipped &&
            headChars + tailChars >= totalChars)
        {
            return new(title, source.Select(line => Line(line, language, lineInk)).ToArray(), markdown);
        }

        var lines = new List<ToolCardText>(headLines.Count + tailLines.Count + 1);
        lines.AddRange(headLines.Select(line => Line(line, language, lineInk)));
        lines.Add(new($"… [已截断：共 {source.Length} 行 / {totalChars} 字符，仅显示头尾摘录]", Ink.DiffMeta));
        lines.AddRange(tailLines.Select(line => Line(line, language, lineInk)));
        return new(title, lines, markdown, true);
    }

    private static ToolCardText Line(string text, string? language, Func<string, Ink>? lineInk) =>
        new(text, lineInk?.Invoke(text) ?? Ink.Tool, language);

    private static ToolCardSection TextSection(string title, IReadOnlyList<ToolCardText> lines) => new(title, lines);

    private static void AddTextSection(List<ToolCardSection> sections, string title, IReadOnlyList<ToolCardText> lines)
    {
        if (lines.Count > 0) sections.Add(TextSection(title, lines));
    }

    private static void AddFact(List<ToolCardText> lines, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) lines.Add(new($"{name} · {SafeSingle(value, "")}"));
    }

    private static void AddBoolFact(List<ToolCardText> lines, string name, JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            lines.Add(new($"{name} · {(value.GetBoolean() ? "true" : "false")}"));
    }

    private static void AddNumberFact(List<ToolCardText> lines, string name, JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number))
            lines.Add(new($"{name} · {number.ToString(CultureInfo.InvariantCulture)}"));
    }

    private static JsonDocument? ParseJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringArg(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    private static string? PathArg(JsonElement? root) =>
        root is { } value ? StringArg(value, "path", "file_path", "filePath", "file", "filename") : null;

    private static string? FirstMeaningfulString(JsonElement root)
    {
        foreach (var name in new[]
                 {
                     "path", "url", "command", "query", "pattern", "name", "job_id", "jobId", "action",
                     "task", "description", "message"
                 })
        {
            if (StringArg(root, name) is { } value) return value;
        }
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString() is { Length: > 0 } value) return value;
        return null;
    }

    private static string? StringProperty(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryArrayLength(JsonElement root, string name, out int count)
    {
        count = 0;
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return false;
        count = value.GetArrayLength();
        return true;
    }

    private static string Verb(ToolCardKind kind, string name) => kind switch
    {
        ToolCardKind.Read => "读取",
        ToolCardKind.Write => "写入",
        ToolCardKind.Edit => "编辑",
        ToolCardKind.List => "浏览目录",
        ToolCardKind.Grep => "搜索",
        ToolCardKind.Glob => "查找文件",
        ToolCardKind.Command => "执行命令",
        ToolCardKind.Plan => "更新计划",
        ToolCardKind.Task => "子代理",
        ToolCardKind.Plugin => "插件工具",
        ToolCardKind.WebSearch => "网页搜索",
        ToolCardKind.Fetch => "抓取网页",
        _ => name
    };

    private static string StatusLabel(ToolCallStatus? status) => status switch
    {
        ToolCallStatus.Pending => "待执行",
        ToolCallStatus.Running => "执行中",
        ToolCallStatus.Completed => "完成",
        ToolCallStatus.Failed => "失败",
        ToolCallStatus.Cancelled => "已取消",
        _ => "状态未提供"
    };

    private static Ink PlanInk(FuiPlanItemState state) => state switch
    {
        FuiPlanItemState.Completed => Ink.DiffAdded,
        FuiPlanItemState.InProgress => Ink.SyntaxKeyword,
        _ => Ink.Quiet
    };

    private static Ink GrepInk(string line)
    {
        if (line.StartsWith("## ", StringComparison.Ordinal)) return Ink.Heading;
        if (line.StartsWith('[')) return Ink.Quiet;
        var colon = line.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && int.TryParse(line[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? Ink.Code
            : Ink.Tool;
    }

    private static Ink ListInk(string line) => line.StartsWith("[DIR]", StringComparison.Ordinal)
        ? Ink.SyntaxType
        : line.StartsWith("[FILE]", StringComparison.Ordinal) ? Ink.Code : Ink.Quiet;

    private static Ink GlobInk(string line) => line.StartsWith('[') ? Ink.Quiet : Ink.Code;

    private static Ink LogInk(string line) => ContainsWord(line, "error", "fail", "fatal", "panic")
        ? Ink.Error
        : ContainsWord(line, "warn") ? Ink.SyntaxKeyword : Ink.Tool;

    private static Ink StderrInk(string line) => ContainsWord(line, "warn") ? Ink.SyntaxKeyword : Ink.Error;

    private static bool ContainsWord(string line, params string[] words) =>
        words.Any(word => line.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static string SafeSingle(string? value, string fallback)
    {
        var clean = TerminalText.SingleLine(value ?? "").Trim();
        return clean.Length == 0 ? fallback : clean;
    }

    private static string? FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = TerminalText.Clean(value).Trim();
        if (clean.Length == 0) return null;
        var newline = clean.IndexOf('\n');
        return TerminalText.SingleLine(newline < 0 ? clean : clean[..newline]).Trim();
    }

    private static bool ContainsStatus(string? output, string status) =>
        output is not null && output.Contains(status, StringComparison.Ordinal);

    private static int LineCount(string text)
    {
        if (text.Length == 0) return 0;
        var lines = 1;
        foreach (var ch in text)
            if (ch == '\n') lines++;
        return text[^1] == '\n' ? lines - 1 : lines;
    }

    private static (int Added, int Removed) DiffStats(string text)
    {
        var added = 0;
        var removed = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("+ ", StringComparison.Ordinal)) added++;
            else if (line.StartsWith("- ", StringComparison.Ordinal)) removed++;
        }
        return (added, removed);
    }

    private static bool LooksLikeDiff(string text) =>
        text.Contains("\n@@ ", StringComparison.Ordinal) ||
        text.StartsWith("@@ ", StringComparison.Ordinal) ||
        text.Contains("\n+ ", StringComparison.Ordinal) && text.Contains("\n- ", StringComparison.Ordinal);

    private static string? LanguageForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var language = SyntaxHighlighter.NormalizeLanguage(Path.GetExtension(path));
        return language switch
        {
            "csharp" or "javascript" or "typescript" or "json" or "python" or "bash" or "powershell" or
                "rust" or "go" or "sql" or "diff" => language,
            _ => null
        };
    }

    private static string LanguageForShell(string? shell)
    {
        var value = shell?.Trim().ToLowerInvariant();
        return value switch
        {
            "cmd" or "cmd.exe" => "",
            "bash" or "sh" or "git-bash" or "gitbash" => "bash",
            "powershell" or "pwsh" or "powershell.exe" or "pwsh.exe" or null or "" => "powershell",
            _ => ""
        };
    }

    private static string FormatDuration(long milliseconds)
    {
        milliseconds = Math.Max(0, milliseconds);
        return milliseconds switch
        {
            < 1_000 => milliseconds.ToString(CultureInfo.InvariantCulture) + " ms",
            < 60_000 => (milliseconds / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + " s",
            _ => (milliseconds / 60_000).ToString(CultureInfo.InvariantCulture) + " min " +
                 (milliseconds % 60_000 / 1000).ToString(CultureInfo.InvariantCulture) + " s"
        };
    }

    private sealed record PlanFacts(IReadOnlyList<FuiPlanItem> Items, string? Notes)
    {
        internal static PlanFacts Empty { get; } = new([], null);
        internal bool HasItems => Items.Count > 0;
        internal int Completed => Items.Count(item => item.State == FuiPlanItemState.Completed);
        internal FuiPlanItem? Active => Items.FirstOrDefault(item => item.State == FuiPlanItemState.InProgress) ??
            Items.FirstOrDefault(item => item.State == FuiPlanItemState.Pending);
    }

    private sealed record CommandFacts(bool Structured, int? ExitCode, string? ExitLabel,
        string? Stdout, string? Stderr, string? Prefix)
    {
        internal static CommandFacts Empty { get; } = new(false, null, null, null, null, null);
    }

    private sealed record ListFacts(bool Known, int Directories, int Files, bool Truncated)
    {
        internal static ListFacts Empty { get; } = new(false, 0, 0, false);
    }

    private sealed record GrepFacts(bool Known, int Files, int Matches, bool Truncated)
    {
        internal static GrepFacts Empty { get; } = new(false, 0, 0, false);
    }

    private sealed record GlobFacts(bool Known, int Count, bool Truncated)
    {
        internal static GlobFacts Empty { get; } = new(false, 0, false);
    }
}
