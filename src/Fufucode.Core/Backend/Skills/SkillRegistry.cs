using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>一个已发现的技能：SKILL.md 元数据、完整说明与所在目录。</summary>
public sealed record SkillDefinition(
    string Name,
    string Description,
    string Directory,
    string Instructions,
    bool DisableModelInvocation);

/// <summary>
/// 轻量技能注册表：发现 SKILL.md，向系统提示词提供紧凑目录，
/// 并通过 <c>skill</c> 工具按需返回完整说明。技能里可以指示模型调用 CLI 完成实际工作。
/// </summary>
public sealed class SkillRegistry : IPureToolDispatcher
{
    public const string ToolName = "skill";

    private readonly Dictionary<string, SkillDefinition> _skills;

    private SkillRegistry(Dictionary<string, SkillDefinition> skills) => _skills = skills;

    public IReadOnlyList<SkillDefinition> All =>
        _skills.Values.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>允许模型主动加载的技能（`disable-model-invocation` 的技能只能由用户显式调用）。</summary>
    public IReadOnlyList<SkillDefinition> ModelInvocable =>
        All.Where(skill => !skill.DisableModelInvocation).ToArray();

    public bool HasModelInvocableSkills => _skills.Values.Any(skill => !skill.DisableModelInvocation);

    public bool TryGet(string name, out SkillDefinition skill) => _skills.TryGetValue(name, out skill!);

    /// <summary>
    /// 按优先级发现技能：项目 → 跨内核 `.agents` → Claude 兼容 → 用户级。
    /// 同名技能由先出现的目录覆盖。
    /// </summary>
    public static SkillRegistry Discover(string? workspaceRoot, string? userHome = null)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var project = Path.GetFullPath(workspaceRoot);
            roots.Add(Path.Combine(project, ".fumo", "skills"));
            roots.Add(Path.Combine(project, ".agents", "skills"));
            roots.Add(Path.Combine(project, ".claude", "skills"));
        }

        userHome ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots.Add(Path.Combine(userHome, ".fumo", "skills"));
        roots.Add(Path.Combine(userHome, ".agents", "skills"));
        roots.Add(Path.Combine(userHome, ".claude", "skills"));

        var skills = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.Ordinal); }
            catch { continue; }

            foreach (var directory in directories)
            {
                var skill = TryLoad(directory);
                if (skill is not null && !skills.ContainsKey(skill.Name)) skills[skill.Name] = skill;
            }
        }

        return new SkillRegistry(skills);
    }

    private static SkillDefinition? TryLoad(string directory)
    {
        try
        {
            var name = Path.GetFileName(directory);
            var file = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(file)) file = Path.Combine(directory, name + ".md");
            if (!File.Exists(file)) return null;

            var raw = File.ReadAllText(file);
            var (frontmatter, body) = SplitFrontmatter(raw);
            var skillName = Read(frontmatter, "name") ?? name;
            var description = Read(frontmatter, "description")
                              ?? body.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim()
                              ?? string.Empty;
            var disable = string.Equals(Read(frontmatter, "disable-model-invocation")
                                        ?? Read(frontmatter, "disable_model_invocation"),
                "true", StringComparison.OrdinalIgnoreCase);

            if (skillName.Length == 0) return null;
            return new SkillDefinition(skillName, description, Path.GetFullPath(directory), body.Trim(), disable);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>拆分 YAML frontmatter；不引入 YAML 依赖，只读取顶层 key: value。</summary>
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
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');
            map[key] = value;
        }

        return (map, string.Join("\n", lines.Skip(end + 1)));
    }

    private static string? Read(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    /// <summary>系统提示词中的紧凑技能目录（只有 name 与 description，不加载正文）。</summary>
    public string BuildPromptSection()
    {
        var skills = ModelInvocable;
        if (skills.Count == 0) return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine("# 可用技能（Skills）");
        builder.AppendLine("需要时先调用 skill 工具读取完整说明，再执行其中描述的 CLI/API 步骤。");
        foreach (var skill in skills)
            builder.Append("- ").Append(skill.Name).Append("：").AppendLine(skill.Description);
        return builder.ToString().TrimEnd();
    }

    /// <summary>`skill` 工具：返回技能正文与基目录；缺省参数时列出可加载技能。</summary>
    public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal))
            return Task.FromResult(new PureToolResult(call.Id, false, $"未知工具：'{call.Name}'。"));

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
            var name = document.RootElement.TryGetProperty("name", out var nameElement) &&
                       nameElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? nameElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
            {
                var listing = string.Join("\n", ModelInvocable.Select(skill => $"- {skill.Name}: {skill.Description}"));
                return Task.FromResult(new PureToolResult(call.Id, true,
                    ModelInvocable.Count == 0 ? "没有可加载的技能。" : "可用技能：\n" + listing));
            }

            if (!TryGet(name, out var skill))
                return Task.FromResult(new PureToolResult(call.Id, false,
                    $"未找到技能 '{name}'。可用：{string.Join(", ", ModelInvocable.Select(s => s.Name))}。"));
            if (skill.DisableModelInvocation)
                return Task.FromResult(new PureToolResult(call.Id, false,
                    $"技能 '{skill.Name}' 禁止模型自动加载；请由用户使用 /skill:{skill.Name} 调用。"));

            return Task.FromResult(new PureToolResult(call.Id, true, Render(skill)));
        }
        catch (Exception error)
        {
            return Task.FromResult(new PureToolResult(call.Id, false, $"读取技能失败：{error.Message}"));
        }
    }

    /// <summary>用户显式 `/skill:name` 调用时的提示词正文（允许 disable-model-invocation 的技能）。</summary>
    public string? ExpandUserInvocation(string text)
    {
        const string prefix = "/skill:";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = text[prefix.Length..];
        var space = rest.IndexOf(' ');
        var name = (space < 0 ? rest : rest[..space]).Trim();
        var arguments = space < 0 ? string.Empty : rest[(space + 1)..].Trim();
        if (name.Length == 0 || !TryGet(name, out var skill)) return null;

        var builder = new StringBuilder();
        builder.AppendLine(Render(skill));
        if (arguments.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("用户参数：");
            builder.AppendLine(arguments);
        }
        return builder.ToString().TrimEnd();
    }

    private static string Render(SkillDefinition skill)
    {
        var builder = new StringBuilder();
        builder.Append("[技能] ").AppendLine(skill.Name);
        builder.Append("Base directory: ").AppendLine(skill.Directory);
        builder.AppendLine();
        builder.AppendLine(skill.Instructions);

        var files = ListEntries(skill.Directory);
        if (files.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("技能目录内的文件：");
            foreach (var file in files) builder.AppendLine("- " + file);
        }
        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<string> ListEntries(string directory)
    {
        try
        {
            return Directory.GetFileSystemEntries(directory)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Take(50)
                .Select(path =>
                {
                    var name = Path.GetFileName(path);
                    return Directory.Exists(path) ? name + "/" : name;
                })
                .Where(name => !string.Equals(name, "SKILL.md", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>把 `skill` 工具接到现有调度器前面；其余工具原样透传。</summary>
public sealed class SkillAwareDispatcher(IPureToolDispatcher inner, SkillRegistry skills) : IPureToolDispatcher
{
    public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default) =>
        string.Equals(call.Name, SkillRegistry.ToolName, StringComparison.Ordinal)
            ? skills.DispatchAsync(call, cancellationToken)
            : inner.DispatchAsync(call, cancellationToken);
}
