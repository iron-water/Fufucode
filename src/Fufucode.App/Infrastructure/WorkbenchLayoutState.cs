using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Fufucode.App.Infrastructure;

public enum DockZone { Left, Right, Bottom }

public sealed class FloatingDockState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DockZone HomeZone { get; set; }
    public List<string> Pages { get; set; } = [];
    public Dictionary<string, int> HomeIndices { get; set; } = new(StringComparer.Ordinal);
    public string? Selected { get; set; }
    public bool Hidden { get; set; }
    public bool Topmost { get; set; }
    public double Width { get; set; } = 640;
    public double Height { get; set; } = 480;
    public int? X { get; set; }
    public int? Y { get; set; }
}

public sealed class WorkbenchLayoutState
{
    public static readonly string[] KnownPages = ["overview", "files", "changes", "browser", "library", "local-review", "automation", "terminal", "sidechat", "notifications", "usage", "perf"];
    public Dictionary<DockZone, List<string>> Pages { get; set; } = new()
    {
        [DockZone.Left] = ["overview", "files"],
        [DockZone.Right] = ["changes", "browser"],
        [DockZone.Bottom] = ["terminal"],
    };
    public Dictionary<DockZone, string?> Selected { get; set; } = new()
    {
        [DockZone.Left] = "overview", [DockZone.Right] = "changes", [DockZone.Bottom] = "terminal",
    };
    public Dictionary<DockZone, bool> Hidden { get; set; } = new()
    {
        [DockZone.Left] = false, [DockZone.Right] = false, [DockZone.Bottom] = false,
    };
    public double LeftWidth { get; set; } = 232;
    public double RightWidth { get; set; } = 320;
    public double BottomHeight { get; set; } = 240;
    public HashSet<string> PinnedPages { get; set; } = new(StringComparer.Ordinal);
    public List<FloatingDockState> Floating { get; set; } = [];
    public bool NotificationMigrationApplied { get; set; }

    public DockZone? Find(string id) => Pages.Where(pair => pair.Value.Contains(id)).Select(pair => (DockZone?)pair.Key).FirstOrDefault();
    public FloatingDockState? FindFloating(string id) => Floating.FirstOrDefault(item => item.Pages.Contains(id));

    public void Open(string id, DockZone target)
    {
        if (FindFloating(id) is { } floating)
        { floating.Selected = id; floating.Hidden = false; return; }
        var zone = Find(id) ?? target;
        if (!Pages[zone].Contains(id)) Pages[zone].Add(id);
        Selected[zone] = id;
        Hidden[zone] = false;
    }

    public void Move(string id, DockZone target)
    {
        RemoveFloatingPage(id);
        if (Find(id) is { } from)
        {
            Pages[from].Remove(id);
            if (Selected[from] == id) Selected[from] = Pages[from].FirstOrDefault();
            if (Pages[from].Count == 0) Hidden[from] = true;
        }
        Open(id, target);
    }

    public void Close(string id)
    {
        if (PinnedPages.Contains(id)) return;
        if (FindFloating(id) is not null) { RemoveFloatingPage(id); return; }
        if (Find(id) is not { } zone) return;
        Pages[zone].Remove(id);
        if (Selected[zone] == id) Selected[zone] = Pages[zone].FirstOrDefault();
        if (Pages[zone].Count == 0) Hidden[zone] = true;
    }

    public void Reorder(string id, int index)
    {
        var pages = FindFloating(id)?.Pages ?? (Find(id) is { } zone ? Pages[zone] : null);
        if (pages is null) return;
        pages.Remove(id);
        pages.Insert(Math.Clamp(index, 0, pages.Count), id);
    }

    public FloatingDockState? FloatPage(string id)
    {
        var previous = FindFloating(id);
        if (previous?.Pages.Count == 1) { previous.Hidden = false; return previous; }
        var zone = Find(id) ?? previous?.HomeZone;
        if (zone is null) return null;
        var index = previous is null ? Pages[zone.Value].IndexOf(id) : previous.HomeIndices.GetValueOrDefault(id);
        if (previous is not null) RemoveFloatingPage(id);
        else RemoveDockedPage(id, zone.Value);
        var floating = new FloatingDockState { HomeZone = zone.Value, Pages = [id], Selected = id };
        floating.HomeIndices[id] = index;
        Floating.Add(floating);
        return floating;
    }

    public FloatingDockState? FloatRegion(DockZone zone)
    {
        if (Pages[zone].Count == 0) return null;
        var floating = new FloatingDockState { HomeZone = zone, Pages = Pages[zone].ToList(), Selected = Selected[zone] };
        for (var index = 0; index < floating.Pages.Count; index++) floating.HomeIndices[floating.Pages[index]] = index;
        Pages[zone].Clear(); Selected[zone] = null; Hidden[zone] = true;
        Floating.Add(floating);
        return floating;
    }

    public void DockFloating(string floatingId, DockZone? destination = null)
    {
        if (Floating.FirstOrDefault(item => item.Id == floatingId) is not { } floating) return;
        Floating.Remove(floating);
        var target = destination ?? floating.HomeZone;
        var insertAt = destination is null ? Math.Clamp(floating.HomeIndices.Values.DefaultIfEmpty(0).Min(), 0, Pages[target].Count) : Pages[target].Count;
        foreach (var id in floating.Pages)
        {
            Pages[target].Insert(insertAt++, id);
        }
        Selected[target] = floating.Selected ?? floating.Pages.FirstOrDefault();
        Hidden[target] = false;
    }

    public void OpenInFloating(string id, string floatingId)
    {
        if (Floating.FirstOrDefault(item => item.Id == floatingId) is not { } target) return;
        if (!target.Pages.Contains(id))
        {
            if (Find(id) is { } zone) RemoveDockedPage(id, zone);
            else RemoveFloatingPage(id);
            target.HomeIndices[id] = target.Pages.Count;
            target.Pages.Add(id);
        }
        target.Selected = id; target.Hidden = false;
    }

    private void RemoveDockedPage(string id, DockZone zone)
    {
        Pages[zone].Remove(id);
        if (Selected[zone] == id) Selected[zone] = Pages[zone].FirstOrDefault();
        if (Pages[zone].Count == 0) Hidden[zone] = true;
    }

    private void RemoveFloatingPage(string id)
    {
        if (FindFloating(id) is not { } floating) return;
        floating.Pages.Remove(id); floating.HomeIndices.Remove(id);
        if (floating.Selected == id) floating.Selected = floating.Pages.FirstOrDefault();
        if (floating.Pages.Count == 0) Floating.Remove(floating);
    }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static WorkbenchLayoutState Restore(string? text, IEnumerable<string> knownPages)
    {
        WorkbenchLayoutState state;
        try { state = string.IsNullOrWhiteSpace(text) ? new() : JsonSerializer.Deserialize<WorkbenchLayoutState>(text) ?? new(); }
        catch (JsonException) { return new(); }
        state.MigrateLegacyPreview();
        var known = knownPages.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        state.PinnedPages = (state.PinnedPages ?? new()).Where(known.Contains).ToHashSet(StringComparer.Ordinal);
        if (state.Pages is null || state.Selected is null || state.Hidden is null) return new();
        foreach (var zone in Enum.GetValues<DockZone>())
        {
            if (!state.Pages.TryGetValue(zone, out var pages) || pages is null) return new();
            state.Pages[zone] = pages.Where(id => id is not null && known.Contains(id) && seen.Add(id)).ToList();
            if (!state.Selected.TryGetValue(zone, out var selected) || selected is null || !state.Pages[zone].Contains(selected))
                state.Selected[zone] = state.Pages[zone].FirstOrDefault();
            state.Hidden.TryAdd(zone, false);
            if (state.Pages[zone].Count == 0) state.Hidden[zone] = true;
        }
        var windowIds = new HashSet<string>(StringComparer.Ordinal);
        state.Floating = (state.Floating ?? []).Where(item => item is not null &&
            !string.IsNullOrWhiteSpace(item.Id) && item.Id.Length <= 80 && windowIds.Add(item.Id) && Enum.IsDefined(item.HomeZone)).ToList();
        foreach (var floating in state.Floating)
        {
            floating.Pages = (floating.Pages ?? []).Where(id => id is not null && known.Contains(id) && seen.Add(id)).ToList();
            floating.HomeIndices = (floating.HomeIndices ?? new()).Where(pair => floating.Pages.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value, 0, known.Count), StringComparer.Ordinal);
            if (floating.Selected is null || !floating.Pages.Contains(floating.Selected)) floating.Selected = floating.Pages.FirstOrDefault();
            floating.Width = Dimension(floating.Width, 640, 320, 2560);
            floating.Height = Dimension(floating.Height, 480, 200, 1600);
        }
        state.Floating.RemoveAll(item => item.Pages.Count == 0);
        state.LeftWidth = Dimension(state.LeftWidth, 232, 200, 500);
        state.RightWidth = Dimension(state.RightWidth, 320, 32, double.MaxValue);
        state.BottomHeight = Dimension(state.BottomHeight, 240, 160, 500);
        state.Normalize();
        return state;
    }

    public void Normalize()
    {
        MigrateLegacyPreview();
        var known = KnownPages.ToHashSet(StringComparer.Ordinal);
        PinnedPages.RemoveWhere(id => !known.Contains(id) || id == "preview");
        foreach (var zone in Enum.GetValues<DockZone>())
        {
            if (!Pages.TryGetValue(zone, out var pages) || pages is null) continue;
            pages.RemoveAll(id => id is null || !known.Contains(id) || id == "preview");
            if (Selected.TryGetValue(zone, out var sel) && (sel is null || !pages.Contains(sel) || sel == "preview"))
                Selected[zone] = pages.FirstOrDefault();
            if (pages.Count == 0) Hidden[zone] = true;
        }
        foreach (var fl in Floating)
        {
            fl.Pages.RemoveAll(id => id is null || !known.Contains(id) || id == "preview");
            if (fl.Selected is null || !fl.Pages.Contains(fl.Selected) || fl.Selected == "preview")
                fl.Selected = fl.Pages.FirstOrDefault();
        }
        Floating.RemoveAll(fl => fl.Pages.Count == 0);
    }

    /// <summary>
    /// 迁移历史布局中的 preview 状态：将固定的 preview 迁移为 files，
    /// 浮动窗口中的 preview 保留窗口坐标/尺寸/置顶并替换为 files，
    /// 停靠区中的 preview 映射为 files 并保留选中状态，避免直接丢弃上下文或销毁浮窗。
    /// </summary>
    public void MigrateLegacyPreview()
    {
        if (PinnedPages.Remove("preview"))
            PinnedPages.Add("files");

        var floatingHasPreview = false;
        if (Floating is not null)
        {
            foreach (var fl in Floating)
            {
                if (fl.Pages is null) continue;
                for (var i = 0; i < fl.Pages.Count; i++)
                {
                    if (fl.Pages[i] == "preview")
                    {
                        fl.Pages[i] = "files";
                        floatingHasPreview = true;
                    }
                }
                if (fl.Selected == "preview") fl.Selected = "files";
                if (fl.HomeIndices is not null && fl.HomeIndices.Remove("preview", out var homeIdx) && !fl.HomeIndices.ContainsKey("files"))
                    fl.HomeIndices["files"] = homeIdx;
                fl.Pages = fl.Pages.Distinct(StringComparer.Ordinal).ToList();
            }
        }

        if (Pages is not null)
        {
            foreach (var zone in Enum.GetValues<DockZone>())
            {
                if (!Pages.TryGetValue(zone, out var pages) || pages is null) continue;
                var previewIndex = pages.IndexOf("preview");
                if (previewIndex >= 0)
                {
                    var filesInZone = pages.Contains("files");
                    var filesInOtherZone = Find("files") is { } other && other != zone;
                    if (!floatingHasPreview && !filesInZone && !filesInOtherZone)
                        pages[previewIndex] = "files";
                    else
                        pages.RemoveAt(previewIndex);
                }
                if (Selected.TryGetValue(zone, out var sel) && sel == "preview")
                {
                    Selected[zone] = pages.Contains("files") ? "files" : pages.FirstOrDefault();
                }
            }
        }
    }

    private static double Dimension(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
