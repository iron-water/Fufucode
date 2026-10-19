using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Fufucode.Core.Backend;

/// <summary>MCP 服务器的认证方式。</summary>
public sealed record McpAuthConfig(
    string Type,
    string? TokenEnvironmentVariable = null,
    string? Token = null,
    string? GrantType = null,
    string? TokenUrl = null,
    string? AuthorizationUrl = null,
    string? ClientId = null,
    string? ClientSecret = null,
    IReadOnlyList<string>? Scopes = null,
    int? RedirectPort = null);

/// <summary>一个 MCP 服务器配置。</summary>
public sealed record McpServerConfig(
    string Name,
    string Transport,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? Env = null,
    string? WorkingDirectory = null,
    string? Url = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    McpAuthConfig? Auth = null,
    bool Enabled = true,
    int TimeoutSeconds = 60,
    string ProtocolVersion = "2025-06-18");

/// <summary>MCP 工具描述（inputSchema 保留原始 JSON 文本，按需给模型）。</summary>
public sealed record McpToolInfo(string Name, string Description, string InputSchemaJson);

/// <summary>MCP 工具调用结果。</summary>
public sealed record McpCallResult(bool Success, string Content);

/// <summary>
/// MCP 配置加载：用户级 + 项目级。项目级服务器默认不加载，除非用户配置里
/// 显式设置 `trustProjectConfig: true`（避免克隆仓库即执行任意命令）。
/// </summary>
public static class McpConfigLoader
{
    public static string UserConfigPath(string userHome) =>
        Path.Combine(userHome, ".fumo", "mcp.json");

    public static string ProjectConfigPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".fumo", "mcp.json");

    /// <summary>加载并合并配置；同名服务器项目级覆盖用户级。</summary>
    public static IReadOnlyList<McpServerConfig> Load(string? workspaceRoot, string? userHome = null)
    {
        userHome ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var merged = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
        var trustProject = false;

        var userPath = UserConfigPath(userHome);
        if (File.Exists(userPath))
        {
            var (servers, trust, _) = Parse(userPath);
            trustProject = trust;
            foreach (var server in servers) merged[server.Name] = server;
        }

        if (!string.IsNullOrWhiteSpace(workspaceRoot) && trustProject)
        {
            var projectPath = ProjectConfigPath(workspaceRoot);
            if (File.Exists(projectPath))
            {
                var (servers, _, _) = Parse(projectPath);
                foreach (var server in servers) merged[server.Name] = server;
            }
        }

        return merged.Values.Where(server => server.Enabled).OrderBy(server => server.Name,
            StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>解析单个配置文件；返回服务器、项目信任开关与错误列表。</summary>
    public static (IReadOnlyList<McpServerConfig> Servers, bool TrustProjectConfig,
        IReadOnlyList<string> Errors) Parse(string path)
    {
        var servers = new List<McpServerConfig>();
        var errors = new List<string>();
        var trustProject = false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path}: 顶层必须是 JSON 对象。");
                return (servers, false, errors);
            }

            if (root.TryGetProperty("trustProjectConfig", out var trust) &&
                trust.ValueKind is JsonValueKind.True or JsonValueKind.False)
                trustProject = trust.GetBoolean();

            if (!root.TryGetProperty("servers", out var serversElement) ||
                serversElement.ValueKind != JsonValueKind.Object)
                return (servers, trustProject, errors);

            foreach (var entry in serversElement.EnumerateObject())
            {
                try
                {
                    var server = ParseServer(entry.Name, entry.Value, path);
                    if (server is not null) servers.Add(server);
                }
                catch (Exception error)
                {
                    errors.Add($"{path}: 服务器 '{entry.Name}' 配置无效：{error.Message}");
                }
            }
        }
        catch (Exception error)
        {
            errors.Add($"{path}: {error.Message}");
        }

        return (servers, trustProject, errors);
    }

    private static McpServerConfig? ParseServer(string name, JsonElement element, string source)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("服务器必须是对象。");
        var transport = String(element, "transport") ?? (Has(element, "url") ? "http" : "stdio");
        var enabled = !element.TryGetProperty("enabled", out var enabledElement) ||
                      enabledElement.ValueKind != JsonValueKind.False;
        var timeout = element.TryGetProperty("timeoutSeconds", out var timeoutElement) &&
                      timeoutElement.TryGetInt32(out var requested) && requested > 0
            ? Math.Clamp(requested, 1, 600)
            : 60;

        var command = String(element, "command");
        var url = String(element, "url");
        if (transport == "stdio" && string.IsNullOrWhiteSpace(command)) throw new InvalidDataException("stdio 服务器缺少 command。");
        if (transport == "http" && string.IsNullOrWhiteSpace(url)) throw new InvalidDataException("http 服务器缺少 url。");

        return new McpServerConfig(
            name,
            transport,
            command,
            StringArray(element, "args"),
            StringMap(element, "env"),
            String(element, "cwd"),
            url,
            StringMap(element, "headers"),
            ParseAuth(element, source),
            enabled,
            timeout,
            String(element, "protocolVersion") ?? "2025-06-18");
    }

    private static McpAuthConfig? ParseAuth(JsonElement element, string source)
    {
        if (!element.TryGetProperty("auth", out var auth) || auth.ValueKind != JsonValueKind.Object) return null;
        var type = String(auth, "type");
        if (string.IsNullOrWhiteSpace(type)) return null;

        return type switch
        {
            "bearer" => new McpAuthConfig("bearer",
                TokenEnvironmentVariable: String(auth, "tokenEnv"),
                Token: String(auth, "token")),
            "oauth" => new McpAuthConfig("oauth",
                GrantType: String(auth, "grant") ?? "authorization_code",
                TokenUrl: String(auth, "tokenUrl"),
                AuthorizationUrl: String(auth, "authorizationUrl"),
                ClientId: String(auth, "clientId"),
                ClientSecret: String(auth, "clientSecret"),
                Scopes: StringArray(auth, "scopes"),
                RedirectPort: auth.TryGetProperty("redirectPort", out var port) && port.TryGetInt32(out var value)
                    ? value
                    : null),
            _ => throw new InvalidDataException($"不支持的认证类型 '{type}'。")
        };
    }

    private static bool Has(JsonElement element, string name) => element.TryGetProperty(name, out _);

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string>? StringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return null;
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string>? StringMap(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? ExpandEnvironment(property.Value.GetString() ?? string.Empty)
                : property.Value.GetRawText();
        return map;
    }

    /// <summary>展开 `${VAR}`；变量不存在时保留空串，避免把字面量误当凭据。</summary>
    private static string ExpandEnvironment(string value)
    {
        if (!value.Contains("${", StringComparison.Ordinal)) return value;
        return System.Text.RegularExpressions.Regex.Replace(value, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}",
            match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? string.Empty);
    }
}
