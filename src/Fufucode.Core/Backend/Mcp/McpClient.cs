using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>单个 MCP 服务器连接：初始化、工具发现与调用。</summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly McpServerConfig _config;
    private readonly IMcpTransport _transport;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private bool _started;

    public McpClient(McpServerConfig config, IMcpTransport transport)
    {
        _config = config;
        _transport = transport;
    }

    public string Name => _config.Name;
    public bool IsConnected => _transport.IsConnected;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started) return;
            var result = await _transport.RequestAsync("initialize", McpJson.Params(new
            {
                protocolVersion = _config.ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "Fufucode", version = "1.0" }
            }), cancellationToken).ConfigureAwait(false);

            if (result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("protocolVersion", out var version) &&
                version.ValueKind == JsonValueKind.String &&
                !string.Equals(version.GetString(), _config.ProtocolVersion, StringComparison.Ordinal))
            {
                // 服务器可以选择较低协议版本；继续使用服务器返回的版本。
            }

            await _transport.NotifyAsync("notifications/initialized", null, cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var tools = new List<McpToolInfo>();
        string? cursor = null;

        for (var page = 0; page < 20; page++)
        {
            var parameters = cursor is null ? McpJson.Params(new { }) : McpJson.Params(new { cursor });
            var result = await _transport.RequestAsync("tools/list", parameters, cancellationToken)
                .ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("tools", out var list) || list.ValueKind != JsonValueKind.Array)
                break;

            foreach (var tool in list.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object) continue;
                var name = String(tool, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var description = String(tool, "description") ?? string.Empty;
                var schema = tool.TryGetProperty("inputSchema", out var inputSchema)
                    ? inputSchema.GetRawText()
                    : "{\"type\":\"object\"}";
                tools.Add(new McpToolInfo(name!, description, schema));
            }

            cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
            if (string.IsNullOrEmpty(cursor)) break;
        }

        return tools;
    }

    public async Task<McpCallResult> CallToolAsync(string tool, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var result = await _transport.RequestAsync("tools/call", McpJson.Params(new
        {
            name = tool,
            arguments
        }), cancellationToken).ConfigureAwait(false);

        var isError = result.ValueKind == JsonValueKind.Object &&
                      result.TryGetProperty("isError", out var errorFlag) &&
                      errorFlag.ValueKind == JsonValueKind.True;

        var content = new StringBuilder();
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in blocks.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                var type = String(block, "type");
                switch (type)
                {
                    case "text":
                        content.AppendLine(String(block, "text") ?? string.Empty);
                        break;
                    case "image":
                        content.AppendLine("[MCP 返回了图片内容，当前内核不展示。]");
                        break;
                    case "resource":
                        content.AppendLine(RenderResource(block));
                        break;
                    default:
                        content.AppendLine(block.GetRawText());
                        break;
                }
            }
        }

        if (content.Length == 0 && result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("structuredContent", out var structured))
            content.AppendLine(structured.GetRawText());

        return new McpCallResult(!isError, content.ToString().TrimEnd());
    }

    private static string RenderResource(JsonElement block)
    {
        if (!block.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object)
            return "[MCP 资源]";
        if (resource.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return text.GetString() ?? string.Empty;
        var uri = String(resource, "uri") ?? string.Empty;
        return $"[MCP 资源：{uri}]";
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}

/// <summary>
/// MCP 服务器管理器：懒启动、工具缓存、空闲回收与统一错误语义。
/// 没有服务器时不产生任何 `mcp` 工具，也不启动任何进程。
/// </summary>
public sealed class McpManager : IAsyncDisposable
{
    private sealed class Connection
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public McpClient? Client { get; set; }
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    }

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyList<McpServerConfig> _servers;
    private readonly string? _userHome;
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task? _reaper;
    private bool _disposed;

    private McpManager(IReadOnlyList<McpServerConfig> servers, string? userHome)
    {
        _servers = servers;
        _userHome = userHome;
        foreach (var server in servers) _connections[server.Name] = new Connection();
        if (servers.Count > 0) _reaper = ReapLoopAsync(_lifetime.Token);
    }

    /// <summary>从 `~/.fumo/mcp.json` 与（受信任的）项目配置加载；无配置时返回空管理器。</summary>
    public static McpManager Load(string? workspaceRoot, string? userHome = null) =>
        new(McpConfigLoader.Load(workspaceRoot, userHome), userHome);

    /// <summary>测试/组合根可直接注入服务器列表。</summary>
    public static McpManager Create(IReadOnlyList<McpServerConfig> servers, string? userHome = null) =>
        new(servers, userHome);

    public IReadOnlyList<McpServerConfig> Servers => _servers;
    public bool HasServers => _servers.Count > 0;

    public McpServerConfig? Find(string name) =>
        _servers.FirstOrDefault(server => string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>系统提示词中的紧凑服务器目录（只列名字与传输方式，不列工具）。</summary>
    public string BuildPromptSection()
    {
        if (_servers.Count == 0) return string.Empty;
        var builder = new StringBuilder();
        builder.AppendLine("# MCP 服务器");
        builder.AppendLine("使用 mcp 工具按需查看工具或调用：action=list_servers | list_tools | call。");
        foreach (var server in _servers)
            builder.Append("- ").Append(server.Name).Append("（").Append(server.Transport).AppendLine("）");
        return builder.ToString().TrimEnd();
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(string serverName,
        CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(serverName, cancellationToken).ConfigureAwait(false);
        return await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpCallResult> CallAsync(string serverName, string tool, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(serverName, cancellationToken).ConfigureAwait(false);
        return await client.CallToolAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpClient> GetClientAsync(string serverName, CancellationToken cancellationToken)
    {
        var config = Find(serverName) ?? throw new McpException($"未知 MCP 服务器：'{serverName}'。");
        Connection connection;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            connection = _connections[config.Name];
        }

        await connection.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (connection.Client is { IsConnected: false } dead)
            {
                connection.Client = null;
                await dead.DisposeAsync().ConfigureAwait(false);
            }

            if (connection.Client is null)
            {
                var transport = CreateTransport(config);
                var client = new McpClient(config, transport);
                try
                {
                    await client.StartAsync(cancellationToken).ConfigureAwait(false);
                    connection.Client = client;
                }
                catch
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            connection.LastUsed = DateTimeOffset.UtcNow;
            return connection.Client;
        }
        finally
        {
            connection.Gate.Release();
        }
    }

    private IMcpTransport CreateTransport(McpServerConfig config)
    {
        if (config.Transport == "stdio") return new StdioMcpTransport(config);

        OAuthTokenProvider? tokens = null;
        if (config.Auth is { Type: "oauth" } auth)
            tokens = new OAuthTokenProvider(config.Name, auth, _userHome);

        // bearer：直接作为请求头；oauth 由传输层按需获取令牌。
        var headers = new Dictionary<string, string>(config.Headers ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        if (config.Auth is { Type: "bearer" } bearer)
        {
            var token = bearer.Token ?? (bearer.TokenEnvironmentVariable is { Length: > 0 } variable
                ? Environment.GetEnvironmentVariable(variable)
                : null);
            if (string.IsNullOrWhiteSpace(token))
                throw new McpException($"MCP 服务器 '{config.Name}' 的 bearer token 未配置或环境变量为空。");
            headers["Authorization"] = "Bearer " + token;
        }

        var effective = config with { Headers = headers };
        return new HttpMcpTransport(effective, tokens);
    }

    private async Task ReapLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await ReapIdleAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReapIdleAsync()
    {
        foreach (var (name, connection) in _connections.ToArray())
        {
            if (DateTimeOffset.UtcNow - connection.LastUsed < IdleTimeout) continue;
            if (!await connection.Gate.WaitAsync(0).ConfigureAwait(false)) continue;
            try
            {
                if (connection.Client is { } client)
                {
                    connection.Client = null;
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch { }
            finally { connection.Gate.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _lifetime.Cancel();
        if (_reaper is not null)
        {
            try { await _reaper.ConfigureAwait(false); } catch { }
        }
        foreach (var connection in _connections.Values)
        {
            if (connection.Client is { } client)
            {
                connection.Client = null;
                try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            connection.Gate.Dispose();
        }
        _lifetime.Dispose();
    }
}
