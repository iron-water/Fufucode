using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>MCP 协议错误（JSON-RPC error 或传输失败）。</summary>
public sealed class McpException(string message, int? code = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? Code { get; } = code;
}

/// <summary>JSON-RPC 2.0 传输抽象（stdio 或 Streamable HTTP）。</summary>
public interface IMcpTransport : IAsyncDisposable
{
    string? SessionId { get; }
    bool IsConnected { get; }
    Task<JsonElement> RequestAsync(string method, JsonElement? parameters, CancellationToken cancellationToken);
    Task NotifyAsync(string method, JsonElement? parameters, CancellationToken cancellationToken);
}

internal static class McpJson
{
    public static JsonElement Params(object? value) =>
        value is null ? default : JsonSerializer.SerializeToElement(value);

    public static string ErrorMessage(JsonElement error) =>
        error.ValueKind == JsonValueKind.Object &&
        error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString() ?? "MCP 请求失败。"
            : "MCP 请求失败。";

    public static int? ErrorCode(JsonElement error) =>
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) &&
        code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var parsed)
            ? parsed
            : null;
}

/// <summary>stdio 传输：子进程 stdin/stdout 上的换行分隔 JSON-RPC。</summary>
public sealed class StdioMcpTransport : IMcpTransport
{
    private readonly Process _process;
    private readonly McpServerConfig _config;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly StringBuilder _stderrTail = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _readTask;

    public StdioMcpTransport(McpServerConfig config)
    {
        _config = config;
        var psi = new ProcessStartInfo
        {
            FileName = config.Command!,
            WorkingDirectory = ResolveWorkingDirectory(config),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in config.Args ?? []) psi.ArgumentList.Add(argument);
        foreach (var (name, value) in config.Env ?? new Dictionary<string, string>()) psi.Environment[name] = value;

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Start();
        _process.Exited += (_, _) => FailPending(new McpException(
            $"MCP 服务器 '{config.Name}' 已退出（exit {SafeExitCode()}）：{StderrTail}"));
        _readTask = ReadLoopAsync(_lifetime.Token);
        _ = ReadStderrAsync(_lifetime.Token);
    }

    private string ResolveWorkingDirectory(McpServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.WorkingDirectory)) return Environment.CurrentDirectory;
        return Path.GetFullPath(config.WorkingDirectory);
    }

    public string? SessionId => null;

    public bool IsConnected
    {
        get
        {
            try { return !_process.HasExited; }
            catch { return false; }
        }
    }

    private int? SafeExitCode()
    {
        try { return _process.HasExited ? _process.ExitCode : null; } catch { return null; }
    }

    public string StderrTail
    {
        get { lock (_stderrTail) return _stderrTail.ToString(); }
    }

    public async Task<JsonElement> RequestAsync(string method, JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            await WriteAsync(id, method, parameters, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
            try
            {
                return await waiter.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new McpException($"MCP 请求 '{method}' 超时（{_config.TimeoutSeconds} 秒）。");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task NotifyAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method
        };
        if (parameters is { } value) payload["params"] = value;
        await WriteLineAsync(JsonSerializer.Serialize(payload), cancellationToken).ConfigureAwait(false);
    }

    private Task WriteAsync(string id, string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method
        };
        if (parameters is { } value) payload["params"] = value;
        return WriteLineAsync(JsonSerializer.Serialize(payload), cancellationToken);
    }

    private async Task WriteLineAsync(string json, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;

                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }

                using (document)
                {
                    var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var idElement) ||
                        idElement.ValueKind != JsonValueKind.String) continue;
                    if (!_pending.TryRemove(idElement.GetString()!, out var waiter)) continue;

                    if (root.TryGetProperty("error", out var error))
                        waiter.TrySetException(new McpException(McpJson.ErrorMessage(error), McpJson.ErrorCode(error)));
                    else if (root.TryGetProperty("result", out var result))
                        waiter.TrySetResult(result.Clone());
                    else
                        waiter.TrySetResult(default);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            FailPending(new McpException($"MCP stdio 读取失败：{error.Message}", inner: error));
        }
        finally
        {
            FailPending(new McpException($"MCP 服务器 '{_config.Name}' 连接已关闭。{StderrTail}"));
        }
    }

    private async Task ReadStderrAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                lock (_stderrTail)
                {
                    _stderrTail.AppendLine(line);
                    if (_stderrTail.Length > 16_000) _stderrTail.Remove(0, _stderrTail.Length - 16_000);
                }
            }
        }
        catch { }
    }

    private void FailPending(McpException error)
    {
        foreach (var entry in _pending)
            if (_pending.TryRemove(entry.Key, out var waiter)) waiter.TrySetException(error);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            _process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        }
        try { if (_readTask is not null) await _readTask.ConfigureAwait(false); } catch { }
        _process.Dispose();
        _lifetime.Dispose();
        _writeGate.Dispose();
    }
}

/// <summary>Streamable HTTP 传输：POST JSON-RPC，响应可为 JSON 或 SSE；支持 OAuth Bearer 与会话头。</summary>
public sealed class HttpMcpTransport : IMcpTransport
{
    private readonly HttpClient _http;
    private readonly McpServerConfig _config;
    private readonly OAuthTokenProvider? _tokens;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _sessionId;

    public HttpMcpTransport(McpServerConfig config, OAuthTokenProvider? tokens, HttpClient? httpClient = null)
    {
        _config = config;
        _tokens = tokens;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string? SessionId => _sessionId;
    public bool IsConnected => true;

    public async Task<JsonElement> RequestAsync(string method, JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var body = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is { } value) body["params"] = value;

        var response = await SendWithAuthAsync(JsonSerializer.Serialize(body), cancellationToken).ConfigureAwait(false);
        using (response)
        {
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
                _sessionId = sessionValues.FirstOrDefault();

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
                throw new McpException(
                    $"MCP HTTP 请求 '{method}' 失败：HTTP {(int)response.StatusCode}。{Bound(errorBody)}");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
                return await ReadSseResponseAsync(response, id, cancellationToken).ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            return ExtractResult(document.RootElement, id);
        }
    }

    public async Task NotifyAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is { } value) body["params"] = value;
        using var response = await SendWithAuthAsync(JsonSerializer.Serialize(body), cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
            throw new McpException($"MCP 通知 '{method}' 失败：HTTP {(int)response.StatusCode}。{Bound(errorBody)}");
        }
    }

    private async Task<HttpResponseMessage> SendWithAuthAsync(string json, CancellationToken cancellationToken)
    {
        var response = await SendOnceAsync(json, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        if (_tokens is null) throw new McpException("MCP 服务器要求认证，但未配置 auth。");
        await _tokens.InvalidateAsync(cancellationToken).ConfigureAwait(false);
        return await SendOnceAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(string json, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _config.Url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _config.ProtocolVersion);
        if (_sessionId is { Length: > 0 }) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);

        foreach (var (name, value) in _config.Headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(name, value);

        if (_tokens is not null)
        {
            await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                if (token is { Length: > 0 })
                {
                    request.Headers.Remove("Authorization");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
            }
            finally { _tokenGate.Release(); }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException($"MCP HTTP 请求超时（{_config.TimeoutSeconds} 秒）。");
        }
    }

    private async Task<JsonElement> ReadSseResponseAsync(HttpResponseMessage response, string id,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var sse in ServerSentEvents.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (sse.IsDone) break;
            if (string.IsNullOrWhiteSpace(sse.Data)) continue;
            using var document = JsonDocument.Parse(sse.Data);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String ||
                idElement.GetString() != id) continue;
            return ExtractResult(root, id);
        }
        throw new McpException("MCP SSE 响应在返回结果前结束。");
    }

    private static JsonElement ExtractResult(JsonElement root, string id)
    {
        if (root.TryGetProperty("error", out var error))
            throw new McpException(McpJson.ErrorMessage(error), McpJson.ErrorCode(error));
        return root.TryGetProperty("result", out var result) ? result.Clone() : default;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static string Bound(string text) =>
        text.Length <= 2000 ? text : text[..2000] + "…";

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _tokenGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
