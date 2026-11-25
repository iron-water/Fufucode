using System.Text;
using System.Text.Json;
using Fufucode.Core.Backend;
using Fufucode.Core.Memory;
using Fufucode.Core.Plugins;
using Fufucode.Core.Security;

namespace Fufucode.Plugins.Memory;

/// <summary>
/// 记忆插件：激活期间把 <c>memory/MEMORY.md</c> 注入每轮 FUMO 会话，并提供保存与删除工具。
/// 记忆文件本身由 Core 的 <see cref="MemoryNotebook"/> 定义，卸载插件不删除文件。
/// </summary>
public sealed class MemoryPluginPackage : IPluginPackage
{
    private IDisposable? _prompt;

    public string Id => MemoryNotebook.PluginId;
    public string Version => "1.0.0";

    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.DataRoot)) return Task.CompletedTask;
        var notebook = new MemoryNotebook(context.DataRoot);
        _prompt = PluginPromptRegistry.Register(Id, notebook.BuildPromptSection);
        return Task.CompletedTask;
    }

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) =>
        string.IsNullOrWhiteSpace(context.DataRoot) ? null : new MemoryToolHandler(new MemoryNotebook(context.DataRoot));

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _prompt?.Dispose();
        _prompt = null;
        return Task.CompletedTask;
    }
}

public sealed class MemoryToolHandler(MemoryNotebook notebook) : IPluginToolHandler
{
    private const string SaveTool = "memory_save";
    private const string ForgetTool = "memory_forget";

    private static readonly PluginToolRegistration[] Tools =
    [
        new(SaveTool,
            "记住一条跨会话、跨项目都成立的信息：用户明确要你记住的事、用户陈述的偏好或规则、用户对你的更正。" +
            "只记用户的原意，不泛化成更宽的规则；不要记代码、文件或环境里能直接查到的事实，不要记任务进度或密钥。一条只写一件事。",
            "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"一条自成一体的记忆，最多 500 字\"}},\"required\":[\"text\"]}",
            PluginCapability.Memory),
        new(ForgetTool,
            "删除一条记忆：用户说它错了、过时了或不再需要时使用。text 为原文或能唯一定位它的片段；匹配多条时不删除并列出候选。",
            "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"记忆原文或唯一片段\"}},\"required\":[\"text\"]}",
            PluginCapability.Memory),
    ];

    public IReadOnlyList<PluginToolRegistration> Registrations => Tools;
    public PluginCapability? Capability => PluginCapability.Memory;

    public Task<PureToolResult> ExecuteAsync(PureToolCall call, CancellationToken cancellationToken = default) =>
        Task.FromResult(Execute(call));

    private PureToolResult Execute(PureToolCall call)
    {
        string text;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            text = document.RootElement.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException error)
        {
            return Fail(call, "参数 JSON 无效：" + error.Message);
        }
        if (string.IsNullOrWhiteSpace(text)) return Fail(call, "缺少 text。");

        try
        {
            return call.Name switch
            {
                SaveTool => Save(call, text),
                ForgetTool => Forget(call, text),
                _ => Fail(call, $"未知记忆工具 '{call.Name}'。"),
            };
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Fail(call, error.Message);
        }
    }

    private PureToolResult Save(PureToolCall call, string text)
    {
        if (SecurityContentScanner.ContainsSecret(text, new SecuritySettings()))
            return Fail(call, "内容像是密钥或令牌，记忆不保存敏感信息。");
        var before = notebook.Read().Count;
        var entry = notebook.Add(text);
        var added = notebook.Read().Count > before;
        return new PureToolResult(call.Id, true, (added ? "已记住：" : "已存在相同记忆：") + entry.Text);
    }

    private PureToolResult Forget(PureToolCall call, string text)
    {
        var result = notebook.Forget(text);
        if (result.Removed.Count > 0)
            return new PureToolResult(call.Id, true, "已删除：" + string.Join("；", result.Removed.Select(entry => entry.Text)));
        if (result.Candidates.Count == 0) return Fail(call, "没有匹配的记忆。");
        var builder = new StringBuilder("匹配到多条，未删除。请用原文再试：");
        foreach (var candidate in result.Candidates) builder.AppendLine().Append("- ").Append(candidate.Text);
        return Fail(call, builder.ToString());
    }

    private static PureToolResult Fail(PureToolCall call, string message) => new(call.Id, false, message);
}
