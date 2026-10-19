using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>
/// MCP OAuth 2.1 令牌获取：支持 client_credentials 与 authorization_code + PKCE。
/// 令牌缓存在用户目录（明文 JSON，仅当前用户可读）；401 时由传输层调用 <see cref="InvalidateAsync"/>。
/// </summary>
public sealed class OAuthTokenProvider
{
    private sealed record CachedToken(string? AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

    private readonly string _serverName;
    private readonly McpAuthConfig _config;
    private readonly string _cachePath;
    private readonly HttpClient _http;
    private readonly Func<string, Task> _openBrowser;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedToken? _token;

    public OAuthTokenProvider(string serverName, McpAuthConfig config, string? userHome = null,
        HttpClient? httpClient = null, Func<string, Task>? openBrowser = null)
    {
        _serverName = serverName;
        _config = config;
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{config.TokenUrl}|{config.AuthorizationUrl}|{config.ClientId}")))[..8].ToLowerInvariant();
        _cachePath = Path.Combine(userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".fumo", "mcp-tokens", $"{SafeName(serverName)}-{identity}.json");
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _openBrowser = openBrowser ?? (url =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            return Task.CompletedTask;
        });
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _token ??= LoadCache();
            if (_token?.AccessToken is { Length: > 0 } access &&
                _token.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
                return access;

            if (_token?.RefreshToken is { Length: > 0 } refresh)
            {
                try
                {
                    var refreshed = await ExchangeAsync(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = refresh,
                        ["client_id"] = _config.ClientId ?? string.Empty
                    }, cancellationToken).ConfigureAwait(false);
                    _token = refreshed;
                    SaveCache(refreshed);
                    return refreshed.AccessToken!;
                }
                catch (McpException)
                {
                    // 刷新失败则重新走授权流程。
                }
            }

            var grant = _config.GrantType ?? "authorization_code";
            CachedToken token = grant switch
            {
                "client_credentials" => await ExchangeAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _config.ClientId ?? string.Empty,
                    ["client_secret"] = _config.ClientSecret ?? string.Empty,
                    ["scope"] = string.Join(' ', _config.Scopes ?? [])
                }, cancellationToken).ConfigureAwait(false),
                "authorization_code" => await AuthorizeAsync(cancellationToken).ConfigureAwait(false),
                _ => throw new McpException($"不支持的 OAuth grant：'{grant}'。")
            };

            _token = token;
            SaveCache(token);
            return token.AccessToken!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>401 后调用：只丢弃访问令牌，保留 refresh_token 供下一次刷新。</summary>
    public async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null) _token = _token with { AccessToken = null, ExpiresAt = DateTimeOffset.MinValue };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CachedToken> AuthorizeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.AuthorizationUrl) || string.IsNullOrWhiteSpace(_config.TokenUrl))
            throw new McpException("OAuth authorization_code 需要 authorizationUrl 与 tokenUrl。");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var listener = new TcpListener(IPAddress.Loopback, _config.RedirectPort ?? 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{port}/callback";

        try
        {
            var query = new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = _config.ClientId ?? string.Empty,
                ["redirect_uri"] = redirectUri,
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256"
            };
            if (_config.Scopes is { Count: > 0 } scopes) query["scope"] = string.Join(' ', scopes);
            var authorizationUrl = AppendQuery(_config.AuthorizationUrl, query);

            await _openBrowser(authorizationUrl).ConfigureAwait(false);
            var code = await WaitForCodeAsync(listener, state, cancellationToken).ConfigureAwait(false);

            var parameters = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = _config.ClientId ?? string.Empty,
                ["code_verifier"] = verifier
            };
            if (!string.IsNullOrWhiteSpace(_config.ClientSecret))
                parameters["client_secret"] = _config.ClientSecret!;
            return await ExchangeAsync(parameters, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    private static async Task<string> WaitForCodeAsync(TcpListener listener, string expectedState,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) ?? string.Empty;
        var parts = requestLine.Split(' ');
        var path = parts.Length >= 2 ? parts[1] : "/";

        var queryIndex = path.IndexOf('?');
        var query = queryIndex < 0 ? "" : path[(queryIndex + 1)..];
        var values = ParseQuery(query.Trim());
        values.TryGetValue("state", out var state);
        values.TryGetValue("code", out var code);

        var body = code is { Length: > 0 } && state == expectedState
            ? "<html><body><h3>授权完成，可以关闭此页面。</h3></body></html>"
            : "<html><body><h3>授权失败。</h3></body></html>";
        var response = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        var bytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);

        if (state != expectedState) throw new McpException("OAuth 回调 state 不匹配。");
        if (string.IsNullOrWhiteSpace(code)) throw new McpException("OAuth 回调缺少 code。");
        return code!;
    }

    private async Task<CachedToken> ExchangeAsync(Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.TokenUrl)) throw new McpException("OAuth 缺少 tokenUrl。");

        var useBasic = !string.IsNullOrWhiteSpace(_config.ClientId) &&
                       !string.IsNullOrWhiteSpace(_config.ClientSecret);
        var form = parameters
            .Where(pair => pair.Value.Length > 0)
            .Where(pair => !(useBasic && pair.Key == "client_secret"))
            .ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Post, _config.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.ParseAdd("application/json");
        if (useBasic)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{Uri.EscapeDataString(_config.ClientId!)}:{Uri.EscapeDataString(_config.ClientSecret!)}")));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new McpException($"OAuth 令牌请求失败：HTTP {(int)response.StatusCode}。{Bound(json)}");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String)
            throw new McpException($"OAuth 令牌响应缺少 access_token：{Bound(json)}");

        var expiresIn = root.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt32(out var seconds)
            ? seconds
            : 3600;
        var refresh = root.TryGetProperty("refresh_token", out var refreshElement) &&
                      refreshElement.ValueKind == JsonValueKind.String
            ? refreshElement.GetString()
            : _token?.RefreshToken;

        return new CachedToken(access.GetString(),
            refresh,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)));
    }

    private CachedToken? LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(_cachePath));
            var root = document.RootElement;
            return new CachedToken(
                root.TryGetProperty("accessToken", out var access) ? access.GetString() : null,
                root.TryGetProperty("refreshToken", out var refresh) ? refresh.GetString() : null,
                root.TryGetProperty("expiresAt", out var expiry) &&
                DateTimeOffset.TryParse(expiry.GetString(), out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue);
        }
        catch
        {
            return null;
        }
    }

    private void SaveCache(CachedToken token)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath)!;
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(new
            {
                server = _serverName,
                accessToken = token.AccessToken,
                refreshToken = token.RefreshToken,
                expiresAt = token.ExpiresAt.ToString("O")
            }, new JsonSerializerOptions { WriteIndented = true });
            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch
        {
            // 缓存失败不影响本次请求。
        }
    }

    private static string AppendQuery(string url, IReadOnlyDictionary<string, string> values)
    {
        var separator = url.Contains('?') ? '&' : '?';
        var query = string.Join("&", values.Where(pair => pair.Value.Length > 0)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return url + separator + query;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? string.Empty : pair[(equals + 1)..];
            values[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }
        return values;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Bound(string text) => text.Length <= 2000 ? text : text[..2000] + "…";
}
