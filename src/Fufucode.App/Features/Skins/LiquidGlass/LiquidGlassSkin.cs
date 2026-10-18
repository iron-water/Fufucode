using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace Fufucode.App.Features.Skins.LiquidGlass
{
    /// <summary>
    /// iOS 液态玻璃皮肤的独立应用/释放入口（vendored from AeroSkin 先例）。
    /// 向目标追加本模块的令牌字典 + 限定样式；可选对窗口内 chrome 面板批量挂
    /// LiquidGlassAdapter（标题栏/dock 宿主/状态条/浮层包覆 LiquidGlassSurface）。
    /// 释放时按引用移除字典/样式、按快照还原包覆，不碰他人的资源或样式。
    /// </summary>
    public static class LiquidGlassSkin
    {
        public const string TokenDictionaryUri = "avares://Fufucode.App/Styles/Skins/LiquidGlass/LiquidGlassTokens.axaml";
        public const string ScopedStylesUri = "avares://Fufucode.App/Styles/Skins/LiquidGlass/LiquidGlassStyles.axaml";

        /// <summary>应用 LiquidGlass 资源与限定样式；可选同时批量挂接窗口 chrome 面板。</summary>
        public static LiquidGlassLease Apply(Control target, ThemeVariant? variant = null, bool attachChrome = true) =>
            LiquidGlassLease.ApplyCore(target, variant, TokenDictionaryUri, ScopedStylesUri, attachChrome);

        /// <summary>目标是否已挂有本模块的字典（按资源内容判断，不依赖对象身份）。</summary>
        public static bool IsAppliedTo(Control target)
        {
            ArgumentNullException.ThrowIfNull(target);
            // LiquidGlassAccent 只存在于 ThemeDictionaries（亮/暗各一值）；
            // ThemeVariant.Default 查询不会进具体变体，故按特征键在任一主题字典中的存在性判定。
            foreach (var provider in target.Resources.MergedDictionaries)
                if (provider is ResourceDictionary dictionary
                    && dictionary.ThemeDictionaries.TryGetValue(ThemeVariant.Light, out var light)
                    && light is ResourceDictionary themed
                    && themed.ContainsKey("LiquidGlassAccent")) return true;
            return false;
        }
    }

    /// <summary>
    /// 一次 LiquidGlass 应用租约。与 SkinLease/AeroLease 语义对齐（先释放旧租约、
    /// 再应用新租约；Dispose 幂等），额外持有 LiquidGlassAdapter 列表，释放时按快照还原面板。
    /// 失败回滚：追加过程任一环节抛错时，已加入的字典/样式/变体/包覆全部撤销后再抛出。
    /// </summary>
    public sealed class LiquidGlassLease : IDisposable
    {
        private static readonly Uri BaseUri = new("avares://Fufucode.App/");

        private readonly Control _target;
        private readonly ResourceDictionary _tokens;
        private readonly IStyle _scopedStyles;
        private readonly ThemeVariant? _previousVariant;
        private readonly bool _ownsVariant;
        private readonly List<LiquidGlassAdapter> _adapters = new();
        private bool _disposed;

        private LiquidGlassLease(
            Control target,
            ResourceDictionary tokens,
            IStyle scopedStyles,
            ThemeVariant? previousVariant,
            bool ownsVariant,
            ThemeVariant requestedVariant)
        {
            _target = target;
            _tokens = tokens;
            _scopedStyles = scopedStyles;
            _previousVariant = previousVariant;
            _ownsVariant = ownsVariant;
            RequestedVariant = requestedVariant;
        }

        public Control Target => _target;

        public ThemeVariant RequestedVariant { get; }

        public bool OwnsVariantScope => _ownsVariant;

        public ThemeVariant? PreviousVariant => _previousVariant;

        public IReadOnlyList<LiquidGlassAdapter> Adapters => _adapters;

        public bool IsActive => !_disposed;

        public static LiquidGlassLease Apply(Control target, ThemeVariant? variant = null, bool attachChrome = true) =>
            ApplyCore(target, variant, LiquidGlassSkin.TokenDictionaryUri, LiquidGlassSkin.ScopedStylesUri, attachChrome);

        internal static LiquidGlassLease ApplyCore(
            Control target,
            ThemeVariant? variant,
            string tokenUri,
            string stylesUri,
            bool attachChrome)
        {
            ArgumentNullException.ThrowIfNull(target);
            var requestedVariant = variant ?? ThemeVariant.Default;

            ResourceDictionary? tokens = null;
            IStyle? scopedStyles = null;
            ThemeVariant? previousVariant = null;
            var ownsVariant = false;
            LiquidGlassLease? lease = null;
            try
            {
                tokens = LoadDictionary(tokenUri);
                target.Resources.MergedDictionaries.Add(tokens);

                scopedStyles = new StyleInclude(BaseUri) { Source = new Uri(stylesUri) };
                target.Styles.Add(scopedStyles);

                if (requestedVariant != ThemeVariant.Default && target is TopLevel topLevel)
                {
                    previousVariant = topLevel.RequestedThemeVariant;
                    topLevel.RequestedThemeVariant = requestedVariant;
                    ownsVariant = true;
                }

                lease = new LiquidGlassLease(target, tokens, scopedStyles, previousVariant, ownsVariant, requestedVariant);
                if (attachChrome) lease.AttachExistingChrome();
                if (target is Window window)
                {
                    window.Opened += lease.OnWindowOpened;
                    window.PropertyChanged += lease.OnWindowPropertyChanged;
                }
                return lease;
            }
            catch
            {
                if (lease is not null)
                {
                    foreach (var adapter in lease._adapters) adapter.Dispose();
                    lease._adapters.Clear();
                    if (target is Window w)
                    {
                        w.Opened -= lease.OnWindowOpened;
                        w.PropertyChanged -= lease.OnWindowPropertyChanged;
                    }
                }
                if (tokens is not null) target.Resources.MergedDictionaries.Remove(tokens);
                if (scopedStyles is not null) target.Styles.Remove(scopedStyles);
                if (ownsVariant && target is TopLevel rollbackTopLevel) rollbackTopLevel.RequestedThemeVariant = previousVariant;
                throw;
            }
        }

        /// <summary>给新出现的 chrome 面板挂接（如同进程浮动窗口/对话框另有一处 caption 或 dock）。</summary>
        public LiquidGlassAdapter AttachChrome(Border host, bool? isCaption = null)
        {
            ArgumentNullException.ThrowIfNull(host);
            if (_disposed) throw new ObjectDisposedException(nameof(LiquidGlassLease));
            var adapter = LiquidGlassAdapter.Attach(host, isCaption ?? LiquidGlassAdapter.IsCaptionHost(host));
            _adapters.Add(adapter);
            return adapter;
        }

        /// <summary>扫描目标子树里的 chrome 宿主并全部挂接。</summary>
        public int AttachExistingChrome()
        {
            var count = 0;
            // 物化列表：Attach 会把宿主 Border 包进 LiquidGlassSurface（改 Children），
            // 惰性枚举在迭代中途改树会抛 Collection was modified。
            foreach (var border in _target.GetLogicalDescendants().OfType<Border>().ToArray())
            {
                if (!LiquidGlassAdapter.IsGlassHost(border)) continue;
                if (LiquidGlassAdapter.IsAttached(border)) continue;
                try
                {
                    _adapters.Add(LiquidGlassAdapter.Attach(border, LiquidGlassAdapter.IsCaptionHost(border)));
                    count++;
                }
                catch (InvalidOperationException)
                {
                    // 宿主暂时不能包（父容器非 Panel 等）：跳过，不算失败。
                }
            }
            SyncSurfaces();
            return count;
        }

        private void OnWindowOpened(object? sender, EventArgs e) => AttachExistingChrome();

        private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Window.WindowStateProperty
                || e.Property == Window.IsActiveProperty
                || e.Property == TopLevel.ActualTransparencyLevelProperty)
                SyncSurfaces();
        }

        private void SyncSurfaces()
        {
            if (_target is Window window)
                foreach (var adapter in _adapters)
                    adapter.SyncFrom(window);
        }

        private static ResourceDictionary LoadDictionary(string uri) =>
            AvaloniaXamlLoader.Load(new Uri(uri)) as ResourceDictionary
            ?? throw new InvalidOperationException($"LiquidGlass 资源文件不是 ResourceDictionary：{uri}");

        /// <summary>幂等释放：只移除本租约加入的字典/样式，还原本租约的包覆与变体。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_target is Window window)
            {
                window.Opened -= OnWindowOpened;
                window.PropertyChanged -= OnWindowPropertyChanged;
            }
            foreach (var adapter in _adapters) adapter.Dispose();
            _adapters.Clear();

            _target.Resources.MergedDictionaries.Remove(_tokens);
            _target.Styles.Remove(_scopedStyles);
            if (_ownsVariant && _target is TopLevel topLevel)
                topLevel.RequestedThemeVariant = _previousVariant;
        }
    }
}
