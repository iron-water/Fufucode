using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Fufucode.App.Features.Skins.LiquidGlass
{
    /// <summary>
    /// 可交互液态玻璃表面（Apple HIG / WWDC25 规范 §7.1–§7.3）：
    /// - 四阶状态机（Rest -> Hover 1.015 浮起 -> Press 0.970 张力下陷收缩 -> Spring 欠阻尼弹性回弹）
    /// - 视差微倾斜（Parallax Micro-Tilt：向光标微动 0.5–1.5px 气垫感）
    /// - 动态高光引力追踪（光标位置驱动边缘 Specular Glint 掠射镜面斑）
    /// - 指尖发光点亮（Internal Glow：按下时从触点激起局部水波光晕，绝非整面刷白）
    /// </summary>
    public class LiquidGlassInteractiveSurface : LiquidGlassSurface
    {
        public static readonly StyledProperty<bool> IsInteractiveProperty =
            AvaloniaProperty.Register<LiquidGlassInteractiveSurface, bool>(nameof(IsInteractive), true);

        public static readonly StyledProperty<bool> InteractiveHighlightEnabledProperty =
            AvaloniaProperty.Register<LiquidGlassInteractiveSurface, bool>(nameof(InteractiveHighlightEnabled), true);

        public static readonly StyledProperty<double> InteractiveMaxScaleDipProperty =
            AvaloniaProperty.Register<LiquidGlassInteractiveSurface, double>(nameof(InteractiveMaxScaleDip), 4.0);

        static LiquidGlassInteractiveSurface()
        {
            AffectsRender<LiquidGlassInteractiveSurface>(
                IsInteractiveProperty,
                InteractiveHighlightEnabledProperty,
                InteractiveMaxScaleDipProperty);
        }

        // 弹簧参数严格按 Apple 规范 §3.4：
        //   Hover/Press：stiffness 320, damping 0.82, mass 1.0（紧凑、极快响应、微弱粘性）
        //   Presence：stiffness 240, damping 0.85, mass 1.0（光晕平滑淡入淡出）
        //   Pointer：stiffness 400, damping 0.85, mass 0.8（极度跟手）
        private readonly LiquidSpring _hoverSpring = new(320.0, 0.82, 1.0, 0.001);
        private readonly LiquidSpring _pressSpring = new(320.0, 0.82, 1.0, 0.001);
        private readonly LiquidSpring _presenceSpring = new(240.0, 0.85, 1.0, 0.001);
        private readonly LiquidSpring2D _pointerSpring = new(400.0, 0.85, 0.8, 0.2);

        private readonly DispatcherTimer _animationTimer;
        private DateTime _lastAnimationTickUtc;

        private int? _activePointerId;
        private TopLevel? _hookedTopLevel;

        public LiquidGlassInteractiveSurface()
        {
            RenderTransformOrigin = RelativePoint.Center;

            _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };

            _animationTimer.Tick += (_, _) =>
            {
                DateTime now = DateTime.UtcNow;
                double dt = (now - _lastAnimationTickUtc).TotalSeconds;
                _lastAnimationTickUtc = now;

                dt = Math.Max(0.0, Math.Min(0.05, dt));

                bool moving = false;
                moving |= _hoverSpring.Step(dt);
                moving |= _pressSpring.Step(dt);
                moving |= _presenceSpring.Step(dt);
                moving |= _pointerSpring.Step(dt);

                ApplyDeformation();
                InvalidateVisual();
                FrontOverlay?.InvalidateVisual();
                InteractiveOverlay?.InvalidateVisual();

                if (!moving)
                    _animationTimer.Stop();
            };

            // 常态交互监听：Hover、Move、Leave、Press
            AddHandler(PointerEnteredEvent, OnSelfPointerEntered, RoutingStrategies.Tunnel, true);
            AddHandler(PointerMovedEvent, OnSelfPointerMoved, RoutingStrategies.Tunnel, true);
            AddHandler(PointerExitedEvent, OnSelfPointerExited, RoutingStrategies.Tunnel, true);
            AddHandler(PointerPressedEvent, OnSelfPointerPressed, RoutingStrategies.Tunnel, true);
        }

        public bool IsInteractive
        {
            get => GetValue(IsInteractiveProperty);
            set => SetValue(IsInteractiveProperty, value);
        }

        public bool InteractiveHighlightEnabled
        {
            get => GetValue(InteractiveHighlightEnabledProperty);
            set => SetValue(InteractiveHighlightEnabledProperty, value);
        }

        public double InteractiveMaxScaleDip
        {
            get => GetValue(InteractiveMaxScaleDipProperty);
            set => SetValue(InteractiveMaxScaleDipProperty, value);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            UnhookTopLevel();
            _activePointerId = null;
            _animationTimer.Stop();
        }

        private void OnSelfPointerEntered(object? sender, PointerEventArgs e)
        {
            if (!IsInteractive) return;
            _presenceSpring.Target = 1.0;
            _hoverSpring.Target = 1.0;
            Point p = e.GetPosition(this);
            _pointerSpring.Target = p;
            StartAnimation();
        }

        private void OnSelfPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!IsInteractive) return;
            Point p = e.GetPosition(this);
            _pointerSpring.Target = p;
            if (_activePointerId is null)
            {
                _presenceSpring.Target = 1.0;
                _hoverSpring.Target = 1.0;
            }
            StartAnimation();
        }

        private void OnSelfPointerExited(object? sender, PointerEventArgs e)
        {
            if (_activePointerId is null)
            {
                _hoverSpring.Target = 0.0;
                _presenceSpring.Target = 0.0;
                StartAnimation();
            }
        }

        private void OnSelfPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!IsInteractive)
                return;

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            if (_activePointerId is not null)
                return;

            _activePointerId = e.Pointer.Id;
            Point p = e.GetPosition(this);
            _pointerSpring.Target = p;
            _pressSpring.Target = 1.0;
            _presenceSpring.Target = 1.0;

            HookTopLevel();
            StartAnimation();
        }

        private void OnTopLevelPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_activePointerId is null || e.Pointer.Id != _activePointerId.Value)
                return;

            Point p = e.GetPosition(this);
            _pointerSpring.Target = p;
            StartAnimation();
        }

        private void OnTopLevelPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_activePointerId is null || e.Pointer.Id != _activePointerId.Value)
                return;

            EndInteraction(e);
        }

        private void OnTopLevelPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (_activePointerId is null)
                return;

            EndInteraction(null);
        }

        private void EndInteraction(PointerEventArgs? e)
        {
            _activePointerId = null;
            UnhookTopLevel();

            _pressSpring.Target = 0.0;

            // 检查指针是否仍在控件范围内
            bool stillOver = false;
            if (e is not null && Bounds.Width > 0 && Bounds.Height > 0)
            {
                Point p = e.GetPosition(this);
                stillOver = new Rect(0, 0, Bounds.Width, Bounds.Height).Contains(p);
                _pointerSpring.Target = p;
            }

            _hoverSpring.Target = stillOver ? 1.0 : 0.0;
            _presenceSpring.Target = stillOver ? 1.0 : 0.0;

            StartAnimation();
        }

        private void HookTopLevel()
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null || ReferenceEquals(topLevel, _hookedTopLevel))
                return;

            UnhookTopLevel();

            _hookedTopLevel = topLevel;
            topLevel.AddHandler(PointerMovedEvent, OnTopLevelPointerMoved, RoutingStrategies.Tunnel, true);
            topLevel.AddHandler(PointerReleasedEvent, OnTopLevelPointerReleased, RoutingStrategies.Tunnel, true);
            topLevel.AddHandler(PointerCaptureLostEvent, OnTopLevelPointerCaptureLost, RoutingStrategies.Tunnel, true);
        }

        private void UnhookTopLevel()
        {
            if (_hookedTopLevel is null)
                return;

            _hookedTopLevel.RemoveHandler(PointerMovedEvent, OnTopLevelPointerMoved);
            _hookedTopLevel.RemoveHandler(PointerReleasedEvent, OnTopLevelPointerReleased);
            _hookedTopLevel.RemoveHandler(PointerCaptureLostEvent, OnTopLevelPointerCaptureLost);
            _hookedTopLevel = null;
        }

        private void StartAnimation()
        {
            if (_animationTimer.IsEnabled)
                return;

            _lastAnimationTickUtc = DateTime.UtcNow;
            _animationTimer.Start();
        }

        private void InvalidateSubscriberVisual()
        {
            LiquidGlassBackdropProvider.NotifySubscriberOnlyInvalidation(this);
            InvalidateVisual();
        }

        private void ApplyDeformation()
        {
            if (!IsInteractive || Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                RenderTransform = null;
                return;
            }

            double w = Bounds.Width;
            double h = Bounds.Height;

            double hoverVal = Math.Clamp(_hoverSpring.Value, 0.0, 1.0);
            double pressVal = Math.Clamp(_pressSpring.Value, 0.0, 1.0);
            double presenceVal = Math.Clamp(_presenceSpring.Value, 0.0, 1.0);

            // 规范 §7.3 缩放物理模型：
            //   Hover 浮起 1.015 (+1.5%)
            //   Press 下陷 0.970 (-3.0% 物理张力压缩收缩，绝不下移！)
            double scale = Lerp(1.0, 1.015, hoverVal) * Lerp(1.0, 0.970, pressVal);

            // 规范 §7.1 视差微位移：向光标方向微倾斜（0.5–1.2px 气垫浮动感）
            double halfW = Math.Max(1.0, w * 0.5);
            double halfH = Math.Max(1.0, h * 0.5);
            Point curPos = _pointerSpring.Value;
            double nx = Math.Clamp((curPos.X - halfW) / halfW, -1.0, 1.0);
            double ny = Math.Clamp((curPos.Y - halfH) / halfH, -1.0, 1.0);
            double tx = nx * 1.2 * presenceVal;
            double ty = ny * 1.2 * presenceVal;

            if (Math.Abs(scale - 1.0) < 0.0005 && Math.Abs(tx) < 0.01 && Math.Abs(ty) < 0.01)
                RenderTransform = null;
            else
                RenderTransform = new MatrixTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(tx, ty));
        }

        internal override LiquidGlassDrawParameters CreateDrawParameters()
        {
            var p = base.CreateDrawParameters();
            p.HoverProgress = Math.Clamp(_hoverSpring.Value, 0.0, 1.0);
            p.PressProgress = Math.Clamp(_pressSpring.Value, 0.0, 1.0);
            p.PointerLocal = _pointerSpring.Value;
            p.PointerPresence = Math.Clamp(_presenceSpring.Value, 0.0, 1.0);
            p.InteractiveProgress = p.PressProgress;
            p.InteractivePosition = p.PointerLocal;
            return p;
        }

        internal double GetInteractiveHighlightProgress()
        {
            if (!IsInteractive || !InteractiveHighlightEnabled)
                return 0.0;

            return Math.Clamp(_pressSpring.Value, 0.0, 1.0);
        }

        internal Point GetInteractiveHighlightPosition()
        {
            return _pointerSpring.Value;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsInteractiveProperty
                || change.Property == InteractiveHighlightEnabledProperty
                || change.Property == InteractiveMaxScaleDipProperty)
            {
                LiquidGlassBackdropProvider.NotifySubscriberOnlyInvalidation(this);
                InteractiveOverlay?.InvalidateVisual();
            }
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }
}
