using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fufucode.Core;

namespace Fufucode.App.Integration;

/// <summary>
/// “始终允许”规则：按项目 + 工具名记住的审批放行。
///
/// 系统通知与页内审批都走这里判断，保证同一个决定的含义一致：
/// 命中规则时不再弹窗、不再生成通知，直接以“允许”应答。
/// 规则按项目文件夹隔离，不同项目的工具权限互不继承。
/// </summary>
internal static partial class ApprovalPolicy
{
    internal sealed record Rule(string Project, string Tool);

    internal sealed record Document(int Version = 1, IReadOnlyList<Rule>? Rules = null);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Document))]
    private sealed partial class Context : JsonSerializerContext;

    private static readonly object Gate = new();
    // 规则键统一小写后比较，项目路径在不同大小写书写下仍是同一条规则。
    private static readonly HashSet<(string Project, string Tool)> Rules = new();
    private static string? _path;
    private static bool _loaded;

    /// <summary>数据根切换（启动/自测隔离）时重载；同一路径只读一次。</summary>
    internal static void Configure(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "approval-rules.json");
        lock (Gate)
        {
            if (_loaded && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return;
            _path = path;
            _loaded = true;
            Rules.Clear();
            try
            {
                if (!File.Exists(path)) return;
                var document = JsonSerializer.Deserialize(File.ReadAllText(path), Context.Default.Document);
                foreach (var rule in document?.Rules ?? [])
                    if (!string.IsNullOrWhiteSpace(rule.Project) && !string.IsNullOrWhiteSpace(rule.Tool))
                        Rules.Add((Normalize(rule.Project), rule.Tool.ToLowerInvariant()));
            }
            catch (Exception)
            {
                // 规则文件损坏按“没有规则”处理：宁可多问一次，也不静默放行。
            }
        }
    }

    /// <summary>是否需要直接放行该审批请求。</summary>
    internal static bool IsAlwaysAllowed(ExtensionUiRequest request, string? projectPath)
    {
        if (RequiresSecurityConfirmation(request) || ToolOf(request) is not { } tool) return false;
        var project = Normalize(projectPath);
        lock (Gate)
        {
            if (!_loaded) return false;
            return Rules.Contains((project, tool.ToLowerInvariant()));
        }
    }

    /// <summary>记住“始终允许”；写入失败时报错，不假装已保存。</summary>
    internal static bool Remember(ExtensionUiRequest request, string? projectPath)
    {
        if (RequiresSecurityConfirmation(request) || ToolOf(request) is not { } tool || string.IsNullOrWhiteSpace(projectPath)) return false;
        var project = Normalize(projectPath);
        lock (Gate)
        {
            if (!Rules.Add((project, tool.ToLowerInvariant()))) return true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path!)!);
                var document = new Document(1, Rules.Select(rule => new Rule(rule.Project, rule.Tool)).ToArray());
                File.WriteAllText(_path!, JsonSerializer.Serialize(document, Context.Default.Document));
                return true;
            }
            catch (Exception)
            {
                Rules.Remove((project, tool.ToLowerInvariant()));
                return false;
            }
        }
    }

    internal static bool RequiresSecurityConfirmation(ExtensionUiRequest request) =>
        request.Payload.ValueKind == JsonValueKind.Object &&
        request.Payload.TryGetProperty("securityRequired", out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>审批请求对应的工具名；非工具审批（扩展 ask）按方法名归类。</summary>
    internal static string? ToolOf(ExtensionUiRequest request)
    {
        var payload = request.Payload;
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("toolName", out var tool) && tool.ValueKind == JsonValueKind.String)
        {
            var name = tool.GetString();
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return string.IsNullOrWhiteSpace(request.Method) ? null : "dialog:" + request.Method;
    }

    /// <summary>审批正文里的工具参数原文（系统通知与页内弹窗展示同一份）。</summary>
    internal static string? DetailOf(ExtensionUiRequest request)
    {
        var payload = request.Payload;
        if (payload.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in new[] { "arguments", "message" })
        {
            if (payload.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String)
                return field.GetString();
        }
        return null;
    }

    private static string Normalize(string? project) => (project ?? string.Empty).TrimEnd('\\', '/');
}
