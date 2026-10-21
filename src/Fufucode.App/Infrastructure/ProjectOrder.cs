using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Fufucode.Core;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 会话管理里的项目显示顺序：新登记的项目排在最前，其余保持用户已见顺序。
/// 顺序只是展示偏好，不改变项目路径或任何会话数据。
/// </summary>
public static class ProjectOrder
{
    private const string Key = "projects:order:v1";

    /// <summary>按已保存顺序排列项目；首次出现的新项目插到最前并立即持久化。</summary>
    public static IReadOnlyList<ProjectRecord> Apply(DesktopMetadataStore store,
        IReadOnlyList<ProjectRecord> projects)
    {
        if (projects.Count == 0) return [];

        var known = Load(store);
        var byId = projects.ToDictionary(project => project.Id, StringComparer.Ordinal);
        var ordered = known.Where(byId.ContainsKey).ToList();
        // 没在当前可见顺序里的项目（新登记，或刚从未归档状态恢复）都视为新增，排到最前。
        var placed = ordered.ToHashSet(StringComparer.Ordinal);
        var additions = projects.Where(project => !placed.Contains(project.Id))
            .Select(project => project.Id).ToList();

        if (additions.Count == 0) return ordered.Select(id => byId[id]).ToArray();

        var merged = additions.Concat(ordered).ToList();
        store.SetPreference(Key, JsonSerializer.Serialize(merged));
        return merged.Select(id => byId[id]).ToArray();
    }

    /// <summary>把已有项目提到最前（“编辑项目/打开项目”后希望它排在顶部时使用）。</summary>
    public static void Promote(DesktopMetadataStore store, string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var list = Load(store).Where(id => !string.Equals(id, projectId, StringComparison.Ordinal)).ToList();
        list.Insert(0, projectId);
        store.SetPreference(Key, JsonSerializer.Serialize(list));
    }

    private static List<string> Load(DesktopMetadataStore store)
    {
        var json = store.GetPreference(Key);
        if (json is null) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
