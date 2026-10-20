using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fufucode.Core;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 项目归档状态：只影响界面可见性，不移动、不删除项目目录或任何会话文件。
/// 会话归档沿用 <see cref="SessionPresentation.Archived"/>；项目归档单独保存，
/// 因此恢复项目不会改写会话各自的归档决定。
/// </summary>
public static class ProjectArchive
{
    private const string Key = "project:archive:v1";

    /// <summary>已归档项目 id，最近归档的在前。</summary>
    public static IReadOnlyList<string> Load(DesktopMetadataStore store)
    {
        var json = store.GetPreference(Key);
        if (json is null) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static IReadOnlySet<string> LoadSet(DesktopMetadataStore store) =>
        Load(store).ToHashSet(StringComparer.Ordinal);

    public static bool IsArchived(DesktopMetadataStore store, string projectId) =>
        projectId.Length > 0 && LoadSet(store).Contains(projectId);

    /// <summary>
    /// 按路径判断：尚未登记为项目、或磁盘上已不存在的路径一律不算归档。
    /// 最近项目列表允许包含已删除目录，这里不能因为路径不存在而抛异常中断启动。
    /// </summary>
    public static bool IsArchivedPath(DesktopMetadataStore store, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return false;
        return store.TryGetProject(projectPath) is { } project && IsArchived(store, project.Id);
    }

    public static void SetArchived(DesktopMetadataStore store, string projectId, bool archived)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var list = Load(store).Where(id => !string.Equals(id, projectId, StringComparison.Ordinal)).ToList();
        if (archived) list.Insert(0, projectId);
        store.SetPreference(Key, JsonSerializer.Serialize(list));
    }
}

/// <summary>会话是否已归档：显式归档，或所属项目已归档。</summary>
public static class SessionArchive
{
    public static bool IsArchived(DesktopMetadataStore store, SessionRecord record,
        IReadOnlySet<string>? archivedProjects = null) =>
        SessionPresentation.Load(store, record.Id).Archived ||
        (archivedProjects ?? ProjectArchive.LoadSet(store)).Contains(record.ProjectId);
}
