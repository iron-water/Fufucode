using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Fufucode.Core.Diagnostics;

namespace Fufucode.Core.Backend;

/// <summary>
/// `mcp` 网关工具：默认只暴露一个工具，按需查看服务器/工具 Schema 或发起调用，
/// 避免把服务器上的全部工具一次性塞进模型上下文。
/// </summary>
public sealed class McpGatewayDispatcher(IPureToolDispatcher inner, McpManager manager) : IPureToolDispatcher
{
    public const string ToolName = "mcp";

    public IPureToolDispatcher Inner => inner;

    public static PureToolDefinition Definition { get; } = new(
        ToolName,
        "访问已配置的 MCP 服务器：action=list_servers 查看服务器；action=list_tools 查看某服务器的工具与参数 Schema；action=call 调用工具。",
        """
        {
            "type": "object",
            "properties": {
                "action": { "type": "string", "enum": ["list_servers", "list_tools", "call"], "description": "要执行的操作" },
                "server": { "type": "string", "description": "服务器名称（list_tools / call 必填）" },
                "tool": { "type": "string", "description": "工具名称（call 必填）" },
                "arguments": { "type": "object", "description": "工具参数对象（call 使用）" }
            },
            "required": ["action"]
        }
        """);

    public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal))
            return inner.DispatchAsync(call, cancellationToken);
        return HandleAsync(call, cancellationToken);
    }

    private async Task<PureToolResult> HandleAsync(PureToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
            var action = String(root, "action") ?? "list_servers";

            switch (action)
            {
                case "list_servers":
                {
                    if (manager.Servers.Count == 0)
                        return new PureToolResult(call.Id, true, "没有配置任何 MCP 服务器。");
                    var text = string.Join("\n", manager.Servers.Select(server =>
                        $"- {server.Name}（{server.Transport}）"));
                    return new PureToolResult(call.Id, true, "MCP 服务器：\n" + text);
                }
                case "list_tools":
                {
                    var server = String(root, "server");
                    if (string.IsNullOrWhiteSpace(server))
                        return new PureToolResult(call.Id, false, "list_tools 需要参数 'server'。");
                    var tools = await manager.ListToolsAsync(server!, cancellationToken).ConfigureAwait(false);
                    if (tools.Count == 0)
                        return new PureToolResult(call.Id, true, $"服务器 '{server}' 没有声明工具。");

                    var builder = new StringBuilder();
                    builder.Append("服务器 ").Append(server).AppendLine(" 的工具：");
                    foreach (var tool in tools)
                    {
                        builder.Append("- ").Append(tool.Name);
                        if (tool.Description.Length > 0) builder.Append("：").Append(tool.Description);
                        builder.AppendLine();
                        var schema = tool.InputSchemaJson.Length > 4000
                            ? tool.InputSchemaJson[..4000] + "…"
                            : tool.InputSchemaJson;
                        builder.Append("  inputSchema: ").AppendLine(schema);
                    }
                    return new PureToolResult(call.Id, true, Cap(builder.ToString()));
                }
                case "call":
                {
                    var server = String(root, "server");
                    var tool = String(root, "tool");
                    if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(tool))
                        return new PureToolResult(call.Id, false, "call 需要参数 'server' 与 'tool'。");

                    var arguments = root.TryGetProperty("arguments", out var argumentsElement)
                        ? argumentsElement
                        : default;
                    if (arguments.ValueKind == JsonValueKind.String)
                    {
                        try
                        {
                            using var parsed = JsonDocument.Parse(arguments.GetString() ?? "{}");
                            arguments = JsonSerializer.SerializeToElement(parsed.RootElement);
                        }
                        catch (JsonException)
                        {
                            return new PureToolResult(call.Id, false, "arguments 不是合法 JSON。");
                        }
                    }
                    if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                        arguments = JsonSerializer.SerializeToElement(new { });

                    McpCallResult result;
                    using (var mcpSpan = PerfTracer.Step("mcp.call", "server", server))
                    {
                        mcpSpan.Tag("tool", tool);
                        result = await manager.CallAsync(server!, tool!, arguments.Clone(), cancellationToken)
                            .ConfigureAwait(false);
                        mcpSpan.Tag("ok", result.Success);
                        mcpSpan.Tag("resultChars", result.Content?.Length ?? 0);
                        if (!result.Success) mcpSpan.Fail("mcp_failed");
                    }
                    // MCP 输出视为不可信数据，显式标注来源。
                    var content = $"[MCP {server}/{tool}]\n{result.Content}";
                    return new PureToolResult(call.Id, result.Success, Cap(content));
                }
                default:
                    return new PureToolResult(call.Id, false,
                        $"未知 action '{action}'；支持 list_servers / list_tools / call。");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (McpException error)
        {
            return new PureToolResult(call.Id, false, "MCP 错误：" + error.Message);
        }
        catch (Exception error)
        {
            return new PureToolResult(call.Id, false, "MCP 调用失败：" + error.Message);
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Cap(string text) =>
        text.Length <= 64 * 1024 ? text : text[..(64 * 1024)] + "\n... [MCP 输出已截断]";
}
