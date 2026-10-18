using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fufucode.App.Features.Plugins;

/// <summary>本产品本地插件描述文件的固定名。</summary>
public static class PluginDescriptorFile
{
    public const string FileName = "fufucode-plugin.json";

    /// <summary>
    /// 读取并校验描述文件。返回 null 表示缺失；返回 Rejection 表示内容不合规。
    /// 未知或缺失的关键字段一律拒绝，不做"猜一个默认值"的兜底。
    /// </summary>
    public static (PluginDescriptor? Descriptor, LocalPluginImportRejection? Rejection) Read(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            return (null, new LocalPluginImportRejection(LocalPluginRejectionKind.MissingDescriptor,
                $"缺少本产品描述文件 {FileName}；Fufucode 本地包不读取 Pi/OMP 原生扩展声明。"));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, new LocalPluginImportRejection(LocalPluginRejectionKind.MalformedDescriptor,
                $"{FileName} 无法解析：{error.GetBaseException().Message}"));
        }

        using (document)
        {
            var root_element = document.RootElement;
            if (root_element.ValueKind != JsonValueKind.Object)
            {
                return (null, new LocalPluginImportRejection(LocalPluginRejectionKind.MalformedDescriptor,
                    $"{FileName} 顶层必须是对象。"));
            }

            var id = ReadString(root_element, "id");
            var displayName = ReadString(root_element, "displayName");
            var version = ReadString(root_element, "version");
            var backendText = ReadString(root_element, "backend");
            var entry = ReadString(root_element, "entryRelativePath");

            if (string.IsNullOrWhiteSpace(id))
                return (null, Reject("缺少 id"));
            if (string.IsNullOrWhiteSpace(displayName))
                return (null, Reject("缺少 displayName"));
            if (string.IsNullOrWhiteSpace(version))
                return (null, Reject("缺少 version"));
            if (string.IsNullOrWhiteSpace(backendText))
                return (null, Reject("缺少 backend"));
            if (string.IsNullOrWhiteSpace(entry))
                return (null, Reject("缺少 entryRelativePath"));

            // ID 直接参与路径与目录名：只接受安全的可见字符，避免路径拼接与大小写别名问题。
            if (!PluginIdRules.IsSafe(id))
                return (null, Reject("id 只允许字母、数字、点、下划线和连字符"));
            if (!PluginIdRules.IsSafe(version))
                return (null, Reject("version 只允许字母、数字、点、下划线和连字符"));

            var backend = ParseBackend(backendText!);
            if (backend == PluginBackend.Unknown)
                return (null, Reject($"backend “{backendText}” 不是已知后端（首版仅 pi）"));

            var supported = new List<string>();
            if (root_element.TryGetProperty("supportedHostVersions", out var versions))
            {
                if (versions.ValueKind != JsonValueKind.Array)
                    return (null, Reject("supportedHostVersions 必须是字符串数组"));
                foreach (var item in versions.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        return (null, Reject("supportedHostVersions 必须全部是字符串"));
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) supported.Add(text.Trim());
                }
            }
            else
            {
                return (null, Reject("缺少 supportedHostVersions（兼容按声明的精确版本列表判定）"));
            }

            var descriptor = new PluginDescriptor(id!.Trim(), displayName!.Trim(), version!.Trim(),
                backend, entry!.Trim(), supported)
            {
                Description = ReadString(root_element, "description"),
                SourceLabel = ReadString(root_element, "sourceLabel"),
            };
            return (descriptor, null);
        }
    }

    private static LocalPluginImportRejection Reject(string message) =>
        new(LocalPluginRejectionKind.MalformedDescriptor, $"{FileName} 校验失败：{message}。");

    public static PluginBackend ParseBackend(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "pi" => PluginBackend.Pi,
        "omp" => PluginBackend.Omp,
        "codex" => PluginBackend.Codex,
        "dsh" => PluginBackend.Dsh,
        _ => PluginBackend.Unknown,
    };

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>参与路径与目录名的字段的字符规则。</summary>
public static class PluginIdRules
{
    public static bool IsSafe(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') continue;
            return false;
        }

        // 纯点段会被路径规范化吞掉；Windows 保留设备名不能作目录名。
        var trimmed = value.Trim('.');
        if (trimmed.Length == 0) return false;
        return !IsReservedWindowsName(trimmed.Split('.')[0]);
    }

    /// <summary>把显示名/版本等任意文本压成安全目录名，绝不把显示名直接拼进路径。</summary>
    public static string Sanitize(string value, string fallback)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : (c is '.' or '_' or '-' ? c : '-'));
        }

        while (builder.Length > 1 && builder[^1] == '-') builder.Length--;
        var text = builder.ToString().Trim('-', '.');
        if (text.Length == 0) text = fallback;
        if (text.Length > 48) text = text[..48].Trim('-', '.');
        if (text.Length == 0) text = fallback;
        var head = text.Split('.')[0];
        return IsReservedWindowsName(head) ? fallback + "-" + text : text;
    }

    private static bool IsReservedWindowsName(string head) =>
        head.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
        head.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
        head.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
        head.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
        head.Length == 4 && head.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
            char.IsAsciiDigit(head[3]) ||
        head.Length == 4 && head.StartsWith("LPT", StringComparison.OrdinalIgnoreCase) &&
            char.IsAsciiDigit(head[3]);
}
