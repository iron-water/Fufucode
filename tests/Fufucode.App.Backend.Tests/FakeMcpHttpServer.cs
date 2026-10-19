using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.App.Backend.Tests;

/// <summary>测试用 Streamable HTTP MCP 服务器 + OAuth 令牌端点（回环 TcpListener）。</summary>
internal sealed class FakeMcpHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;

    public int Port { get; }
    public bool UseSse { get; set; }
    public string? RequiredBearerToken { get; set; }
    public List<string> TokenRequests { get; } = new();
    public int McpRequestCount { get; private set; }

    public FakeMcpHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = AcceptLoopAsync(_lifetime.Token);
    }

    public string McpUrl => $"http://127.0.0.1:{Port}/mcp";
    public string TokenUrl => $"http://127.0.0.1:{Port}/token";

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                var (method, path, headers, body) = await ReadRequestAsync(stream, cancellationToken)
                    .ConfigureAwait(false);

                if (method == "POST" && path.StartsWith("/token", StringComparison.Ordinal))
                {
                    TokenRequests.Add(body);
                    var json = JsonSerializer.Serialize(new
                    {
                        access_token = "test-token",
                        token_type = "Bearer",
                        expires_in = 3600
                    });
                    await WriteAsync(stream, 200, "application/json", json, null, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (path.StartsWith("/mcp", StringComparison.Ordinal) && method == "POST")
                {
                    McpRequestCount++;
                    if (RequiredBearerToken is { Length: > 0 } required &&
                        (!headers.TryGetValue("authorization", out var auth) || auth != "Bearer " + required))
                    {
                        await WriteAsync(stream, 401, "application/json", "{\"error\":\"unauthorized\"}",
                            null, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    using var document = JsonDocument.Parse(body);
                    var response = FakeMcpServer.Handle(document.RootElement);
                    if (response is null)
                    {
                        await WriteAsync(stream, 202, "application/json", "", null, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    var session = document.RootElement.TryGetProperty("method", out var m) && m.GetString() == "initialize"
                        ? new Dictionary<string, string> { ["Mcp-Session-Id"] = "test-session" }
                        : null;

                    if (UseSse)
                    {
                        var sse = "event: message\ndata: " + response.Value.GetRawText() + "\n\n";
                        await WriteAsync(stream, 200, "text/event-stream", sse, session, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteAsync(stream, 200, "application/json", response.Value.GetRawText(), session,
                            cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }

                await WriteAsync(stream, 404, "text/plain", "not found", null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 测试服务器忽略单连接错误。
            }
        }
    }

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers, string Body)>
        ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            headerBytes.Add(one[0]);
            var count = headerBytes.Count;
            if (count >= 4 && headerBytes[count - 4] == '\r' && headerBytes[count - 3] == '\n' &&
                headerBytes[count - 2] == '\r' && headerBytes[count - 1] == '\n')
                break;
        }

        var headerText = Encoding.UTF8.GetString(headerBytes.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines.FirstOrDefault()?.Split(' ') ?? [];
        var method = requestLine.Length > 0 ? requestLine[0] : string.Empty;
        var path = requestLine.Length > 1 ? requestLine[1] : "/";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var length = headers.TryGetValue("Content-Length", out var lengthText) &&
                     int.TryParse(lengthText, out var parsed)
            ? parsed
            : 0;
        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }
        return (method, path, headers, Encoding.UTF8.GetString(body, 0, offset));
    }

    private static async Task WriteAsync(Stream stream, int status, string contentType, string body,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(status).Append(' ').AppendLine(status == 200 ? "OK" : status switch
        {
            202 => "Accepted",
            401 => "Unauthorized",
            _ => "Not Found"
        });
        builder.Append("Content-Type: ").AppendLine(contentType);
        builder.Append("Content-Length: ").AppendLine(bytes.Length.ToString());
        builder.AppendLine("Connection: close");
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            builder.Append(name).Append(": ").AppendLine(value);
        builder.AppendLine();

        var head = Encoding.UTF8.GetBytes(builder.ToString());
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > 0) await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        try { _listener.Stop(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _lifetime.Dispose();
    }
}
