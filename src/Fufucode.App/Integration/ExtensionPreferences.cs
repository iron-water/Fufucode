using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Fufucode.Core;

namespace Fufucode.App.Integration;

/// <summary>应用扩展变更失败（新扩展加载失败或项目内扩展未信任），已回滚到旧启用列表。</summary>
public sealed record ExtensionApplyFailure(string ExtensionPath, string Kind, string Message)
{
    public static ExtensionApplyFailure From(Exception ex) => ex switch
    {
        UserExtensionLoadException { Kind: UserExtensionLoadFailureKind.MissingPath } missing =>
            new(missing.ExtensionPath, "路径缺失", missing.Message),
        UserExtensionLoadException load =>
            new(load.ExtensionPath, "加载失败",
                load.InnerException?.GetBaseException().Message ?? load.Message),
        ProjectExtensionNotTrustedException trust =>
            new(trust.ExtensionPath, "需要信任项目", trust.Message),
        _ => new(string.Empty, "未知", ex.GetBaseException().Message),
    };
}

/// <summary>
/// 扩展管理持久化（H4/M5）：启用列表与手动添加列表按项目隔离存进 desktop.db，
/// 键 app.enabled-extensions:&lt;projectId&gt; / app.added-extensions:&lt;projectId&gt;，
/// 最近加载错误存 app.extension-errors:&lt;projectId&gt;（JSON 对象 path→message）。
/// </summary>
internal static class ExtensionPreferences
{
    internal static string EnabledKey(string projectId) => $"app.enabled-extensions:{projectId}";
    private static string AddedKey(string projectId) => $"app.added-extensions:{projectId}";
    private static string ErrorsKey(string projectId) => $"app.extension-errors:{projectId}";

    public static IReadOnlyList<string> LoadEnabled(DesktopMetadataStore metadata, string projectId) =>
        LoadList(metadata.GetPreference(EnabledKey(projectId)));

    public static void SaveEnabled(DesktopMetadataStore metadata, string projectId,
        IReadOnlyList<string> paths) =>
        metadata.SetPreference(EnabledKey(projectId), JsonSerializer.Serialize(paths));

    public static IReadOnlyList<string> LoadAdded(DesktopMetadataStore metadata, string projectId) =>
        LoadList(metadata.GetPreference(AddedKey(projectId)));

    public static void SaveAdded(DesktopMetadataStore metadata, string projectId,
        IReadOnlyList<string> paths) =>
        metadata.SetPreference(AddedKey(projectId), JsonSerializer.Serialize(paths));

    /// <summary>最近一次加载失败（路径 → 消息）；重开 App 后仍可显示。</summary>
    public static IReadOnlyDictionary<string, string> LoadErrors(DesktopMetadataStore metadata,
        string projectId)
    {
        var raw = metadata.GetPreference(ErrorsKey(projectId));
        if (raw is null)
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            return parsed ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static void RecordError(DesktopMetadataStore metadata, string projectId,
        string extensionPath, string message)
    {
        var errors = new Dictionary<string, string>(LoadErrors(metadata, projectId),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        errors[extensionPath] = message;
        metadata.SetPreference(ErrorsKey(projectId), JsonSerializer.Serialize(errors));
    }

    /// <summary>加载成功后清除这些路径的旧错误记录（修好后再次启用不应残留“加载失败”）。</summary>
    public static void ClearErrors(DesktopMetadataStore metadata, string projectId,
        IEnumerable<string> paths)
    {
        var errors = new Dictionary<string, string>(LoadErrors(metadata, projectId),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var changed = false;
        foreach (var path in paths)
        {
            changed |= errors.Remove(path);
        }

        if (changed)
        {
            metadata.SetPreference(ErrorsKey(projectId), JsonSerializer.Serialize(errors));
        }
    }

    private static IReadOnlyList<string> LoadList(string? raw)
    {
        if (raw is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
