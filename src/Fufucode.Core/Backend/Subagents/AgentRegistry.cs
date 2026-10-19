using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Fufucode.Core.Backend;

/// <summary>子代理定义：名称、用途、工具白名单、轮次上限与系统提示。</summary>
public sealed record AgentDefinition(
    string Name,
    string Description,
    IReadOnlyList<string> Tools,
    string SystemPrompt,
    int MaxTurns = 15,
    string? ModelId = null);

/// <summary>
/// 轻量子代理注册表：内置 explore/general，并发现项目与用户级 Markdown 定义。
/// 与 Claude/OMP 的 `agents/*.md` 约定兼容（frontmatter: name/description/tools/maxTurns/model）。
/// </summary>
public sealed class AgentRegistry
{
    private readonly Dictionary<string, AgentDefinition> _agents;

    private AgentRegistry(Dictionary<string, AgentDefinition> agents) => _agents = agents;

    public IReadOnlyList<AgentDefinition> All =>
        _agents.Values.OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public bool TryGet(string name, out AgentDefinition agent) => _agents.TryGetValue(name, out agent!);

    /// <summary>默认子代理：general。</summary>
    public AgentDefinition Default =>
        _agents.TryGetValue("general", out var general) ? general : All[0];

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

        // roots 顺序是"项目 → 用户"；倒序加载，让项目定义最后写入并覆盖用户定义。
        foreach (var root in roots.AsEnumerable().Reverse())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.md").OrderBy(path => path, StringComparer.Ordinal); }
            catch { continue; }
            foreach (var file in files)
            {
                var agent = TryLoad(file);
                // 项目/用户定义优先于内置（后发现的覆盖先发现的，roots 顺序已保证项目在前）。
                if (agent is not null) agents[agent.Name] = agent;
            }
        }

        return new AgentRegistry(agents);
    }

    private static IEnumerable<AgentDefinition> Builtins()
    {
        var readOnly = new[] { "read_file", "list_dir", "grep_search", "glob_files" };
        var all = new[] { "read_file", "write_file", "edit_file", "list_dir", "grep_search", "glob_files", "run_command" };

        yield return new AgentDefinition(
            "explore",
            "只读探索：搜索代码、定位文件与事实，不修改任何内容。",
            readOnly,
            "你是只读探索子代理。使用读取、目录列表、正则搜索与通配查找收集事实，" +
            "不要修改文件或执行命令。返回简洁、可核验的结论与文件位置。",
            MaxTurns: 15);

        yield return new AgentDefinition(
            "general",
            "通用子代理：可使用全部内置工具完成一个边界明确的任务。",
            all,
            "你是一个独立子代理，只负责交给你的这一项任务。先调查再行动，" +
            "完成后用简洁文本返回结果、涉及的文件与任何未完成事项。",
            MaxTurns: 20);
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
            var maxTurns = int.TryParse(Read(frontmatter, "maxTurns"), out var turns)
                ? Math.Clamp(turns, 1, 50)
                : 15;

            return new AgentDefinition(name, description, tools,
                string.IsNullOrWhiteSpace(body) ? description : body.Trim(), maxTurns,
                Read(frontmatter, "model"));
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
        builder.AppendLine("需要隔离上下文时调用 subagent 工具，传入 agent 与 prompt。");
        foreach (var agent in All)
            builder.Append("- ").Append(agent.Name).Append("：").AppendLine(agent.Description);
        return builder.ToString().TrimEnd();
    }
}
