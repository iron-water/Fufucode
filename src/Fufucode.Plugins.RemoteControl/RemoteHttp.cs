using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Fufucode.Core.RemoteControl;

/// <summary>已解析的 HTTP 请求（只支持远控页面所需的单次请求-响应语义）。</summary>
internal sealed record RemoteHttpRequest(
    string Method,
    string Target,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body)
{
    public bool IsWebSocketUpgrade =>
        Headers.TryGetValue("upgrade", out var upgrade) &&
        upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase);

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public string? QueryValue(string name)
    {
        if (Query.Length == 0) return null;
        foreach (var pair in Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair[..separator];
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return null;
    }
}

/// <summary>HTTP/1.1 与 WebSocket 握手的最小实现（不依赖 HttpListener，避免 URL ACL 提权需求）。</summary>
internal static class RemoteHttp
{
    internal const int MaxRequestBytes = 256 * 1024;
    private const int MaxHeaderBytes = 32 * 1024;

    /// <summary>读取并解析一个请求；连接提前关闭时返回 null。</summary>
    internal static async Task<RemoteHttpRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(1024);
        var chunk = new byte[4096];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
            headerEnd = FindHeaderEnd(buffer);
            if ((headerEnd < 0 ? buffer.Count : headerEnd) > MaxHeaderBytes) return null;
        }

        var headerText = Encoding.ASCII.GetString(buffer.ToArray(), 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || parts[2] != "HTTP/1.1" || !parts[1].StartsWith('/')) return null;
        var method = parts[0];
        var target = parts[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator <= 0) continue;
            if (!headers.TryAdd(lines[i][..separator].Trim(), lines[i][(separator + 1)..].Trim()))
                return null;
        }

        if (headers.ContainsKey("transfer-encoding")) return null;
        var body = Array.Empty<byte>();
        if (headers.TryGetValue("content-length", out var lengthText))
        {
            if (!int.TryParse(lengthText, out var length) || length < 0 || length > MaxRequestBytes) return null;
            body = new byte[length];
            var already = buffer.Count - (headerEnd + 4);
            if (already > 0)
            {
                var copy = Math.Min(already, length);
                buffer.CopyTo(headerEnd + 4, body, 0, copy);
            }

            var offset = Math.Max(0, Math.Min(already, length));
            while (offset < length)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset, length - offset), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) return null;
                offset += read;
            }
        }

        var questionMark = target.IndexOf('?');
        var path = questionMark < 0 ? target : target[..questionMark];
        var query = questionMark < 0 ? string.Empty : target[questionMark..];
        return new RemoteHttpRequest(method.ToUpperInvariant(), target, Uri.UnescapeDataString(path), query, headers, body);
    }

    private static int FindHeaderEnd(List<byte> buffer)
    {
        for (var i = 3; i < buffer.Count; i++)
        {
            if (buffer[i - 3] == (byte)'\r' && buffer[i - 2] == (byte)'\n' &&
                buffer[i - 1] == (byte)'\r' && buffer[i] == (byte)'\n')
                return i - 3;
        }

        return -1;
    }

    internal static Task WriteJsonAsync(Stream stream, int status, string json, CancellationToken cancellationToken) =>
        WriteAsync(stream, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), cancellationToken);

    internal static async Task WriteAsync(Stream stream, int status, string contentType, byte[] body,
        CancellationToken cancellationToken, bool headOnly = false)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        head.Append("Access-Control-Allow-Origin: *\r\n");
        head.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        head.Append("Access-Control-Allow-Headers: Content-Type, Authorization, X-Requested-With\r\n");
        head.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);
        if (!headOnly && body.Length > 0) await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        _ => "OK",
    };

    /// <summary>完成 RFC 6455 握手并返回服务端 WebSocket。</summary>
    internal static async Task<WebSocket?> AcceptWebSocketAsync(Stream stream, RemoteHttpRequest request,
        CancellationToken cancellationToken)
    {
        var key = request.Header("sec-websocket-key");
        if (request.Method != "GET" || request.Header("sec-websocket-version") != "13" ||
            request.Header("connection")?.Split(',').Any(x => x.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase)) != true ||
            string.IsNullOrWhiteSpace(key)) return null;
        try { if (Convert.FromBase64String(key).Length != 16) return null; }
        catch (FormatException) { return null; }
        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var handshake = new StringBuilder();
        handshake.Append("HTTP/1.1 101 Switching Protocols\r\n");
        handshake.Append("Upgrade: websocket\r\n");
        handshake.Append("Connection: Upgrade\r\n");
        handshake.Append("Sec-WebSocket-Accept: ").Append(accept).Append("\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake.ToString()), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
    }
}
