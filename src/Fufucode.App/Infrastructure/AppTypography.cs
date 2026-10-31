using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;

namespace Fufucode.App.Infrastructure;

internal sealed record AppFontChoice(string Value, string Label);

/// <summary>中英文按字形覆盖分开选择，所有原生顶层统一使用灰阶抗锯齿。</summary>
internal static class AppTypography
{
    internal const string DefaultFamily = "fonts:FufucodeLatin#Segoe UI Variable Text, fonts:FufucodeLatin#Segoe UI, fonts:FufucodeLatin#Inter, Microsoft YaHei UI";
    private static readonly Lazy<(AppFontChoice[] English, AppFontChoice[] Chinese)> Choices = new(CreateChoices);
    private static bool _initialized;

    internal static IReadOnlyList<AppFontChoice> EnglishChoices => Choices.Value.English;
    internal static IReadOnlyList<AppFontChoice> ChineseChoices => Choices.Value.Chinese;

    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Grayscale(window));
        Control.LoadedEvent.AddClassHandler<TopLevel>((topLevel, args) =>
        {
            if (ReferenceEquals(args.Source, topLevel)) Grayscale(topLevel);
        });
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, change) =>
        {
            if (!change.GetNewValue<bool>()) return;
            ConfigurePopup(popup);
            Dispatcher.UIThread.Post(() => { if (popup.IsOpen) ConfigurePopup(popup); }, DispatcherPriority.Loaded);
        });
        FontManager.Current.AddFontCollection(new LatinFontCollection());
    }

    internal static AppBuilder WithAppTypography(this AppBuilder builder) => builder.With(new FontManagerOptions
    {
        DefaultFamilyName = DefaultFamily,
    });

    internal static void Apply(Window window, WorkbenchAppearance appearance)
    {
        var family = Resolve(appearance);
        window.Resources["AppFontFamily"] = family;
        window.FontFamily = family;
        if (Application.Current is { } app) app.Resources["AppFontFamily"] = family;
        Grayscale(window);
    }

    internal static FontFamily Resolve(WorkbenchAppearance appearance)
    {
        var english = Selected(appearance.EnglishFont, false,
            "fonts:FufucodeLatin#Segoe UI Variable Text, fonts:FufucodeLatin#Segoe UI, fonts:FufucodeLatin#Inter");
        var chinese = Selected(appearance.ChineseFont, true, "Microsoft YaHei UI");
        return new FontFamily(english + ", " + chinese);
    }

    private static string Selected(string value, bool chinese, string fallback)
    {
        if (value == "system") return fallback;
        var choices = chinese ? ChineseChoices : EnglishChoices;
        var choice = choices.FirstOrDefault(choice => choice.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (choice is null) return fallback;
        return chinese ? choice.Value : LatinFontCollection.CollectionKey + "#" + choice.Value;
    }

    private static (AppFontChoice[] English, AppFontChoice[] Chinese) CreateChoices()
    {
        var english = new List<AppFontChoice> { new("system", "系统默认"), new("Inter", "Inter（内置）") };
        var chinese = new List<AppFontChoice> { new("system", "系统默认") };
        foreach (var family in FontManager.Current.SystemFonts.OrderBy(font => font.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var name = family.Name;
            if (name.StartsWith('@') || name.Equals("Inter", StringComparison.OrdinalIgnoreCase) ||
                !FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var face)) continue;
            if ("汉漢中文".Any(character => face.CharacterToGlyphMap.ContainsGlyph(character))) chinese.Add(new(name, name));
            // 英文候选不含汉字，避免抢走用户单独选定的中文字体。
            else if (face.CharacterToGlyphMap.ContainsGlyph('A') && face.CharacterToGlyphMap.ContainsGlyph('a')) english.Add(new(name, name));
        }
        return (english.DistinctBy(choice => choice.Value, StringComparer.OrdinalIgnoreCase).ToArray(),
            chinese.DistinctBy(choice => choice.Value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void ConfigurePopup(Popup popup)
    {
        Grayscale(popup);
        if (popup.Child is { } child)
        {
            Grayscale(child);
            if (TopLevel.GetTopLevel(child) is { } root) Grayscale(root);
        }
    }

    private static void Grayscale(Visual visual) => TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Antialias);
}
