using System.Text.Json;

namespace Live2D.Core;

/// <summary>
/// motion3.json 解析与求值，等价 Cubism Framework cubismmotion 的最小实现：
/// 段类型 0=Linear 1=Bezier 2=Stepped 3=InverseStepped；首个点为 (time,value) 对，
/// Linear/Stepped 跟 2 个数（endTime,endValue），Bezier 跟 6 个数（c1t,c1v,c2t,c2v,endT,endV）。
/// 权重语义：v = src + (v_motion - src) * fadeWeight。
/// </summary>
public sealed class Motion3Json
{
    public enum Target : byte { Model = 0, Parameter = 1, PartOpacity = 2 }

    public sealed class Curve
    {
        public Target Type;
        public string Id = "";
        public float FadeInTime = -1f, FadeOutTime = -1f;   // 曲线级淡入淡出（秒，<0 = 用整体）
        public int BasePoint;   // 首个点索引
        public (int Base, int Count)[] Segments = Array.Empty<(int, int)>();
        public byte[] SegmentTypes = Array.Empty<byte>();
    }

    public float Duration, Fps, FadeInTime = 1f, FadeOutTime = 1f;
    public bool Loop;
    public float EventFiredTime { get; private set; }
    public string? LastEvent;
    private readonly HashSet<string> _firedEvents = new();
    public List<(float Time, float Value)> Points = new();
    public List<Curve> Curves = new();
    private readonly List<(string Id, float Time)> _events = new();

    public static Motion3Json Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var meta = root.GetProperty("Meta");
        var m = new Motion3Json
        {
            Duration = meta.TryGetProperty("Duration", out var d) ? d.GetSingle() : 0,
            Loop = meta.TryGetProperty("Loop", out var l) && l.ValueKind == JsonValueKind.True,
            Fps = meta.TryGetProperty("Fps", out var f) ? f.GetSingle() : 30f,
        };
        if (meta.TryGetProperty("FadeInTime", out var fi)) m.FadeInTime = fi.GetSingle();
        if (meta.TryGetProperty("FadeOutTime", out var fo)) m.FadeOutTime = fo.GetSingle();
        if (root.TryGetProperty("Meta", out _) && meta.TryGetProperty("AreBeziersRestricted", out var r) && r.GetBoolean())
        { /* restricted bezier = 标准 de Casteljau，与本实现一致 */ }

        if (root.TryGetProperty("Curves", out var curves))
            foreach (var c in curves.EnumerateArray())
            {
                var curve = new Curve();
                var t = c.GetProperty("Target").GetString();
                curve.Type = t switch
                {
                    "Model" => Target.Model,
                    "Parameter" => Target.Parameter,
                    "PartOpacity" => Target.PartOpacity,
                    _ => Target.Parameter
                };
                curve.Id = c.GetProperty("Id").GetString() ?? "";
                if (c.TryGetProperty("FadeInTime", out var cfi)) curve.FadeInTime = cfi.GetSingle();
                if (c.TryGetProperty("FadeOutTime", out var cfo)) curve.FadeOutTime = cfo.GetSingle();
                var seg = c.GetProperty("Segments");
                var segs = new List<(int, int)>();
                var types = new List<byte>();
                int i = 0;
                // 首点 (time, value)
                m.Points.Add((seg[0].GetSingle(), seg[1].GetSingle()));
                i = 2;
                curve.BasePoint = m.Points.Count - 1;
                while (i < seg.GetArrayLength())
                {
                    byte type = (byte)seg[i].GetInt32();
                    int n = type == 1 ? 3 : 1;            // bezier 3 个点，其余 1 个终点
                    segs.Add((m.Points.Count - 1, n));
                    types.Add(type);
                    for (int k = 1; k <= n; k++)
                        m.Points.Add((seg[i + 2 * k - 1].GetSingle(), seg[i + 2 * k].GetSingle()));
                    i += 1 + 2 * n;
                }
                curve.Segments = segs.ToArray();
                curve.SegmentTypes = types.ToArray();
                m.Curves.Add(curve);
            }

        if (root.TryGetProperty("UserData", out var ud))
            foreach (var e in ud.EnumerateArray())
                m._events.Add((e.GetProperty("Value").GetString() ?? "", e.GetProperty("Time").GetSingle()));
        return m;
    }

    private float EvalCurve(Curve c, float t)
    {
        // 定位时间所在段（按框架语义：段终点时间 > t 的第一个段）
        int segIdx = -1;
        for (int s = 0; s < c.Segments.Length; s++)
        {
            var (b, n) = c.Segments[s];
            if (Points[b + n].Time > t) { segIdx = s; break; }
        }
        if (segIdx < 0) return Points[c.Segments[^1].Base + c.Segments[^1].Count].Value;

        var (b0, n0) = c.Segments[segIdx];
        var p0 = Points[b0];
        switch (c.SegmentTypes[segIdx])
        {
            case 0: // Linear
            {
                var p1 = Points[b0 + 1];
                return p1.Time - p0.Time < 1e-8f ? p0.Value : p0.Value + (p1.Value - p0.Value) * (t - p0.Time) / (p1.Time - p0.Time);
            }
            case 1: // Bezier de Casteljau
            {
                var p1 = Points[b0 + 1]; var p2 = Points[b0 + 2]; var p3 = Points[b0 + 3];
                float tt = p3.Time - p0.Time < 1e-8f ? 0 : (t - p0.Time) / (p3.Time - p0.Time);
                var a = Lerp(p0, p1, tt); var b = Lerp(p1, p2, tt); var cc = Lerp(p2, p3, tt);
                return Lerp(Lerp(a, b, tt), Lerp(b, cc, tt), tt).V;
            }
            case 2: return p0.Value;                    // Stepped
            case 3: return Points[b0 + 1].Value;        // InverseStepped
            default: return p0.Value;
        }
        static (float T, float V) Lerp((float T, float V) a, (float T, float V) b, float t)
            => (a.T + (b.T - a.T) * t, a.V + (b.V - a.V) * t);
    }

    /// <summary>
    /// 求曲线在 t 秒的值。typeHint 用于按段类型细分的平滑曲线目标。
    /// </summary>
    public float Evaluate(Curve c, float t) => EvalCurve(c, t);

    /// <summary>当前触发的新事件（每帧至多取一次后清零）。</summary>
    public string? ConsumeEvent(float t)
    {
        foreach (var (id, et) in _events)
            if (et <= t && _firedEvents.Add(id + "@" + et)) return id;
        return null;
    }

    public void ResetEvents() => _firedEvents.Clear();
}

/// <summary>带淡入淡出的活动运动实例（简化版 MotionQueueEntry）。</summary>
public sealed class ActiveMotion
{
    public required Motion3Json Motion;
    public float StartTime = -1;          // 会话时钟（秒）
    public float EndTime = -1;            // 非 loop 时的结束时刻
    public float FadeIn = 1f, FadeOut = 1f;
    public bool Loop;
    public float Weight = 1f;
    public bool Finished;

    public float FadeWeight(float now, float fadeIn, float fadeOut)
    {
        float fin = fadeIn <= 0 ? 1f : EasingSin((now - StartTime) / fadeIn);
        float fout = fadeOut <= 0 || EndTime < 0 ? 1f : EasingSin((EndTime - now) / fadeOut);
        return Weight * fin * fout;
    }

    public static float EasingSin(float x) =>
        x <= 0 ? 0 : x >= 1 ? 1 : 0.5f - 0.5f * MathF.Cos(x * MathF.PI);
}
