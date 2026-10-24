using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Fufucode.Fufu.Pet;

/// <summary>
/// 占位角色渲染器：纯 Avalonia 矢量绘制的小团子。
/// 仅用于在 Live2D 接入前走通桌宠完整流程——不代表 Live2D 已实现。
/// </summary>
internal sealed class PlaceholderPetRenderer : ICharacterRenderer
{
    public PlaceholderPetRenderer() => View = new PetVisual();

    public Control View { get; }
    public Size CharacterSize => new(140, 150);

    public void SetState(PetState state)
    {
        if (View is PetVisual visual) visual.State = state;
    }

    public bool HitTest(Point clientPoint, double scale)
    {
        // 角色实体是下部的椭圆身体 + 小脚：包围盒内按椭圆近似判定，
        // 其余区域（头顶透明带、两侧空隙）让鼠标穿透到桌面。
        var w = CharacterSize.Width * scale;
        var h = CharacterSize.Height * scale;
        var bodyCx = w / 2;
        var bodyCy = h * 0.52;
        var bodyRx = w * 0.46;
        var bodyRy = h * 0.42;
        var dx = (clientPoint.X - bodyCx) / bodyRx;
        var dy = (clientPoint.Y - bodyCy) / bodyRy;
        return dx * dx + dy * dy <= 1.0;
    }

    private sealed class PetVisual : Control
    {
        private static readonly Dictionary<PetState, (Color Body, Color Belly, Color Cheek)> Palette = new()
        {
            [PetState.Idle] = (Color.Parse("#4ECDC4"), Color.Parse("#7FE3DB"), Color.Parse("#FFB3C1")),
            [PetState.Walk] = (Color.Parse("#3EC2B9"), Color.Parse("#7FE3DB"), Color.Parse("#FFB3C1")),
            [PetState.Rest] = (Color.Parse("#8FB8C9"), Color.Parse("#B8D5E0"), Color.Parse("#D9A7B1")),
            [PetState.Interact] = (Color.Parse("#45D9CE"), Color.Parse("#8FE8E0"), Color.Parse("#FF9BB0")),
            [PetState.Thinking] = (Color.Parse("#6FA8DC"), Color.Parse("#9CC5E8"), Color.Parse("#FFB3C1")),
            [PetState.Working] = (Color.Parse("#4A90D9"), Color.Parse("#7FB5E5"), Color.Parse("#FFB3C1")),
            [PetState.Success] = (Color.Parse("#58C98D"), Color.Parse("#8BE0AF"), Color.Parse("#FFB3C1")),
            [PetState.Error] = (Color.Parse("#E0685A"), Color.Parse("#EF9484"), Color.Parse("#FFB3C1")),
            [PetState.Attention] = (Color.Parse("#F2B33D"), Color.Parse("#F8CD77"), Color.Parse("#FFB3C1")),
        };

        private static readonly IBrush ShadowBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0));
        private static readonly IBrush EyeBrush = new SolidColorBrush(Color.Parse("#22242B"));
        private static readonly IBrush EyeShine = Brushes.White;
        private static readonly IBrush MouthBrush = new SolidColorBrush(Color.Parse("#4A3B3F"));
        private static readonly IBrush TongueBrush = new SolidColorBrush(Color.Parse("#FF8FA3"));

        private readonly DispatcherTimer _animator;
        private PetState _state = PetState.Idle;
        private int _frame;

        public PetVisual()
        {
            // 动画节拍：行走弹跳、待机呼吸、眨眼、思考摆动、注意力闪烁。
            _animator = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
            _animator.Tick += (_, _) => { _frame++; InvalidateVisual(); };
            _animator.Start();
        }

        public PetState State
        {
            get => _state;
            set
            {
                if (_state == value) return;
                _state = value;
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var bounds = Bounds;
            var (body, belly, cheek) = Palette.GetValueOrDefault(_state, Palette[PetState.Idle]);

            // 呼吸/弹跳相位
            var t = _frame;
            var breathe = Math.Sin(t * 0.06) * 0.02;
            var bob = _state switch
            {
                PetState.Walk => Math.Abs(Math.Sin(t * 0.25)) * 4,
                PetState.Idle or PetState.Rest => Math.Sin(t * 0.06) * 1.5,
                PetState.Interact or PetState.Success => Math.Abs(Math.Sin(t * 0.2)) * 6,
                _ => 0,
            };
            var squash = _state == PetState.Walk ? 1 + Math.Sin(t * 0.5) * 0.04
                       : _state is PetState.Interact or PetState.Success ? 1 + Math.Abs(Math.Sin(t * 0.2)) * 0.05
                       : 1 + breathe;

            var cx = bounds.Width / 2;
            var groundY = bounds.Height * 0.94;
            var bodyRx = bounds.Width * 0.40 * (2 - squash);
            var bodyRy = bounds.Height * 0.36 * squash;
            var bodyCy = groundY - bodyRy - bob;

            // 地面阴影随弹跳缩放
            var shadowRx = bodyRx * (1.15 - bob / 30);
            context.DrawEllipse(ShadowBrush, null, new Point(cx, groundY + 2), shadowRx, 5);

            // 小脚（行走时左右交替抬起）
            var footW = bounds.Width * 0.14;
            var footH = bounds.Height * 0.07;
            var step = _state == PetState.Walk ? Math.Sin(t * 0.5) * footH : 0;
            var footBrush = new SolidColorBrush(body);
            var leftFoot = new Rect(cx - bodyRx * 0.55 - footW / 2, groundY - footH - Math.Max(0, step), footW, footH);
            var rightFoot = new Rect(cx + bodyRx * 0.55 - footW / 2, groundY - footH - Math.Max(0, -step), footW, footH);
            context.DrawRectangle(footBrush, null, leftFoot, footW / 2, footH / 2);
            context.DrawRectangle(footBrush, null, rightFoot, footW / 2, footH / 2);

            // 身体与肚皮高光
            context.DrawEllipse(footBrush, null, new Point(cx, bodyCy), bodyRx, bodyRy);
            context.DrawEllipse(new SolidColorBrush(belly), null,
                new Point(cx - bodyRx * 0.28, bodyCy - bodyRy * 0.45), bodyRx * 0.30, bodyRy * 0.22);

            // 眼睛：Rest/Thinking 闭眼，周期性眨眼；Attention 瞪大。
            var blink = (t % 45) < 2 || _state is PetState.Rest or PetState.Thinking;
            var eyeY = bodyCy - bodyRy * 0.15;
            var eyeRx = bodyRx * 0.11 * (_state == PetState.Attention ? 1.35 : 1);
            var eyeRy = eyeRx * (blink ? 0.15 : 1.15);
            foreach (var ex in new[] { cx - bodyRx * 0.38, cx + bodyRx * 0.38 })
            {
                context.DrawEllipse(EyeBrush, null, new Point(ex, eyeY), eyeRx, eyeRy);
                if (!blink)
                    context.DrawEllipse(EyeShine, null, new Point(ex - eyeRx * 0.3, eyeY - eyeRy * 0.35),
                        eyeRx * 0.35, eyeRy * 0.35);
            }

            // 腮红
            var cheekBrush = new SolidColorBrush(cheek, 0.75);
            foreach (var ex in new[] { cx - bodyRx * 0.55, cx + bodyRx * 0.55 })
                context.DrawEllipse(cheekBrush, null, new Point(ex, eyeY + bodyRy * 0.28),
                    bodyRx * 0.13, bodyRy * 0.08);

            // 嘴：状态形状
            var mouthY = eyeY + bodyRy * 0.34;
            switch (_state)
            {
                case PetState.Error:
                {
                    var pen = new Pen(MouthBrush, 3, lineCap: PenLineCap.Round);
                    context.DrawLine(pen, new Point(cx - 7, mouthY + 3), new Point(cx, mouthY - 2));
                    context.DrawLine(pen, new Point(cx, mouthY - 2), new Point(cx + 7, mouthY + 3));
                    break;
                }
                case PetState.Thinking:
                {
                    var pen = new Pen(MouthBrush, 2.5, lineCap: PenLineCap.Round);
                    context.DrawLine(pen, new Point(cx - 5, mouthY), new Point(cx + 5, mouthY));
                    break;
                }
                case PetState.Rest:
                    context.DrawEllipse(MouthBrush, null, new Point(cx, mouthY + 2), 4, 4);
                    break;
                default:
                {
                    // 张开的笑口 + 舌头
                    var geometry = new PathGeometry();
                    var figure = new PathFigure { StartPoint = new Point(cx - 9, mouthY), IsClosed = true };
                    figure.Segments?.Add(new QuadraticBezierSegment
                    {
                        Point1 = new Point(cx, mouthY + 16), Point2 = new Point(cx + 9, mouthY),
                    });
                    var figures = geometry.Figures ??= new PathFigures();
                    figures.Add(figure);
                    context.DrawGeometry(MouthBrush, null, geometry);
                    context.DrawEllipse(TongueBrush, null, new Point(cx, mouthY + 7), 5, 3.5);
                    break;
                }
            }

            // 状态附属标记（真实行为反馈，不是装饰）
            switch (_state)
            {
                case PetState.Rest:
                {
                    var zBrush = new SolidColorBrush(Color.FromArgb(160, 80, 90, 110));
                    var zx = cx + bodyRx * 0.8;
                    var zy = eyeY - bodyRy * 0.55;
                    for (var i = 0; i < 3; i++)
                    {
                        var drift = (t * 0.03 + i * 0.33) % 1;
                        var size = 6 + i * 4;
                        context.DrawText(new FormattedText("z",
                            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            new Typeface("Segoe UI"), size, zBrush),
                            new Point(zx + i * 8 - drift * 6, zy - i * 9 - drift * 8));
                    }
                    break;
                }
                case PetState.Attention:
                {
                    var flash = (t % 14) < 7;
                    if (flash)
                        context.DrawText(new FormattedText("!",
                            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), 26,
                            EyeBrush), new Point(cx + bodyRx * 0.75, eyeY - bodyRy * 0.8));
                    break;
                }
                case PetState.Thinking:
                {
                    var dots = (t / 10) % 4;
                    for (var i = 0; i < dots; i++)
                        context.DrawEllipse(EyeShine, null,
                            new Point(cx + bodyRx * 0.7 + i * 9, eyeY - bodyRy * 0.65 - i * 4), 3, 3);
                    break;
                }
            }
        }
    }
}
