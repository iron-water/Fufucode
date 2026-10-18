using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Fufucode.App.Features.Skins;
using Fufucode.App.Infrastructure;

namespace Fufucode.App;

public partial class MainWindow
{
    private readonly Dictionary<Window, SkinLease> _windowSkins = new();
    // These direct values are owned by ApplyCustomization. Optional skin dictionaries must win over them.
    private static readonly string[] CustomAccentKeys =
    [
        "SystemAccentColor", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "AccentFillColorDefaultBrush", "SystemControlBackgroundAccentBrush", "SystemControlForegroundAccentBrush",
        "SystemControlHighlightAccentBrush", "SystemControlHighlightAltAccentBrush", "SystemControlRevealFocusVisualBrush",
        "TextControlBorderBrushFocused", "TabItemHeaderSelectedPipeFill", "AccentButtonBackground",
        "AccentTextFillColorPrimaryBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
        "AccentButtonForeground", "AppUserBubble", "AppAccent", "AppAccentText", "AppAccentForeground", "AppAccentSubtle"
    ];

    private void ApplyWindowSkin(Window target, WorkbenchAppearance appearance)
    {
        var resolution = appearance.SkinResolution;
        // strip 注入键=让皮肤令牌字典的固定色赢（Metro/M3/Aero/Luna 锁色皮肤需要）。
        // FollowsSystemAccent 皮肤（LiquidGlass）：保留本地注入键——本地 Resources 直接条目
        // 优先于 merged 字典，用户/系统 accent 继续生效，皮肤令牌只贡献中性色与形状。
        if (resolution.Skin != SkinId.Fluent && !resolution.Definition.FollowsSystemAccent)
            foreach (var key in CustomAccentKeys) target.Resources.Remove(key);
        if (_windowSkins.TryGetValue(target, out var previous) && previous.Skin == resolution.Skin) return;
        ReleaseWindowSkin(target);
        // Theme is owned by the existing appearance flow, including Default/system inheritance.
        _windowSkins[target] = SkinLease.Apply(target, resolution);
    }

    private void ReleaseWindowSkin(Window target)
    {
        if (_windowSkins.Remove(target, out var lease)) lease.Dispose();
    }
}
