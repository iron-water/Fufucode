using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Fufucode.App.Backend.Tests;

/// <summary>测试用 MCP 服务器：stdio 模式由测试进程自身以 `--fake-mcp-stdio` 启动。</summary>
internal static class FakeMcpServer
{
    public static async Task<int> RunStdioAsync()
    {
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            if (line.Length == 0) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (document)
            {
                var response = Handle(document.RootElement);
                if (response is null) continue;
                await Console.Out.WriteLineAsync(response.Value.GetRawText());
                await Console.Out.FlushAsync();
            }
        }
        return 0;
    }

    /// <summary>处理一条 JSON-RPC 请求；通知返回 null。</summary>
    public static JsonElement? Handle(JsonElement request)
    {
        var method = request.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
        var hasId = request.TryGetProperty("id", out var idElement);

        if (!hasId) return null; // notifications/initialized

        object? result = method switch
        {
            "initialize" => new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = new { } },
                serverInfo = new { name = "fake-mcp", version = "1.0.0" }
            },
            "tools/list" => new
            {
                tools = new object[]
                {
                    new
                    {
                        name = "echo",
                        description = "回显文本",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new { text = new { type = "string" } },
                            required = new[] { "text" }
                        }
                    },
                    new
                    {
                        name = "fail",
                        description = "返回错误",
                        inputSchema = new { type = "object", properties = new { } }
                    }
                }
            },
            "tools/call" => Call(request),
            "ping" => new { },
            _ => null
        };

        if (result is null)
            return JsonSerializer.SerializeToElement(new
            {
                jsonrpc = "2.0",
                id = idElement.Clone(),
                error = new { code = -32601, message = $"未知方法 '{method}'" }
            });

        return JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = idElement.Clone(),
            result
        });
    }

    private static object Call(JsonElement request)
    {
        var parameters = request.TryGetProperty("params", out var value) ? value : default;
        var name = parameters.ValueKind == JsonValueKind.Object &&
                   parameters.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : null;
        var arguments = parameters.ValueKind == JsonValueKind.Object &&
                        parameters.TryGetProperty("arguments", out var argumentsElement)
            ? argumentsElement
            : default;
        var text = arguments.ValueKind == JsonValueKind.Object &&
                   arguments.TryGetProperty("text", out var textElement)
            ? textElement.GetString()
            : null;

        return name switch
        {
            "echo" => new
            {
                content = new object[] { new { type = "text", text = "echo:" + text } },
                isError = false
            },
            "fail" => new
            {
                content = new object[] { new { type = "text", text = "expected failure" } },
                isError = true
            },
            _ => new
            {
                content = new object[] { new { type = "text", text = $"unknown tool '{name}'" } },
                isError = true
            }
        };
    }
}
