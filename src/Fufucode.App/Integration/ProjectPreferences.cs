using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Fufucode.Core;

namespace Fufucode.App.Integration;

/// <summary>
/// 最近项目列表（M3.1）：全局键 app.recentProjects，JSON 字符串数组，
/// 最近在前、按规范化路径去重、上限 10。无显式 --project 时启动回退到
/// 最近一个磁盘上仍存在的项目；全部缺失则进入“选择项目”态。
/// </summary>
internal static class ProjectPreferences
{
    private const string RecentKey = "app.recentProjects";
    internal const int MaxRecent = 10;

    public static IReadOnlyList<string> LoadRecent(DesktopMetadataStore metadata)
    {
        var raw = metadata.GetPreference(RecentKey);
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

    /// <summary>项目打开成功后记录：规范化路径置顶，按 PathEquals 去重，保留前 10 条。</summary>
    public static void RecordOpened(DesktopMetadataStore metadata, string path)
    {
        var normalized = AppDataLayout.NormalizeProjectPath(path);
        var list = new List<string> { normalized };
        list.AddRange(LoadRecent(metadata).Where(p => !RealSessionHost.PathEquals(p, normalized)));
        if (list.Count > MaxRecent)
        {
            list.RemoveRange(MaxRecent, list.Count - MaxRecent);
        }

        metadata.SetPreference(RecentKey, JsonSerializer.Serialize(list));
    }

    /// <summary>
    /// 启动项目解析：显式 --project 优先；否则第一个磁盘上存在的最近项目；否则 null（走选择器）。
    /// </summary>
    public static string? ResolveStartupProject(string? explicitProject,
        IReadOnlyList<string> recent, Func<string, bool> directoryExists)
    {
        if (!string.IsNullOrWhiteSpace(explicitProject))
        {
            return explicitProject;
        }

        return recent.FirstOrDefault(directoryExists);
    }
}
