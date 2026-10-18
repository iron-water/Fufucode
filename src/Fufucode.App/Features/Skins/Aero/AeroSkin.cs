using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace Fufucode.App.Features.Skins.Aero;

/// <summary>
/// Aero（Vista）皮肤模块的独立应用/释放入口。
/// 只向目标 Control/TopLevel 追加本模块自己的两个资源字典（AeroTokens / AeroCaption）与一个限定样式（AeroStyles），
/// 释放时按引用移除；不修改既有 SkinCatalog.cs / SkinLease.cs，不清空他人的资源或样式。
/// 顶层窗口可选接管 RequestedThemeVariant（释放恢复原值）；非顶层 Control 不伪造主题作用域。
/// 组合根接线（目录 Aero 项、主窗/浮动窗公共入口、设置持久化）由 M 负责，本模块是待接线模块。
/// </summary>
public static class AeroSkin
{
    public const string TokenDictionaryUri = "avares://Fufucode.App/Styles/Skins/Aero/AeroTokens.axaml";
    public const string CaptionDictionaryUri = "avares://Fufucode.App/Styles/Skins/Aero/AeroCaption.axaml";
    public const string ScopedStylesUri = "avares://Fufucode.App/Styles/Skins/Aero/AeroStyles.axaml";

    /// <summary>应用 Aero 资源与限定样式；可选同时挂接标题栏适配器（生产 CustomCaption）。</summary>
    public static AeroLease Apply(Control target, Border? caption = null, ThemeVariant? variant = null) =>
        AeroLease.ApplyCore(target, variant, TokenDictionaryUri, CaptionDictionaryUri, ScopedStylesUri, caption);

    /// <summary>目标是否已挂有本模块的标题栏字典（按资源内容判断，不依赖对象身份）。</summary>
    public static bool IsAppliedTo(Control target)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (var dictionary in target.Resources.MergedDictionaries)
            if (dictionary.TryGetResource("AeroCaptionButtonTheme", ThemeVariant.Default, out _)) return true;
        return false;
    }
}

/// <summary>
/// 一次 Aero 应用租约。与既有 SkinLease 语义对齐（先释放旧租约、再应用新租约；Dispose 幂等），
/// 额外持有 AeroCaptionAdapter，释放时还原标题栏类与按钮 Theme。
/// 失败回滚：追加过程任一环节抛错时，已加入的字典/样式/变体全部撤销后再抛出，不留半挂层。
/// </summary>
public sealed class AeroLease : IDisposable
{
    private static readonly Uri BaseUri = new("avares://Fufucode.App/");

    private readonly Control _target;
    private readonly ResourceDictionary _tokens;
    private readonly ResourceDictionary _captionDictionary;
    private readonly IStyle _scopedStyles;
    private readonly ThemeVariant? _previousVariant;
    private readonly bool _ownsVariant;
    private readonly List<AeroCaptionAdapter> _extraCaptions = new();
    private bool _disposed;

    private AeroLease(
        Control target,
        ResourceDictionary tokens,
        ResourceDictionary captionDictionary,
        IStyle scopedStyles,
        AeroCaptionAdapter? caption,
        ThemeVariant? previousVariant,
        bool ownsVariant,
        ThemeVariant requestedVariant)
    {
        _target = target;
        _tokens = tokens;
        _captionDictionary = captionDictionary;
        _scopedStyles = scopedStyles;
        Caption = caption;
        _previousVariant = previousVariant;
        _ownsVariant = ownsVariant;
        RequestedVariant = requestedVariant;
    }

    public Control Target => _target;

    public ThemeVariant RequestedVariant { get; }

    /// <summary>是否接管目标自身的 RequestedThemeVariant（仅 TopLevel 且显式请求具体变体时为 true）。</summary>
    public bool OwnsVariantScope => _ownsVariant;

    public ThemeVariant? PreviousVariant => _previousVariant;

    public AeroCaptionAdapter? Caption { get; private set; }

    public bool IsActive => !_disposed;

    public static AeroLease Apply(Control target, Border? caption = null, ThemeVariant? variant = null) =>
        ApplyCore(target, variant, AeroSkin.TokenDictionaryUri, AeroSkin.CaptionDictionaryUri, AeroSkin.ScopedStylesUri, caption);

    /// <summary>内部重载：URI 可替换，供回滚反例使用（坏 uri 时验证半挂层清理）。</summary>
    internal static AeroLease ApplyCore(
        Control target,
        ThemeVariant? variant,
        string tokenUri,
        string captionUri,
        string stylesUri,
        Border? caption = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        var requestedVariant = variant ?? ThemeVariant.Default;

        if (caption is not null && !ReferenceEquals(caption, target) && !IsWithin(target, caption))
            throw new InvalidOperationException("标题栏不属于租约目标子树：拒绝挂接，避免半挂层。");

        ResourceDictionary? tokens = null;
        ResourceDictionary? captionDictionary = null;
        IStyle? scopedStyles = null;
        ThemeVariant? previousVariant = null;
        var ownsVariant = false;
        try
        {
            tokens = LoadDictionary(tokenUri);
            target.Resources.MergedDictionaries.Add(tokens);

            captionDictionary = LoadDictionary(captionUri);
            target.Resources.MergedDictionaries.Add(captionDictionary);

            scopedStyles = new StyleInclude(BaseUri) { Source = new Uri(stylesUri) };
            target.Styles.Add(scopedStyles);

            if (requestedVariant != ThemeVariant.Default && target is TopLevel topLevel)
            {
                previousVariant = topLevel.RequestedThemeVariant;
                topLevel.RequestedThemeVariant = requestedVariant;
                ownsVariant = true;
            }

            var adapter = caption is null ? null : AeroCaptionAdapter.Attach(caption);
            return new AeroLease(target, tokens, captionDictionary, scopedStyles, adapter, previousVariant, ownsVariant, requestedVariant);
        }
        catch
        {
            if (tokens is not null) target.Resources.MergedDictionaries.Remove(tokens);
            if (captionDictionary is not null) target.Resources.MergedDictionaries.Remove(captionDictionary);
            if (scopedStyles is not null) target.Styles.Remove(scopedStyles);
            if (ownsVariant && target is TopLevel rollbackTopLevel) rollbackTopLevel.RequestedThemeVariant = previousVariant;
            throw;
        }
    }

    /// <summary>挂接附加标题栏（如同进程的浮动窗口里另有一处 caption）；已挂接的适配器保持独立生命周期。</summary>
    public AeroCaptionAdapter AttachCaption(Border caption)
    {
        ArgumentNullException.ThrowIfNull(caption);
        if (_disposed) throw new ObjectDisposedException(nameof(AeroLease));
        var adapter = AeroCaptionAdapter.Attach(caption);
        _extraCaptions.Add(adapter);
        return adapter;
    }

    private static bool IsWithin(Control target, Control candidate)
    {
        for (var current = candidate.Parent; current is not null; current = current.Parent)
            if (ReferenceEquals(current, target))
                return true;
        return false;
    }

    private static ResourceDictionary LoadDictionary(string uri) =>
        AvaloniaXamlLoader.Load(new Uri(uri)) as ResourceDictionary
        ?? throw new InvalidOperationException($"Aero 资源文件不是 ResourceDictionary：{uri}");

    /// <summary>幂等释放：只移除本租约加入的字典/样式，还原本租约改过的变体与标题栏类/按钮 Theme。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Caption?.Dispose();
        foreach (var extra in _extraCaptions) extra.Dispose();
        _extraCaptions.Clear();

        _target.Resources.MergedDictionaries.Remove(_tokens);
        _target.Resources.MergedDictionaries.Remove(_captionDictionary);
        _target.Styles.Remove(_scopedStyles);
        if (_ownsVariant && _target is TopLevel topLevel)
            topLevel.RequestedThemeVariant = _previousVariant;
    }
}
