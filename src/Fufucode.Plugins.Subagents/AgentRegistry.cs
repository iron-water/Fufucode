using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Fufucode.Core.Backend;

/// <summary>子代理运行模式：read 只读快速探索；patch 在临时 worktree 中修改并返回补丁。</summary>
public enum SubagentMode
{
    Read,
    Patch,
}

/// <summary>子代理定义：名称、模式、工具白名单、可选外部预算、输出契约与系统提示。</summary>
public sealed record AgentDefinition(
    string Name,
    string Description,
    IReadOnlyList<string> Tools,
    string SystemPrompt,
    int? MaxTurns = null,
    string? ModelId = null,
    string? Effort = null,
    string? OutputSchema = null,
    bool Fork = false,
    SubagentMode Mode = SubagentMode.Read,
    int? TimeoutSeconds = null);

/// <summary>
/// 轻量子代理注册表：内置 explore（read）与 general（patch），并发现项目/用户级 Markdown 定义。
/// </summary>
public sealed class AgentRegistry
{
    private readonly Dictionary<string, AgentDefinition> _agents;

    private AgentRegistry(Dictionary<string, AgentDefinition> agents) => _agents = agents;

    public IReadOnlyList<AgentDefinition> All =>
        _agents.Values.OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public bool TryGet(string name, out AgentDefinition agent) => _agents.TryGetValue(name, out agent!);

    /// <summary>默认子代理：explore（只读、最快）。</summary>
    public AgentDefinition Default =>
        _agents.TryGetValue("explore", out var explore) ? explore : All[0];

    public static AgentRegistry Discover(string? workspaceRoot, string? userHome = null)
    {
        var agents = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var builtin in Builtins()) agents[builtin.Name] = builtin;

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            roots.Add(Path.Combine(workspaceRoot, ".fumo", "agents"));
            roots.Add(Path.Combine(workspaceRoot, ".agents", "agents"));
            roots.Add(Path.Combine(workspaceRoot, ".claude", "agents"));
        }
        userHome ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots.Add(Path.Combine(userHome, ".fumo", "agents"));
        roots.Add(Path.Combine(userHome, ".agents", "agents"));
        roots.Add(Path.Combine(userHome, ".claude", "agents"));

        // 倒序加载：用户先写、项目最后写，保证项目定义覆盖用户与内置。
        foreach (var root in roots.AsEnumerable().Reverse())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.md").OrderBy(path => path, StringComparer.Ordinal); }
            catch { continue; }
            foreach (var file in files)
            {
                var agent = TryLoad(file);
                if (agent is not null) agents[agent.Name] = agent;
            }
        }

        return new AgentRegistry(agents);
    }

    private static IEnumerable<AgentDefinition> Builtins()
    {
        var readOnly = new[] { "read_file", "list_dir", "grep_search", "glob_files" };
        var patchTools = new[]
        {
            "read_file", "write_file", "edit_file", "list_dir", "grep_search", "glob_files", "run_command"
        };

        yield return new AgentDefinition(
            "explore",
            "只读探索：搜索代码、定位文件与事实，不修改任何内容。",
            readOnly,
            "你是只读探索子代理。使用读取、目录列表、正则搜索与通配查找收集事实，" +
            "不要尝试修改文件或执行命令。返回简洁结论，并给出文件:行号证据。",
            Mode: SubagentMode.Read);

        yield return new AgentDefinition(
            "general",
            "通用修改代理：在隔离 worktree 中修改代码并返回补丁。",
            patchTools,
            "你是一个隔离修改子代理。只处理交给你的任务：先阅读相关代码，再做最小必要修改；" +
            "可以运行验证命令。完成后用简洁文本说明改动、验证结果与未完成项。",
            Mode: SubagentMode.Patch);
    }

    private static AgentDefinition? TryLoad(string file)
    {
        try
        {
            var raw = File.ReadAllText(file);
            var (frontmatter, body) = SplitFrontmatter(raw);
            var name = Read(frontmatter, "name") ?? Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(name)) return null;

            var description = Read(frontmatter, "description")
                              ?? body.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim()
                              ?? string.Empty;
            var tools = ParseList(Read(frontmatter, "tools"))
                        ?? new[] { "read_file", "list_dir", "grep_search", "glob_files" };
            // 未声明 maxTurns 表示不设置轮次预算；显式值才作为自动化/实验预算。
            var maxTurns = int.TryParse(Read(frontmatter, "maxTurns"), out var turns) && turns > 0
                ? turns
                : (int?)null;

            var mode = string.Equals(Read(frontmatter, "mode"), "patch", StringComparison.OrdinalIgnoreCase)
                ? SubagentMode.Patch
                : SubagentMode.Read;
            var timeout = int.TryParse(Read(frontmatter, "timeoutSeconds"), out var seconds)
                ? Math.Clamp(seconds, 10, 600)
                : (int?)null;

            return new AgentDefinition(
                name, description, tools,
                string.IsNullOrWhiteSpace(body) ? description : body.Trim(),
                maxTurns,
                Read(frontmatter, "model"),
                Read(frontmatter, "effort"),
                Read(frontmatter, "outputSchema"),
                bool.TryParse(Read(frontmatter, "fork"), out var fork) && fork,
                mode,
                timeout);
        }
        catch
        {
            return null;
        }
    }

    private static (Dictionary<string, string> Frontmatter, string Body) SplitFrontmatter(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!text.StartsWith("---", StringComparison.Ordinal)) return (map, text);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var end = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (!string.Equals(lines[i].Trim(), "---", StringComparison.Ordinal)) continue;
            end = i;
            break;
        }
        if (end < 0) return (map, text);

        for (var i = 1; i < end; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            map[line[..colon].Trim()] = line[(colon + 1)..].Trim().Trim('"', '\'');
        }
        return (map, string.Join("\n", lines.Skip(end + 1)));
    }

    private static IReadOnlyList<string>? ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Trim().TrimStart('[').TrimEnd(']');
        var items = cleaned.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim().Trim('"', '\''))
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return items.Length == 0 ? null : items;
    }

    private static string? Read(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    /// <summary>系统提示词中的紧凑子代理目录。</summary>
    public string BuildPromptSection()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 可用子代理");
        builder.AppendLine("需要隔离上下文时调用 subagent 工具；read 只读探索，patch 在隔离工作树中修改并返回补丁。");
        // 何时该派发：写清判断依据，避免模型对所有任务都派发（增加成本）或从不派发（插件形同虚设）。
        builder.AppendLine("适合派发：大范围搜索/定位、需要阅读大量文件才能回答、可拆成多个互不依赖的子问题、审查独立模块。");
        builder.AppendLine("不要派发：单文件小改、已知位置的精确读取、需要保留完整上下文的连续修改——直接调用内置工具更快。");
        builder.AppendLine("派发时用 context 传入已确认的文件路径、符号名或报错原文，避免子代理重复发现。");
        foreach (var agent in All)
            builder.Append("- ").Append(agent.Name).Append("（")
                .Append(agent.Mode == SubagentMode.Patch ? "patch" : "read").Append("）：")
                .AppendLine(agent.Description);
        return builder.ToString().TrimEnd();
    }
}
