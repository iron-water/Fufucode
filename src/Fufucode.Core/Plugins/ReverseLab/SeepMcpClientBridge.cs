using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.Core.Plugins;

/// <summary>
/// Seep 逆向工作台 MCP 客户端桥（C# ⇄ Python stdio JSON-RPC 2.0）。
///
/// 生命周期边界（与插件市场状态严格对齐）：
/// - 只有 <c>tool.seep-reverse-lab</c> 处于“已安装且已启用”时，宿主才调用
///   <see cref="StartAsync"/> 启动 Python 子进程并注册工具；
/// - 停用或卸载时必须调用 <see cref="StopAsync"/>：终止子进程并清空工具表，
///   <see cref="Registrations"/> 随即为空，不再可能被调度（不留幽灵工具）；
/// - 未启动时执行任何工具调用都如实返回“未启动”，不伪造结果。
///
/// 工具清单不硬编码：启动时通过 <c>tools/list</c> 从服务端动态获取
/// （2026-10-16 实测为 23 个），因此工作台升级工具后无需改 C# 代码。
/// </summary>
public sealed class SeepMcpClientBridge : IPluginToolHandler, IAsyncDisposable
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ClientName = "fufucode-seep-bridge";
    private const string ClientVersion = "1.0.0";

    private readonly ISeepProcessFactory _factory;
    private readonly SeepProcessSpec _processSpec;
    private readonly SemaphoreSlim _callGate = new(1, 1);
    private readonly object _gate = new();
    private readonly List<PluginToolRegistration> _registrations = new();
    private readonly Dictionary<string, string> _descriptions = new(StringComparer.Ordinal);

    private ISeepProcess? _process;
    private int _nextRequestId;
    private bool _disposed;

    public SeepMcpClientBridge(
        SeepProcessSpec? processSpec = null,
        ISeepProcessFactory? processFactory = null,
        string? labRoot = null)
    {
        _processSpec = processSpec ?? SeepReverseLabAssets.CreateMcpProcessSpec(labRoot);
        _factory = processFactory ?? new SeepProcessFactory();
    }

    /// <summary>解析后的 MCP 服务脚本绝对路径，供诊断与交付记录使用。</summary>
    public string McpServerPath => _processSpec.Arguments.Count > 0 ? _processSpec.Arguments[0] : string.Empty;

    /// <summary>子进程是否正在运行；子进程异常退出后为 false。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate) return _process is { HasExited: false };
        }
    }

    /// <summary>从服务端 tools/list 动态获得的工具数量；未启动时为 0。</summary>
    public int ToolCount
    {
        get { lock (_gate) return _registrations.Count; }
    }

    /// <summary>服务端上报的 serverInfo.name / version，未握手成功时为 null。</summary>
    public string? ServerInfo { get; private set; }

    /// <summary>当前 MCP 子进程的真实操作系统 PID；未运行时为 null（供收尾核验用）。</summary>
    public int? ProcessId
    {
        get
        {
            lock (_gate) return _process?.ProcessId;
        }
    }

    /// <summary>本桥服务的插件能力（用于门禁拒绝时给出真实原因）。</summary>
    public PluginCapability? Capability => PluginCapability.ReverseLab;

    /// <summary>seep 工具的统一前缀；用于在工具清单尚未取得时仍能正确认领工具名。</summary>
    private const string ToolNamePrefix = "seep_";

    /// <summary>
    /// 是否属于本桥的工具。子进程未启动时 <see cref="Registrations"/> 为空，
    /// 但以 <c>seep_</c> 前缀命名的名字仍归本桥所有并向调用方解释真实原因，
    /// 不会被当成宿主基础工具或泛化的“未注册”。
    /// </summary>
    public bool OwnsToolName(string toolName) =>
        !string.IsNullOrEmpty(toolName) &&
        (toolName.StartsWith(ToolNamePrefix, StringComparison.Ordinal) ||
         Registrations.Any(r => string.Equals(r.Name, toolName, StringComparison.Ordinal)));

    /// <summary>
    /// 工具清单尚未就绪（子进程未启动、工作台未部署或启动失败）时的真实原因；
    /// 拿不到任何原因时如实说明“未启动”，绝不编造工具结果。
    /// </summary>
    public string? ExplainUnavailable(string toolName)
    {
        if (!OwnsToolName(toolName)) return null;
        if (LastError is { Length: > 0 } error) return error;
        if (!IsRunning)
            return $"Seep 工作台 MCP 子进程未运行（插件未启用或已停止），'{toolName}' 不可调用。";

        // 子进程在跑但清单里没有该名字：如实说明“未知工具”，不冒充暂时不可用。
        var registered = Registrations;
        if (registered.Count > 0 && registered.All(r => !string.Equals(r.Name, toolName, StringComparison.Ordinal)))
            return $"未知的 Seep 工作台工具：'{toolName}'。";

        return $"Seep 工作台 MCP 工具清单尚未就绪，'{toolName}' 暂不可调用。";
    }

    /// <summary>最近一次启动或调用失败的如实原因；成功时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 动态工具契约。插件注册表在 <see cref="StartAsync"/> 之后读取本属性，
    /// <see cref="StopAsync"/> 之后立即为空数组 —— 未启动即无工具可调度。
    /// </summary>
    public IReadOnlyList<PluginToolRegistration> Registrations
    {
        get { lock (_gate) return _registrations.ToArray(); }
    }

    /// <summary>
    /// 启动 MCP 子进程并完成 initialize + tools/list，随后动态填充 <see cref="Registrations"/>。
    /// 重复调用是幂等的：已在运行时直接返回当前工具清单。
    /// </summary>
    public async Task<IReadOnlyList<PluginToolRegistration>> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_process is { HasExited: false }) return _registrations.ToArray();
        }

        // 工作台未部署时如实报告缺项，不去启动一个必然失败的子进程。
        var probe = SeepReverseLabAssets.Probe(LabRootOf(_processSpec));
        if (!probe.McpAvailable)
        {
            LastError = probe.Describe();
            return Array.Empty<PluginToolRegistration>();
        }

        await _callGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopCore();
            var process = _factory.Start(_processSpec);
            lock (_gate) _process = process;

            await InitializeAsync(process, cancellationToken).ConfigureAwait(false);
            var tools = await ListToolsAsync(process, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _registrations.Clear();
                _registrations.AddRange(tools);
            }
            LastError = null;
            return Registrations;
        }
        catch (OperationCanceledException)
        {
            StopCore();
            throw;
        }
        catch (Exception error)
        {
            StopCore();
            LastError = $"启动 seep MCP 子进程失败：{error.Message}";
            return Array.Empty<PluginToolRegistration>();
        }
        finally
        {
            _callGate.Release();
        }
    }

    /// <summary>
    /// 停止桥接：终止子进程并清空工具表。幂等；重复调用不抛异常、不影响其它处理器。
    /// 这是卸载/停用路径，必须保证调用后 <see cref="Registrations"/> 为空。
    /// </summary>
    public Task StopAsync()
    {
        StopCore();
        return Task.CompletedTask;
    }

    private void StopCore()
    {
        ISeepProcess? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
            _registrations.Clear();
            _descriptions.Clear();
            ServerInfo = null;
        }
        if (process is null) return;
        try { process.Dispose(); }
        catch (Exception error) { LastError = $"终止 seep MCP 子进程时发生异常：{error.GetType().Name}"; }
    }

    /// <summary>执行一次已注册工具调用；工具名不在清单中时如实拒绝，不透传给其它调度器。</summary>
    public async Task<PureToolResult> ExecuteAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ISeepProcess? process;
        lock (_gate)
        {
            if (!_descriptions.ContainsKey(call.Name))
                return new PureToolResult(call.Id, false, $"未知的 Seep 工作台工具：'{call.Name}'。");
            process = _process;
        }

        if (process is null || process.HasExited)
            return new PureToolResult(call.Id, false, "Seep 工作台 MCP 子进程未运行（插件未启用或已停止）。");

        var argumentsJson = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;
        if (!TryParseArguments(argumentsJson, out var arguments, out var parseError))
            return new PureToolResult(call.Id, false, parseError!);

        await _callGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = JsonDocument.Parse(BuildToolCallRequest(call.Name, arguments));
            var response = await SendAsync(process, request.RootElement, cancellationToken).ConfigureAwait(false);
            return InterpretToolResult(call.Id, response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return new PureToolResult(call.Id, false, $"Seep 工作台工具 '{call.Name}' 调用失败：{error.Message}");
        }
        finally
        {
            _callGate.Release();
        }
    }

    private static bool TryParseArguments(string argumentsJson, out JsonElement arguments, out string? error)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                arguments = default;
                error = "工具参数必须是 JSON 对象。";
                return false;
            }
            arguments = doc.RootElement.Clone();
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            arguments = default;
            error = $"工具参数不是合法 JSON：{ex.Message}";
            return false;
        }
    }

    private static string BuildToolCallRequest(string toolName, JsonElement arguments) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 0,
            method = "tools/call",
            @params = new { name = toolName, arguments },
        });

    private async Task InitializeAsync(ISeepProcess process, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = NextId(),
            method = "initialize",
            @params = new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = ClientName, version = ClientVersion },
            },
        });
        using var doc = JsonDocument.Parse(request);
        var response = await SendAsync(process, doc.RootElement, cancellationToken).ConfigureAwait(false);
        if (!response.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("MCP initialize 未返回 result。");

        var serverInfo = result.TryGetProperty("serverInfo", out var info) && info.ValueKind == JsonValueKind.Object
            ? string.Concat(
                info.TryGetProperty("name", out var n) ? n.GetString() : "seep",
                " ",
                info.TryGetProperty("version", out var v) ? v.GetString() : "?")
            : null;
        ServerInfo = serverInfo;

        // 按 MCP stdio 规范，initialize 完成后必须发送 initialized 通知（无 id，无需响应）。
        await WriteLineAsync(process,
            """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PluginToolRegistration>> ListToolsAsync(
        ISeepProcess process, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = NextId(),
            method = "tools/list",
            @params = new { },
        });
        using var doc = JsonDocument.Parse(request);
        var response = await SendAsync(process, doc.RootElement, cancellationToken).ConfigureAwait(false);
        if (!response.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("tools", out var tools) ||
            tools.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("MCP tools/list 未返回工具数组。");
        }

        var registrations = new List<PluginToolRegistration>();
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object) continue;
            if (!tool.TryGetProperty("name", out var nameElement) ||
                nameElement.GetString() is not { Length: > 0 } name)
                continue;

            var description = tool.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? string.Empty : string.Empty;
            // inputSchema 缺失时给合法空对象，绝不写入 null（下游工具声明必须可解析）。
            var schema = tool.TryGetProperty("inputSchema", out var s) && s.ValueKind == JsonValueKind.Object
                ? s.GetRawText()
                : """{"type":"object","properties":{}}""";

            registrations.Add(new PluginToolRegistration(
                name, description, schema, PluginCapability.ReverseLab));
            descriptions[name] = description;
        }

        lock (_gate) _descriptions.Clear();
        foreach (var (name, description) in descriptions) lock (_gate) _descriptions[name] = description;
        return registrations;
    }

    private int NextId() => Interlocked.Increment(ref _nextRequestId);

    /// <summary>
    /// 发送一条请求并读取匹配 id 的响应；服务端主动通知（如 logging）会被跳过。
    /// 子进程在等待期间退出时如实抛异常，不无限阻塞。
    /// </summary>
    private static async Task<JsonElement> SendAsync(
        ISeepProcess process, JsonElement request, CancellationToken cancellationToken)
    {
        var requestId = request.TryGetProperty("id", out var id) ? id.GetInt32() : -1;
        await WriteLineAsync(process, request.GetRawText(), cancellationToken).ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                throw new InvalidOperationException("seep MCP 子进程已退出，未返回响应。");
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // 非 JSON 噪声行（如告警）不参与协议判定。

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number)
                    continue; // 通知（无 id）直接跳过。
                if (responseId.GetInt32() != requestId) continue;

                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                    var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                        ? c.GetInt32().ToString(CultureInfo.InvariantCulture) : "?";
                    throw new InvalidOperationException($"MCP 错误 {code}：{message ?? "未提供原因"}");
                }
                return root.Clone();
            }
        }
    }

    private static async Task WriteLineAsync(ISeepProcess process, string payload, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把 MCP tools/call 的 result 翻译成工具调度结果；isError 不被当作成功。</summary>
    private static PureToolResult InterpretToolResult(string callId, JsonElement response)
    {
        if (!response.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return new PureToolResult(callId, false, "seep MCP 返回缺少 result 字段。");

        var isError = result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
        var text = ExtractText(result);
        if (string.IsNullOrEmpty(text))
            text = result.GetRawText();
        return new PureToolResult(callId, !isError, text);
    }

    private static string ExtractText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("type", out var type) ||
                !string.Equals(type.GetString(), "text", StringComparison.Ordinal)) continue;
            if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(text.GetString());
            }
        }
        return builder.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }
        await StopAsync().ConfigureAwait(false);
        lock (_gate) _disposed = true;
        _callGate.Dispose();
    }

    /// <summary>从启动参数反推工作台根目录（脚本路径形如 &lt;root&gt;\Tool\... 时取 &lt;root&gt;）。</summary>
    private static string LabRootOf(SeepProcessSpec spec)
    {
        if (spec.Arguments.Count == 0) return SeepReverseLabAssets.DefaultLabRoot;
        var script = spec.Arguments[0];
        var directory = System.IO.Path.GetDirectoryName(script);
        // Tool\mcp → <root>；合规中继的 Tool\scripts\compliance-relay 同理。
        for (var i = 0; i < 2 && !string.IsNullOrEmpty(directory); i++)
        {
            if (string.Equals(System.IO.Path.GetFileName(directory), "Tool", StringComparison.OrdinalIgnoreCase))
                return System.IO.Path.GetDirectoryName(directory) ?? SeepReverseLabAssets.DefaultLabRoot;
            directory = System.IO.Path.GetDirectoryName(directory);
        }
        return SeepReverseLabAssets.DefaultLabRoot;
    }
}
