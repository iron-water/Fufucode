using System;
using Avalonia;

namespace Fufucode.App.Features.Skins.LiquidGlass
{
    /// <summary>
    /// 二阶阻尼弹簧 m·x'' + c·x' + k·(x − target) = 0（Apple Liquid Glass 规范 §3.4：
    /// 禁止线性/贝塞尔驱动玻璃，必须带质量与粘滞阻尼）。固定子步半隐式欧拉，k≤400 时稳定。
    /// </summary>
    internal sealed class LiquidSpring
    {
        private readonly double _stiffness;
        private readonly double _damping;
        private readonly double _mass;
        private readonly double _threshold;

        /// <param name="stiffness">刚度 k。</param>
        /// <param name="dampingRatio">阻尼比 ζ（&lt;1 欠阻尼，末端轻微回弹）。</param>
        /// <param name="mass">质量 m。</param>
        /// <param name="threshold">静止判定阈值（位移与速度）。</param>
        public LiquidSpring(double stiffness, double dampingRatio, double mass, double threshold = 0.001)
        {
            _stiffness = stiffness;
            _mass = mass;
            _damping = 2.0 * dampingRatio * Math.Sqrt(stiffness * mass);
            _threshold = threshold;
        }

        public double Value { get; private set; }
        public double Velocity { get; private set; }
        public double Target { get; set; }

        public bool IsResting =>
            Math.Abs(Value - Target) <= _threshold && Math.Abs(Velocity) <= _threshold;

        public void SnapTo(double value)
        {
            Value = value;
            Target = value;
            Velocity = 0.0;
        }

        /// <summary>推进 dt 秒；返回是否仍在运动。</summary>
        public bool Step(double dt)
        {
            if (IsResting)
            {
                Value = Target;
                Velocity = 0.0;
                return false;
            }

            const int substeps = 4;
            double h = dt / substeps;
            double x = Value, v = Velocity;
            for (int i = 0; i < substeps; i++)
            {
                double a = (-_stiffness * (x - Target) - _damping * v) / _mass;
                v += a * h;
                x += v * h;
            }

            Value = x;
            Velocity = v;
            return !IsResting;
        }
    }

    internal sealed class LiquidSpring2D
    {
        private readonly LiquidSpring _x;
        private readonly LiquidSpring _y;

        public LiquidSpring2D(double stiffness, double dampingRatio, double mass, double threshold = 0.25)
        {
            _x = new LiquidSpring(stiffness, dampingRatio, mass, threshold);
            _y = new LiquidSpring(stiffness, dampingRatio, mass, threshold);
        }

        public Point Value => new(_x.Value, _y.Value);

        public Point Target
        {
            get => new(_x.Target, _y.Target);
            set
            {
                _x.Target = value.X;
                _y.Target = value.Y;
            }
        }

        public void SnapTo(Point value)
        {
            _x.SnapTo(value.X);
            _y.SnapTo(value.Y);
        }

        public bool Step(double dt) => _x.Step(dt) | _y.Step(dt);
    }
}
