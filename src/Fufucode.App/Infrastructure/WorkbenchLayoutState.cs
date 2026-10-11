using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Fufucode.App.Infrastructure;

public enum DockZone { Left, Right, Bottom }

public sealed class WorkbenchLayoutState
{
    public static readonly string[] KnownPages = ["overview", "files", "changes", "preview", "browser", "terminal", "sidechat"];
    public Dictionary<DockZone, List<string>> Pages { get; set; } = new()
    {
        [DockZone.Left] = ["overview", "files"],
        [DockZone.Right] = ["changes", "preview", "browser"],
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

    public DockZone? Find(string id) => Pages.Where(pair => pair.Value.Contains(id)).Select(pair => (DockZone?)pair.Key).FirstOrDefault();

    public void Open(string id, DockZone target)
    {
        var zone = Find(id) ?? target;
        if (!Pages[zone].Contains(id)) Pages[zone].Add(id);
        Selected[zone] = id;
        Hidden[zone] = false;
    }

    public void Move(string id, DockZone target)
    {
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
        if (Find(id) is not { } zone) return;
        Pages[zone].Remove(id);
        if (Selected[zone] == id) Selected[zone] = Pages[zone].FirstOrDefault();
        if (Pages[zone].Count == 0) Hidden[zone] = true;
    }

    public void Reorder(string id, int index)
    {
        if (Find(id) is not { } zone) return;
        var pages = Pages[zone];
        pages.Remove(id);
        pages.Insert(Math.Clamp(index, 0, pages.Count), id);
    }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static WorkbenchLayoutState Restore(string? text, IEnumerable<string> knownPages)
    {
        WorkbenchLayoutState state;
        try { state = string.IsNullOrWhiteSpace(text) ? new() : JsonSerializer.Deserialize<WorkbenchLayoutState>(text) ?? new(); }
        catch (JsonException) { return new(); }
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
        }
        state.LeftWidth = Dimension(state.LeftWidth, 232, 200, 500);
        state.RightWidth = Dimension(state.RightWidth, 320, 32, double.MaxValue);
        state.BottomHeight = Dimension(state.BottomHeight, 240, 160, 500);
        return state;
    }

    private static double Dimension(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
