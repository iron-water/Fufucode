using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace Fufucode.App.Features.Skins.LiquidGlass
{
    /// <summary>
    /// 把既有 chrome Border（标题栏/停靠宿主/状态条/浮动工具栏/Toast/浮层）包进 LiquidGlassSurface。
    /// 与 AeroCaptionAdapter 同语义：识别→快照原状态→替换 Child/Background/类→Dispose 精确还原；
    /// 同一 Border 多次挂接按引用计数共享一份状态。
    ///
    /// 识别规则：按 x:Name（CustomCaption/SidebarBorder/RightPanelBorder/TerminalBorder/
    /// CustomStatusBar/NotificationPrompt）或类 chromeBar/dockHost/statusStrip/floatingToolbar/
    /// toast/overlayPanel 命中；普通内容面板不识别、不改动。
    ///
    /// 包覆方式：新建 LiquidGlassSurface 放进 Border 原父容器同一位置（复制 Grid.Row/Column/
    /// RowSpan/ColumnSpan/ZIndex/Margin/尺寸绑定），把原 Border 整棵移进 Surface 作其 Child；
    /// Border 本身加 liquidGlass 类（样式表把它背景改透，让玻璃从底下透出）。Surface 即玻璃的
    /// 渲染宿主——折射、模糊、高光、阴影全在它 Render 里走完。
    /// </summary>
    public sealed class LiquidGlassAdapter : IDisposable
    {
        /// <summary>本适配器可能写入宿主的类名；释放时只在这个集合内按快照复原。
        /// underDark/underLight 是 AdaptiveLuminance 采样写出的状态类——宿主本来
        /// 不会有它们，释放时总是清掉（快照里没有它们 → 被视为"原本没有"）。</summary>
        private static readonly string[] ManagedHostClasses =
            ["liquidGlass", "liquidGlassCaption", "glass", "underDark", "underLight"];

        private static readonly Dictionary<Border, SharedState> Shared = new(ReferenceEqualityComparer.Instance);

        private readonly SharedState _state;
        private bool _disposed;

        private LiquidGlassAdapter(SharedState state)
        {
            _state = state;
        }

        public Border Host => _state.Host;

        /// <summary>本适配器为宿主创建的玻璃表面；未挂接时为 null。</summary>
        public LiquidGlassSurface? Surface => _state.Surface;

        public bool IsActive => !_disposed;

        /// <summary>当前是否有租约挂在这个宿主上（测试与诊断用）。</summary>
        public static bool IsAttached(Border host)
        {
            ArgumentNullException.ThrowIfNull(host);
            return Shared.TryGetValue(host, out var state) && state.RefCount > 0;
        }

        /// <summary>
        /// 挂接：识别命中才包覆；未识别（普通面板）抛 InvalidOperationException，由调用方决定跳过。
        /// 同一宿主重复挂接复用状态；任一环节失败按快照回滚，不留半挂层。
        /// </summary>
        /// <param name="host">目标 chrome Border。</param>
        /// <param name="isCaption">是否按"标题栏"语义处理（更高的玻璃强度参数、1px 描边高光）。</param>
        public static LiquidGlassAdapter Attach(Border host, bool isCaption = false)
        {
            ArgumentNullException.ThrowIfNull(host);

            if (Shared.TryGetValue(host, out var existing) && existing.RefCount > 0)
            {
                existing.RefCount++;
                return new LiquidGlassAdapter(existing);
            }

            if (!IsGlassHost(host) && !isCaption)
                throw new InvalidOperationException("目标不是 chrome 面板（未命中 x:Name 或 chrome 类）：拒绝包覆。");

            if (host.Parent is not Panel parent)
                throw new InvalidOperationException("宿主不在 Panel 容器内，无法原位插入玻璃表面。");

            var state = new SharedState(host, SnapshotClasses(host), refCount: 1);
            try
            {
                // 先加类再建表面：.liquidGlass/.dockLeft 等限定样式把浮起 Margin 写到宿主上，
                // CreateSurface 读 Margin 时必须是加类之后的值，否则玻璃面吃不到版式缝。
                AddClass(host, "liquidGlass");
                if (isCaption) AddClass(host, "liquidGlassCaption");
                AddClass(host, "glass");

                var surface = CreateSurface(host, isCaption);
                var index = parent.Children.IndexOf(host);
                if (index < 0)
                    throw new InvalidOperationException("宿主不在其父容器的 Children 中。");

                parent.Children.RemoveAt(index);
                surface.Child = host;
                parent.Children.Insert(index, surface);

                // Surface 自身的类名：样式层/调试据它识别包覆容器（玻璃浮层语义无样式不可达）。
                surface.Classes.Add("glassSurface");
                if (isCaption) surface.Classes.Add("glassCaptionSurface");
                if (host.Classes.Contains("statusStrip")) surface.Classes.Add("glassStatusSurface");

                state.Surface = surface;
                state.Parent = parent;

                Shared[host] = state;
                var adapter = new LiquidGlassAdapter(state);
                adapter.StartAdaptiveLuminance();
                return adapter;
            }
            catch
            {
                // 回滚：若 surface 已入父容器，把 host 取回放回。
                if (state.Parent is { } p && state.Surface is { } s && p.Children.Contains(s))
                {
                    var i = p.Children.IndexOf(s);
                    s.Child = null;
                    p.Children.RemoveAt(i);
                    p.Children.Insert(i, host);
                }

                RestoreClasses(state);
                throw;
            }
        }

        /// <summary>
        /// 按窗口实际透明级别 + 系统无障碍偏好同步 glass 强弱（与 Aero .glass 机制对齐）。
        /// 无平台模糊 / 系统高对比（iOS "Reduce Transparency" 对应物）时把玻璃降到
        /// 近平实色面：关折射/色散/渐进模糊，提高 BackdropOpacity，文字可读优先。
        /// 系统"减少动画"（iOS "Reduce Motion" 对应物）关交互形变与指尖点亮。
        /// </summary>
        public void SyncFrom(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            if (_disposed) throw new ObjectDisposedException(nameof(LiquidGlassAdapter));
            if (_state.Surface is not { } surface) return;

            var level = window.ActualTransparencyLevel;
            var hasPlatformBlur = level == WindowTransparencyLevel.AcrylicBlur
                || level == WindowTransparencyLevel.Blur
                || level == WindowTransparencyLevel.Mica
                || level == WindowTransparencyLevel.Transparent;

            var reducedTransparency =
                Application.Current?.PlatformSettings?.GetColorValues().ContrastPreference
                    == ColorContrastPreference.High;
            var reducedMotion = Fufucode.App.Infrastructure.MotionPreference.ReducedMotion;

            // 平台已给窗后模糊（AcrylicBlur/Blur/Mica/Transparent）时，玻璃面自身的 SurfaceColor
            // 保持较低不透明度让窗后透出；无平台模糊时提高 SurfaceColor 的不透明度，靠折射+色散
            // 单独撑起玻璃感（快照里只有窗内内容）。高对比等同"降低透明度"——彻底实色。
            surface.BackdropOpacity = reducedTransparency ? 1.0 : (hasPlatformBlur ? 0.85 : 1.0);
            surface.ChromaticAberration = !reducedTransparency;
            surface.RefractionAmount = reducedTransparency
                ? 0.0
                : (IsCaptionHost(_state.Host) ? 26.0 : 16.0);
            surface.ProgressiveBlurEnabled =
                !reducedTransparency && !IsCaptionHost(_state.Host);
            surface.HighlightOpacity = reducedTransparency
                ? 0.9
                : (IsCaptionHost(_state.Host) ? 0.7 : 0.5);

            if (surface is LiquidGlassInteractiveSurface interactive)
            {
                interactive.IsInteractive = !reducedMotion;
                interactive.InteractiveHighlightEnabled = !reducedMotion;
            }
        }

        /// <summary>幂等释放：引用计数归 0 时把宿主 Border 从 Surface 中取出放回父容器原位，并还原类与 Child。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopLuminanceTimer();
            _state.RefCount--;
            if (_state.RefCount > 0) return;

            Shared.Remove(_state.Host);

            if (_state.Parent is { } parent && _state.Surface is { } surface)
            {
                var index = parent.Children.IndexOf(surface);
                if (index >= 0)
                {
                    surface.Child = null;
                    parent.Children.RemoveAt(index);
                    parent.Children.Insert(index, _state.Host);
                }
                else if (surface.Child is not null)
                {
                    // 父容器已不含 surface（布局重建过）：至少把 Child 释放回逻辑树。
                    surface.Child = null;
                }
            }

            RestoreClasses(_state);
            _state.Surface = null;
            _state.Parent = null;
        }

        // ---- Adaptive luminance：玻璃底下内容亮度采样（低频），写进 surface.AdaptiveLuminance。 ----
        // 玻璃上文字/图标的颜色由样式层按该值选反色（亮底→暗字、暗底→亮字）。

        private Avalonia.Threading.DispatcherTimer? _luminanceTimer;

        /// <summary>启动 AdaptiveLuminance 采样：首帧后立即一次，之后每 250ms 跟 snapshot 同步。
        /// 宿主不可见（隐藏/最小化/未入树）时 tick 直接跳过，不产生采样开销；
        /// Avalonia 12 的 IsEffectivelyVisibleChanged 不可公开订阅，所以保留定时器用 tick 兜底。</summary>
        internal void StartAdaptiveLuminance()
        {
            if (_state.Surface is null || _luminanceTimer is not null) return;
            _luminanceTimer = new Avalonia.Threading.DispatcherTimer(Avalonia.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _luminanceTimer.Tick += (_, _) => UpdateAdaptiveLuminance();
            _luminanceTimer.Start();
            // 首采样不等 tick
            UpdateAdaptiveLuminance();
        }

        private void StopLuminanceTimer()
        {
            _luminanceTimer?.Stop();
            _luminanceTimer = null;
        }

        /// <summary>宿主与所在窗口都实际可见时才采样；玻璃渲染本身不受影响。</summary>
        private bool IsLuminanceVisible()
        {
            if (_disposed || _state.Surface is null) return false;
            if (!_state.Host.IsEffectivelyVisible) return false;
            return TopLevel.GetTopLevel(_state.Host) is Window window
                ? window.IsVisible && window.WindowState != WindowState.Minimized
                : false;
        }

        private void UpdateAdaptiveLuminance()
        {
            // 低内存模式：关闭玻璃/模糊采样，玻璃渲染本身仍在。
            if (Fufucode.App.Integration.MemoryProfile.DisableVisualEffects) return;
            if (!IsLuminanceVisible()) return;
            if (_state.Surface is not { } surface) return;
            var snapshot = LiquidGlassBackdropProvider.TryGetSnapshot(surface);
            if (snapshot is null) return;

            var transformed = surface.GetTransformedBounds();
            if (transformed is null) return;
            var bounds = transformed.Value.Bounds.TransformToAABB(transformed.Value.Transform);

            var lum = snapshot.SampleLuminance(bounds);
            surface.AdaptiveLuminance = lum;

            // 同步 class 让样式层给玻璃上的文字/图标选反色：
            //   underDark → 底下亮（需要暗文字）；underLight → 底下暗（需要亮文字）。
            // 阈值 0.5 保守；迟滞 0.08 防止亮暗交界频繁抖动。
            var host = _state.Host;
            bool hasDark = host.Classes.Contains("underDark");
            bool hasLight = host.Classes.Contains("underLight");
            const double upper = 0.58, lower = 0.42;
            if (!hasDark && lum > upper)
            {
                host.Classes.Add("underDark");
                host.Classes.Remove("underLight");
            }
            else if (!hasLight && lum < lower)
            {
                host.Classes.Add("underLight");
                host.Classes.Remove("underDark");
            }
        }

        /// <summary>识别 chrome 宿主：x:Name 或语义类。供 SkinLease 扫描时预判，避免盲目 try/catch。</summary>
        public static bool IsGlassHost(Border host) => host.Name switch
        {
            "CustomCaption" or "SidebarBorder" or "RightPanelBorder" or "TerminalBorder"
                or "CustomStatusBar" or "NotificationPrompt" => true,
            _ => host.Classes.Contains("chromeBar")
                 || host.Classes.Contains("dockHost")
                 || host.Classes.Contains("settingsNavHost")
                 || host.Classes.Contains("floatingToolbar")
                 || host.Classes.Contains("toast")
                 || host.Classes.Contains("overlayPanel"),
        };

        /// <summary>是否为标题栏语义（决定玻璃参数强度与 liquidGlassCaption 类）。</summary>
        public static bool IsCaptionHost(Border host) =>
            host.Name == "CustomCaption" || host.Classes.Contains("chromeBar");

        private static LiquidGlassSurface CreateSurface(Border host, bool isCaption)
        {
            // 继承宿主的布局定位：Grid 附加属性 + 尺寸/边距。
            // 玻璃强度按宿主语义分档：caption 最强折射/最亮高光，dock/浮层面板次级。
            // dock/toast/overlayPanel 用 InteractiveSurface：按压指尖发光+形变（iOS interactive()）。
            // caption 是拖拽区——按压=窗口拖动，不该有"点亮"形变，保持静态表面。
            var surface = isCaption
                ? new LiquidGlassSurface()
                : new LiquidGlassInteractiveSurface { IsInteractive = true, InteractiveHighlightEnabled = true, InteractiveMaxScaleDip = 3.0 };
            ApplySurfaceParameters(surface, host, isCaption);
            return surface;
        }

        private static void ApplySurfaceParameters(LiquidGlassSurface surface, Border host, bool isCaption)
        {
            // iOS 玻璃语义：chrome 取较强折射与边缘高光；dock/面板略弱。
            // BlurRadius 用真实 iOS 量级（UIVisualEffect 半径远高于 2）才有"液"感；
            // SurfaceColor 经 DynamicResource 绑到 LiquidGlass*SurfaceColor 令牌，alpha
            // 决定玻璃面实色占比——alpha 越低窗后/窗内模糊越透。
            surface.BlurRadius = isCaption ? 22.0 : 16.0;
            surface.Vibrancy = 1.6;
            surface.RefractionHeight = isCaption ? 12.0 : 8.0;
            surface.RefractionAmount = isCaption ? 26.0 : 16.0;
            surface.ChromaticAberration = true;
            surface.DepthEffect = true;
            surface.HighlightEnabled = true;
            surface.HighlightWidth = 0.75;
            surface.HighlightOpacity = isCaption ? 0.7 : 0.5;
            surface.HighlightAngle = 90.0;
            surface.HighlightFalloff = 1.2;
            surface.ShadowEnabled = !isCaption; // caption 不外投影（Vista/iOS 顶栏贴边）
            surface.ShadowRadius = 24.0;
            surface.ShadowOpacity = 0.5;
            surface.CornerRadius = isCaption ? new CornerRadius(0) : new CornerRadius(12);

            // Progressive blur：dock/侧栏顶部向内容渐隐（iOS scroll edge soft 语义）；
            // 顶部 35% 渐隐到透明，面板底下内容滚过去时不是硬切边。
            surface.ProgressiveBlurEnabled = !isCaption;
            surface.ProgressiveBlurStart = 0.0;
            surface.ProgressiveBlurEnd = 0.35;
            surface.ProgressiveTintColor = Color.FromArgb(0, 0, 0, 0);
            surface.ProgressiveTintIntensity = 0.0;

            // 玻璃面实色取自令牌（主题化）：caption 更薄透，dock/面板比 caption 稍实。
            var resourceKey = isCaption ? "LiquidGlassCaptionSurfaceColor" : "LiquidGlassDockSurfaceColor";
            surface[!LiquidGlassSurface.SurfaceColorProperty] =
                new Avalonia.Data.Binding { Source = host, Path = $"Resources[{resourceKey}]", Converter = ColorResourceConverter.Instance };
            var tintBinding = new Avalonia.Data.Binding { Source = host, Path = "Resources[LiquidGlassTintColor]", Converter = ColorResourceConverter.Instance };
            surface[!LiquidGlassSurface.TintColorProperty] = tintBinding;

            CopyAttached(host, surface, Grid.RowProperty);
            CopyAttached(host, surface, Grid.ColumnProperty);
            CopyAttached(host, surface, Grid.RowSpanProperty);
            CopyAttached(host, surface, Grid.ColumnSpanProperty);
            CopyAttached(host, surface, Canvas.LeftProperty);
            CopyAttached(host, surface, Canvas.TopProperty);
            CopyAttached(host, surface, Canvas.RightProperty);
            CopyAttached(host, surface, Canvas.BottomProperty);
            CopyAttached(host, surface, Visual.ZIndexProperty);

            // Margin/IsVisible 用绑定而不是一次性拷贝：宿主的浮起边距是 .liquidGlass
            // 类下的样式值（加类后才生效，且皮肤热切换会再写），一次拷贝拿不到；
            // 绑定让玻璃面实时跟随宿主版式，宿主隐藏（如 ShowStatusBar=false）时玻璃面同隐。
            surface.Bind(Layoutable.MarginProperty,
                new Avalonia.Data.Binding(nameof(Layoutable.Margin)) { Source = host });
            surface.Bind(Visual.IsVisibleProperty,
                new Avalonia.Data.Binding(nameof(Visual.IsVisible)) { Source = host });
            if (!double.IsNaN(host.Width)) surface.Width = host.Width;
            if (!double.IsNaN(host.Height)) surface.Height = host.Height;
            surface.MinWidth = host.MinWidth;
            surface.MinHeight = host.MinHeight;
            surface.MaxWidth = host.MaxWidth;
            surface.MaxHeight = host.MaxHeight;
            surface.HorizontalAlignment = host.HorizontalAlignment;
            surface.VerticalAlignment = host.VerticalAlignment;
        }

        private static void CopyAttached(Control from, Control to, AvaloniaProperty property)
        {
            if (from.IsSet(property)) to.SetValue(property, from.GetValue(property));
        }

        private static string[] SnapshotClasses(Control control)
        {
            var present = new List<string>();
            foreach (var name in ManagedHostClasses)
                if (control.Classes.Contains(name)) present.Add(name);
            return present.ToArray();
        }

        private static void RestoreClasses(SharedState state)
        {
            var original = state.OriginalClasses;
            foreach (var name in ManagedHostClasses)
            {
                var had = Array.IndexOf(original, name) >= 0;
                var has = state.Host.Classes.Contains(name);
                if (had && !has) state.Host.Classes.Add(name);
                else if (!had && has) state.Host.Classes.Remove(name);
            }
        }

        private static void AddClass(Control control, string name)
        {
            if (!control.Classes.Contains(name)) control.Classes.Add(name);
        }

        private sealed class SharedState
        {
            public SharedState(Border host, string[] originalClasses, int refCount)
            {
                Host = host;
                OriginalClasses = originalClasses;
                RefCount = refCount;
            }

            public Border Host { get; }

            public string[] OriginalClasses { get; }

            public int RefCount { get; set; }

            public LiquidGlassSurface? Surface { get; set; }

            public Panel? Parent { get; set; }
        }
    }
}
