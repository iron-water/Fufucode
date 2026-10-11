using System;
using System.Globalization;
using Fufucode.Core;

namespace Fufucode.App.Infrastructure;

/// <summary>Preferred dimensions survive narrow windows; only rendered dimensions are clamped.</summary>
internal sealed class WorkspaceLayoutPreferences
{
    internal const string RightKey = "workspace:right-width";
    internal const string BottomKey = "workspace:bottom-height";
    public double RightWidth { get; set; } = 360;
    public double BottomHeight { get; set; } = 240;

    public void Load(DesktopMetadataStore store)
    {
        RightWidth = Read(store.GetPreference(RightKey), 360);
        BottomHeight = Read(store.GetPreference(BottomKey), 240);
    }

    public void Save(DesktopMetadataStore store)
    {
        store.SetPreference(RightKey, RightWidth.ToString(CultureInfo.InvariantCulture));
        store.SetPreference(BottomKey, BottomHeight.ToString(CultureInfo.InvariantCulture));
    }

    private static double Read(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value) && value > 0 ? value : fallback;

    public double RightFor(double available, bool drawer) =>
        Math.Clamp(RightWidth, 360, Math.Max(360, available - (drawer ? 24 : 530)));

    public double BottomFor(double available) =>
        Math.Clamp(BottomHeight, 120, Math.Max(120, Math.Min(available / 2, available - 290)));
}
