using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Threading;

namespace Fufucode.App.Infrastructure;

internal sealed record AppFontChoice(string Value, string Label);

/// <summary>中英文按字形覆盖分开选择，所有原生顶层统一使用灰阶抗锯齿。</summary>
internal static class AppTypography
{
    internal const string DefaultFamily = "fonts:FufucodeLatin#Segoe UI, fonts:FufucodeLatin#Inter, fonts:FufucodeLatin#Microsoft YaHei UI";
    private const string ChineseSample = "一是在不了有和人这中大为上个国我以要他时来用们生到作地于出就分对成会可主发年动同工也能下过子说产种面而方后多定行学法所民得经之进着等部度家电力里如水化高自二理起小物现实加量都两体制机当使点从业本性好应开合还因由其些然前外天政四日那社义事平形相全表间样与关各重新线内数正心反你明看原又么利比或但质气第向道命此变条只没结解问意建月公无系军很情者最立代想已通并提直题程展果料象员位入常文总次品式活设及管特件长求老头基资边流路级少图山统接知较将组见计别她手角期根论运指区强放决西被干做必战先回则任取据处理世车价深字体附件消息权限保存继续变更审批规划确认";
    private static readonly Lazy<(AppFontChoice[] English, AppFontChoice[] Chinese)> Choices = new(CreateChoices);
    private static readonly AppFontCollection Collection = new();
    private static FontFamily? _currentFamily;
    private static bool _initialized;

    internal static IReadOnlyList<AppFontChoice> EnglishChoices => Choices.Value.English;
    internal static IReadOnlyList<AppFontChoice> ChineseChoices => Choices.Value.Chinese;

    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        FontManager.Current.AddFontCollection(new LatinFontCollection());
        FontManager.Current.AddFontCollection(Collection);
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            var family = window.Resources.TryGetResource("AppFontFamily", window.ActualThemeVariant, out var own) && own is FontFamily selected
                ? selected : window.Owner is Window owner ? owner.FontFamily : CurrentFamily;
            var mono = window.Resources.TryGetResource("AppMonoFont", window.ActualThemeVariant, out var code) && code is FontFamily codeFamily
                ? codeFamily : MonoFamily;
            ApplyFamily(window, family, mono);
        });
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
    }

    private static FontFamily CurrentFamily => _currentFamily ??= Resolve(new WorkbenchAppearance());

    internal static AppBuilder WithAppTypography(this AppBuilder builder) => builder.With(new FontManagerOptions
    {
        DefaultFamilyName = DefaultFamily,
    });

    internal static void Apply(Window window, WorkbenchAppearance appearance)
    {
        var family = _currentFamily = Resolve(appearance);
        var mono = new[] { "Cascadia Mono", "Consolas", "DejaVu Sans Mono" }.FirstOrDefault(name =>
            AppFontCollection.TryFace(name, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _)) ?? "Inter";
        MonoFamily = Collection.Register(mono, Select(appearance.ChineseFont, true));
        if (Application.Current is { } app)
        {
            SetResources(app.Resources, family);
            if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                foreach (var open in lifetime.Windows.Where(open => !ReferenceEquals(open, window) && IsOwnedBy(open, window)).ToArray())
                    ApplyFamily(open, family, MonoFamily);
        }
        ApplyFamily(window, family, MonoFamily);
    }

    internal static FontFamily Resolve(WorkbenchAppearance appearance)
    {
        var english = Select(appearance.EnglishFont, false);
        var chinese = Select(appearance.ChineseFont, true);
        return Collection.Register(english, chinese);
    }

    private static string Select(string value, bool chinese)
    {
        if (value != "system" && (chinese ? ChineseChoices : EnglishChoices)
            .FirstOrDefault(choice => choice.Value.Equals(value, StringComparison.OrdinalIgnoreCase)) is { } selected) return selected.Value;
        var fallbacks = chinese ? AppFontCollection.ChineseFallbacks() : new[] { "Segoe UI", "Inter" };
        return fallbacks.FirstOrDefault(name => AppFontCollection.TryFace(name, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _))
            ?? (chinese ? ChineseChoices.FirstOrDefault(choice => choice.Value != "system")?.Value : null) ?? "Inter";
    }

    private static (AppFontChoice[] English, AppFontChoice[] Chinese) CreateChoices()
    {
        var english = new List<AppFontChoice> { new("system", "系统默认"), new("Inter", "Inter（内置）") };
        var chinese = new List<AppFontChoice> { new("system", "系统默认") };
        foreach (var family in FontManager.Current.SystemFonts.OrderBy(font => font.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var name = family.Name;
            if (name.StartsWith('@') || name.Equals("Inter", StringComparison.OrdinalIgnoreCase) ||
                !AppFontCollection.TryFace(name, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var face)) continue;
            var chineseCoverage = (face.CodePageCoverage & (FontCodePageCoverage.ChineseSimplified | FontCodePageCoverage.ChineseTraditional)) != 0;
            if (chineseCoverage && ChineseSample.All(character => face.CharacterToGlyphMap.ContainsGlyph(character)))
                chinese.Add(new(name, name));
            else if (!face.CharacterToGlyphMap.ContainsGlyph('中') &&
                     "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789".All(character => face.CharacterToGlyphMap.ContainsGlyph(character)))
                english.Add(new(name, name));
        }
        return (english.DistinctBy(choice => choice.Value, StringComparer.OrdinalIgnoreCase).ToArray(),
            chinese.DistinctBy(choice => choice.Value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool IsOwnedBy(Window child, Window owner)
    {
        for (var parent = child.Owner; parent is not null; parent = parent.Owner)
            if (ReferenceEquals(parent, owner)) return true;
        return false;
    }

    private static void ApplyFamily(Window window, FontFamily family, FontFamily mono)
    {
        SetResources(window.Resources, family);
        window.Resources["AppMonoFont"] = mono;
        window.Resources["AppFontMono"] = mono;
        window.FontFamily = family;
        Grayscale(window);
        foreach (var popup in window.OpenedPopups) ConfigurePopup(popup);
    }

    private static void SetResources(IResourceDictionary resources, FontFamily family)
    {
        resources["AppFontFamily"] = family;
        resources["AppFontUi"] = family;
        resources["AppFontDisplay"] = family;
        resources["AppMonoFont"] = MonoFamily;
        resources["AppFontMono"] = MonoFamily;
    }

    internal static FontFamily MonoFamily { get; private set; } = new("Cascadia Mono, Consolas, monospace");

    private static void ConfigurePopup(Popup popup)
    {
        Grayscale(popup);
        if (popup.Child is not { } child) return;
        var family = popup.PlacementTarget is { } target
            ? TextElement.GetFontFamily(TopLevel.GetTopLevel(target) ?? target) : CurrentFamily;
        var mono = popup.PlacementTarget?.TryFindResource("AppMonoFont", out var code) == true && code is FontFamily codeFamily
            ? codeFamily : MonoFamily;
        child.Resources["AppFontFamily"] = family;
        child.Resources["AppMonoFont"] = mono;
        TextElement.SetFontFamily(child, family);
        if (TopLevel.GetTopLevel(child) is { } root)
        {
            TextElement.SetFontFamily(root, family);
            Grayscale(root);
        }
        Grayscale(child);
    }

    private static void Grayscale(Visual visual) => TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Antialias);
}
