using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Fufucode.App.Features.Skins.Aero;

namespace Fufucode.App.Features.Skins;

/// <summary>
/// 一次皮肤应用租约：只向目标 Control/TopLevel 追加本租约自己的资源字典与限定样式，
/// 释放时按引用移除，绝不清空他人的 Resources/Styles。顶层窗口额外接管/恢复
/// RequestedThemeVariant；非顶层 Control 不伪造主题作用域，按宿主实际变体解析。
/// 多个窗口各自持有租约即天然隔离；连续换肤请"先释放旧租约、再应用新租约"。
/// </summary>
public sealed class SkinLease : IDisposable
{
    private readonly Control _target;
    private readonly ResourceDictionary? _tokenDictionary;
    private readonly IStyle? _scopedStyles;
    private readonly ThemeVariant? _previousVariant;
    private bool _disposed;
    private ResourceDictionary? _captionDictionary;
    private readonly List<AeroCaptionAdapter> _captions = new();
    private readonly List<LiquidGlass.LiquidGlassAdapter> _glassAdapters = new();
    private readonly List<LiquidGlass.LiquidGlassMaterialAdapter> _glassMaterials = new();
    private IDisposable? _materialLoadedSubscription;

    /// <summary>后加载的内容卡（切换设置分类、虚拟化列表）在 Loaded 时补挂材质。</summary>
    private void OnBorderLoaded(Border border)
    {
        if (_disposed || !LiquidGlass.LiquidGlassMaterialAdapter.IsMaterialHost(border)) return;
        if (!ReferenceEquals(TopLevel.GetTopLevel(border), _target)) return;
        _glassMaterials.RemoveAll(m => TopLevel.GetTopLevel(m.Host) is null && !ReferenceEquals(m.Host, border));
        if (_glassMaterials.Any(m => ReferenceEquals(m.Host, border))) return;
        if (LiquidGlass.LiquidGlassMaterialAdapter.TryAttach(border) is { } material)
            _glassMaterials.Add(material);
    }

    private SkinLease(
        Control target,
        SkinResolution resolution,
        ThemeVariant requestedVariant,
        ResourceDictionary? tokenDictionary,
        IStyle? scopedStyles,
        bool ownsVariantScope,
        ThemeVariant? previousVariant,
        bool variantApplied,
        string variantNote)
    {
        _target = target;
        _tokenDictionary = tokenDictionary;
        _scopedStyles = scopedStyles;
        _previousVariant = previousVariant;
        Resolution = resolution;
        RequestedVariant = requestedVariant;
        OwnsVariantScope = ownsVariantScope;
        VariantApplied = variantApplied;
        VariantNote = variantNote;
    }

    public SkinResolution Resolution { get; }

    public SkinId Skin => Resolution.Skin;

    public ThemeVariant RequestedVariant { get; }

    /// <summary>是否接管了目标自身的 RequestedThemeVariant（仅 TopLevel 目标为 true）。</summary>
    public bool OwnsVariantScope { get; }

    /// <summary>请求的变体是否按请求生效；Control 目标在宿主变体不一致时为 false，不假装成功。</summary>
    public bool VariantApplied { get; }

    public string VariantNote { get; }

    public Control Target => _target;

    public bool HasTokenOverrides => _tokenDictionary is not null;

    public bool HasScopedStyles => _scopedStyles is not null;

    public bool IsActive => !_disposed;

    /// <summary>应用皮肤并返回租约；未知键由目录解释性回退 fluent（租约仍有效，只是不追加覆盖）。</summary>
    public static SkinLease Apply(Control target, SkinResolution resolution, ThemeVariant? variant = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        var requestedVariant = variant ?? ThemeVariant.Default;
        var definition = resolution.Definition;

        ResourceDictionary? tokens = null;
        if (definition.TokenDictionaryUri is { } tokenUri)
        {
            tokens = AvaloniaXamlLoader.Load(new Uri(tokenUri)) as ResourceDictionary
                ?? throw new InvalidOperationException($"皮肤资源文件不是 ResourceDictionary：{tokenUri}");
            tokens.MergedDictionaries.Add(SkinIconSet.Create(resolution.Skin));
            tokens["AppSkinId"] = resolution.Skin;
            target.Resources.MergedDictionaries.Add(tokens);
        }

        IStyle? scopedStyles = null;
        if (definition.ScopedStylesUri is { } stylesUri)
        {
            scopedStyles = new StyleInclude(new Uri("avares://Fufucode.App/"))
            {
                Source = new Uri(stylesUri),
            };
            target.Styles.Add(scopedStyles);
        }

        var ownsVariantScope = false;
        var variantApplied = false;
        ThemeVariant? previousVariant = null;
        string variantNote;
        if (requestedVariant == ThemeVariant.Default)
        {
            variantNote = "未指定主题变体：沿用宿主当前变体。";
        }
        else if (target is TopLevel topLevel)
        {
            previousVariant = topLevel.RequestedThemeVariant;
            topLevel.RequestedThemeVariant = requestedVariant;
            ownsVariantScope = true;
            variantApplied = true;
            variantNote = $"顶层目标接管 RequestedThemeVariant（原值 {DescribeVariant(previousVariant)}，释放时恢复）。";
        }
        else
        {
            var actual = target.ActualThemeVariant;
            variantApplied = actual == requestedVariant;
            variantNote = variantApplied
                ? $"Control 目标不拥有主题作用域：宿主实际变体 {actual} 与请求一致，覆盖层按宿主变体解析。"
                : $"Control 目标不拥有主题作用域：宿主实际变体 {actual} 与请求 {requestedVariant} 不一致，" +
                  "覆盖层按宿主变体解析（不伪造主题、不隐藏差异）。";
        }

        var lease = new SkinLease(
            target, resolution, requestedVariant, tokens, scopedStyles,
            ownsVariantScope, previousVariant, variantApplied, variantNote);
        if (resolution.Skin == SkinId.Aero)
        {
            try
            {
                lease._captionDictionary = AvaloniaXamlLoader.Load(new Uri(AeroSkin.CaptionDictionaryUri)) as ResourceDictionary
                    ?? throw new InvalidOperationException("Aero caption resources are not a dictionary.");
                target.Resources.MergedDictionaries.Add(lease._captionDictionary);
                lease.AttachExistingCaptions();
                if (target is Window window)
                {
                    window.Opened += lease.OnWindowOpened;
                    window.PropertyChanged += lease.OnWindowPropertyChanged;
                }
            }
            catch { lease.Dispose(); throw; }
        }
        else if (resolution.Skin == SkinId.LiquidGlass)
        {
            // LiquidGlass：令牌/限定样式已按通用路径挂载，这里追加 chrome 玻璃包覆 + 窗底回落。
            // 令牌把 AppMicaPageBackground 顶成 Transparent，让 AcrylicBlur 的窗后桌面模糊透进
            // 玻璃面板缝（参考 macOS 玻璃浮在壁纸上的形态）；桌面拒绝 AcrylicBlur/无 GPU 时窗底
            // 得回落到不透明 iOS 底，否则整窗透出桌面。SyncGlassFallback 按 ActualTransparencyLevel
            // 直接写本地 Resources["AppMicaPageBackground"]——本地条目优先于合并字典里的令牌值。
            try
            {
                lease.InstallGlassWallpaper();
                lease._materialLoadedSubscription = Control.LoadedEvent.AddClassHandler<Border>(
                    (border, _) => lease.OnBorderLoaded(border));
                lease.AttachExistingGlassChrome();
                lease.SyncGlassFallback();
                if (target is Window window)
                {
                    window.Opened += lease.OnWindowOpened;
                    window.PropertyChanged += lease.OnWindowPropertyChanged;
                    // 系统高对比/透明度偏好变化（iOS "Reduce Transparency/Contrast" 对应物）
                    // → 重新 SyncFrom 调弱玻璃面，不然用户切了辅助设置皮肤不回读。
                    if (Application.Current?.PlatformSettings is { } platform)
                    {
                        lease._platformColorValuesChanged = (_, _) => lease.SyncGlass();
                        platform.ColorValuesChanged += lease._platformColorValuesChanged;
                        lease._platformColorValuesSource = platform;
                    }
                }
            }
            catch { lease.Dispose(); throw; }
        }
        return lease;
    }

    internal void AttachCaption(Border caption)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SkinLease));
        if (Skin == SkinId.LiquidGlass)
        {
            // SkinDialog 等代码路径新建的 chromeBar 经此进玻璃（caption 语义：更强折射+无投影）。
            if (_glassAdapters.Any(adapter => ReferenceEquals(adapter.Host, caption))) return;
            _glassAdapters.Add(LiquidGlass.LiquidGlassAdapter.Attach(caption, isCaption: true));
            SyncGlass();
            return;
        }
        if (Skin != SkinId.Aero || _captions.Any(adapter => ReferenceEquals(adapter.Caption, caption))) return;
        var adapter = AeroCaptionAdapter.Attach(caption);
        if (adapter.Buttons.Count == 1) adapter.MarkSingle();
        _captions.Add(adapter);
        SyncCaptions();
    }

    /// <summary>给后出现的 chrome 面板（浮动窗工具栏/对话框 caption）挂玻璃，外部可直接调用。</summary>
    internal void AttachGlassChrome(Border host, bool? isCaption = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SkinLease));
        if (Skin != SkinId.LiquidGlass) return;
        if (LiquidGlass.LiquidGlassAdapter.IsAttached(host)) return;
        _glassAdapters.Add(LiquidGlass.LiquidGlassAdapter.Attach(
            host, isCaption ?? LiquidGlass.LiquidGlassAdapter.IsCaptionHost(host)));
        SyncGlass();
    }

    private void AttachExistingCaptions()
    {
        foreach (var caption in _target.GetLogicalDescendants().OfType<Border>()
                     .Where(border => (border.Name == "CustomCaption" || border.Classes.Contains("chromeBar")) &&
                         border.GetLogicalDescendants().OfType<Fufucode.App.Views.CaptionAssetButton>().Any()))
            AttachCaption(caption);
    }

    private void SyncCaptions()
    {
        if (_target is Window window)
            foreach (var adapter in _captions) adapter.SyncFrom(window, hasBlur: false);
    }

    /// <summary>
    /// 页面切换后重扫 chrome 宿主：设置页等页面在租约挂载之后才进 PageHost，
    /// 只在 Apply/Opened 扫一次会让它们的导航宿主永远不成玻璃。
    /// </summary>
    internal void RescanGlassChrome()
    {
        if (_disposed || Skin != SkinId.LiquidGlass) return;
        _glassAdapters.RemoveAll(adapter => !adapter.IsActive);
        AttachExistingGlassChrome();
    }

    private void AttachExistingGlassChrome()
    {
        // 物化：Attach 原位替换宿主为 LiquidGlassSurface，迭代中改树会抛集合已修改。
        foreach (var border in _target.GetLogicalDescendants().OfType<Border>().ToArray())
        {
            if (!LiquidGlass.LiquidGlassAdapter.IsGlassHost(border)) continue;
            if (LiquidGlass.LiquidGlassAdapter.IsAttached(border)) continue;
            try
            {
                _glassAdapters.Add(LiquidGlass.LiquidGlassAdapter.Attach(
                    border, LiquidGlass.LiquidGlassAdapter.IsCaptionHost(border)));
            }
            catch (InvalidOperationException)
            {
                // 宿主暂时不能包（父容器非 Panel 等）：跳过，不算失败。
            }
        }
        // 内容层卡片（设置页行卡/内容卡）：Standard Material（模糊+鲜明度），不移位。
        foreach (var border in _target.GetVisualDescendants().OfType<Border>().ToArray())
        {
            if (!LiquidGlass.LiquidGlassMaterialAdapter.IsMaterialHost(border)) continue;
            if (_glassMaterials.Any(m => ReferenceEquals(m.Host, border))) continue;
            if (LiquidGlass.LiquidGlassMaterialAdapter.TryAttach(border) is { } material)
                _glassMaterials.Add(material);
        }
        SyncGlass();
    }

    private void SyncGlass()
    {
        if (_target is Window window)
            foreach (var adapter in _glassAdapters) adapter.SyncFrom(window);
    }

    /// <summary>
    /// 按平台透明级别切换窗底：AcrylicBlur/Blur 生效时窗底透（令牌 Transparent 放行），
    /// 其余级别（含 None/软渲/远程桌面）用本地条目把 AppMicaPageBackground 顶回不透明
    /// iOS 底。LiquidGlassFallbackPageBackground 存实色回落值；本键只在本皮肤租约期写入，
    /// Dispose 时移除条目即还原。
    /// </summary>
    private void SyncGlassFallback()
    {
        if (_target is not Window window || Skin != SkinId.LiquidGlass) return;
        var level = window.ActualTransparencyLevel;
        var hasPlatformBlur = level == WindowTransparencyLevel.AcrylicBlur
            || level == WindowTransparencyLevel.Blur
            || level == WindowTransparencyLevel.Mica
            || level == WindowTransparencyLevel.Transparent;
        const string key = "AppMicaPageBackground";
        if (hasPlatformBlur)
        {
            window.Resources.Remove(key); // 回落条目拿走，令牌 Transparent 生效
            _glassFallbackInstalled = false;
            return;
        }
        if (window.TryFindResource("LiquidGlassFallbackPageBackground", out var fallback)
            && fallback is Avalonia.Media.IBrush brush)
        {
            window.Resources[key] = brush;
            _glassFallbackInstalled = true;
        }
    }

    private bool _glassFallbackInstalled;
    private LiquidGlass.LiquidGlassWallpaper? _glassWallpaper;
    private Panel? _glassWallpaperHost;

    /// <summary>在窗口根 Grid（ChromeRoot）最底层插入应用内壁纸，给玻璃提供可采样的色差。</summary>
    private void InstallGlassWallpaper()
    {
        if (_target is not Window { Content: Panel root }) return;
        var wallpaper = new LiquidGlass.LiquidGlassWallpaper();
        if (root is Grid grid && grid.RowDefinitions.Count > 0)
            Grid.SetRowSpan(wallpaper, grid.RowDefinitions.Count);
        if (root is Grid g2 && g2.ColumnDefinitions.Count > 0)
            Grid.SetColumnSpan(wallpaper, g2.ColumnDefinitions.Count);
        wallpaper.ZIndex = -100;
        root.Children.Insert(0, wallpaper);
        _glassWallpaper = wallpaper;
        _glassWallpaperHost = root;
    }
    private Avalonia.Platform.IPlatformSettings? _platformColorValuesSource;
    private EventHandler<Avalonia.Platform.PlatformColorValues>? _platformColorValuesChanged;

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        AttachExistingCaptions();
        if (Skin == SkinId.LiquidGlass) AttachExistingGlassChrome();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == Window.IsActiveProperty)
        {
            SyncCaptions();
            SyncGlass();
        }
        else if (Skin == SkinId.LiquidGlass && e.Property == TopLevel.ActualTransparencyLevelProperty)
        {
            // 平台模糊级别变化 → 玻璃面 SurfaceColor 强弱 + 窗底回落一起跟着调。
            SyncGlass();
            SyncGlassFallback();
        }
    }

    /// <summary>幂等释放：只移除本租约加入的字典/样式引用并恢复本租约改过的变体。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_target is Window window)
        {
            window.Opened -= OnWindowOpened;
            window.PropertyChanged -= OnWindowPropertyChanged;
        }
        foreach (var adapter in _captions) adapter.Dispose();
        _captions.Clear();
        foreach (var adapter in _glassAdapters) adapter.Dispose();
        _glassAdapters.Clear();
        _materialLoadedSubscription?.Dispose();
        _materialLoadedSubscription = null;
        foreach (var material in _glassMaterials) material.Dispose();
        _glassMaterials.Clear();
        if (_platformColorValuesSource is not null && _platformColorValuesChanged is not null)
        {
            _platformColorValuesSource.ColorValuesChanged -= _platformColorValuesChanged;
            _platformColorValuesChanged = null;
            _platformColorValuesSource = null;
        }
        // 我安装的窗底回落条目（令牌未删 AppMicaPageBackground，本地条目遮蔽令牌值）—— 拿走后还原令牌。
        if (_glassWallpaper is not null && _glassWallpaperHost is not null)
            _glassWallpaperHost.Children.Remove(_glassWallpaper);
        _glassWallpaper = null;
        _glassWallpaperHost = null;
        if (_glassFallbackInstalled) _target.Resources.Remove("AppMicaPageBackground");
        _glassFallbackInstalled = false;
        if (_captionDictionary is not null) _target.Resources.MergedDictionaries.Remove(_captionDictionary);

        if (_tokenDictionary is not null)
            _target.Resources.MergedDictionaries.Remove(_tokenDictionary);

        if (_scopedStyles is not null)
            _target.Styles.Remove(_scopedStyles);

        if (OwnsVariantScope && _target is TopLevel topLevel)
            topLevel.RequestedThemeVariant = _previousVariant;
    }

    private static string DescribeVariant(ThemeVariant? variant) =>
        variant is null || variant == ThemeVariant.Default ? "Default" : variant.Key?.ToString() ?? variant.ToString();
}

/// <summary>面向组合根/窗口接线的便捷入口：解析键后应用皮肤。</summary>
public static class SkinHostExtensions
{
    /// <summary>按持久化键应用皮肤（未知键回退 fluent，并把回退原因放进租约 Resolution）。</summary>
    public static SkinLease ApplySkin(this Control target, string? storageKey, ThemeVariant? variant = null) =>
        SkinLease.Apply(target, SkinCatalog.Resolve(storageKey), variant);

    public static SkinLease ApplySkin(this Control target, SkinId skin, ThemeVariant? variant = null) =>
        SkinLease.Apply(target, SkinCatalog.Resolve(SkinCatalog.Get(skin).StorageKey), variant);
}
