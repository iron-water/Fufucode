using System.Text.Json;

namespace Live2D.Core;

/// <summary>model3.json 的解析（纹理、motions 分组、hitAreas、pose/physics 路径）。</summary>
public sealed class ModelSpec
{
    public required string Dir;
    public required string Moc;
    public string[] Textures = Array.Empty<string>();
    public string? Physics, Pose, DisplayInfo;
    /// <summary>group → [(file, fadeIn, fadeOut)]</summary>
    public Dictionary<string, List<(string File, float FadeIn, float FadeOut)>> Motions = new();
    /// <summary>hitArea drawableId → name</summary>
    public List<(string Id, string Name)> HitAreas = new();
    public string[] EyeBlinkParamIds = Array.Empty<string>();
    public string[] LipSyncParamIds = Array.Empty<string>();

    public static ModelSpec Load(string model3JsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(model3JsonPath));
        var root = doc.RootElement;
        var fr = root.GetProperty("FileReferences");
        var spec = new ModelSpec
        {
            Dir = Path.GetDirectoryName(model3JsonPath)!,
            Moc = fr.GetProperty("Moc").GetString()!
        };
        if (fr.TryGetProperty("Textures", out var tex))
            spec.Textures = tex.EnumerateArray().Select(t => t.GetString()!).ToArray();
        if (fr.TryGetProperty("Physics", out var ph)) spec.Physics = ph.GetString();
        if (fr.TryGetProperty("Pose", out var po)) spec.Pose = po.GetString();
        if (fr.TryGetProperty("DisplayInfo", out var di)) spec.DisplayInfo = di.GetString();
        if (fr.TryGetProperty("Motions", out var mo))
            foreach (var g in mo.EnumerateObject())
            {
                var list = new List<(string, float, float)>();
                foreach (var e in g.Value.EnumerateArray())
                {
                    float fi = e.TryGetProperty("FadeInTime", out var v) ? v.GetSingle() : 0.5f;
                    float fo = e.TryGetProperty("FadeOutTime", out var w) ? w.GetSingle() : 0.5f;
                    list.Add((e.GetProperty("File").GetString()!, fi, fo));
                }
                spec.Motions[g.Name] = list;
            }
        if (root.TryGetProperty("HitAreas", out var ha))
            foreach (var h in ha.EnumerateArray())
                spec.HitAreas.Add((h.GetProperty("Id").GetString()!, h.GetProperty("Name").GetString()!));
        if (root.TryGetProperty("Groups", out var gr))
            foreach (var g in gr.EnumerateArray())
            {
                var name = g.GetProperty("Name").GetString();
                var ids = g.GetProperty("Ids").EnumerateArray().Select(x => x.GetString()!).ToArray();
                if (name == "EyeBlink") spec.EyeBlinkParamIds = ids;
                else if (name == "LipSync") spec.LipSyncParamIds = ids;
            }
        return spec;
    }
}

/// <summary>pose3.json：组内互斥部件的淡入淡出（等价 CubismPose）。</summary>
public sealed class Pose3
{
    public struct ResolvedPart { public int PartIndex; public int[] LinkPartIndices; }
    public List<ResolvedPart[]> ResolvedGroups = new();
    private float _fadeTime = 0.5f;
    private const float Epsilon = 0.001f, Phi = 0.5f, BackOpacityThreshold = 0.15f;

    public static Pose3? LoadAndResolve(string? path, Func<string, int> partIndexOf)
    {
        if (path == null || !File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var p = new Pose3();
        if (root.TryGetProperty("FadeInTime", out var fi))
        {
            var f = fi.GetSingle();
            p._fadeTime = f > 0 ? f : 0.5f;
        }
        foreach (var group in root.GetProperty("Groups").EnumerateArray())
        {
            var list = new List<ResolvedPart>();
            foreach (var item in group.EnumerateArray())
            {
                var id = item.GetProperty("Id").GetString()!;
                var links = item.TryGetProperty("Link", out var lk)
                    ? lk.EnumerateArray().Select(l => partIndexOf(l.GetString()!)).ToArray()
                    : Array.Empty<int>();
                list.Add(new ResolvedPart { PartIndex = partIndexOf(id), LinkPartIndices = links });
            }
            p.ResolvedGroups.Add(list.ToArray());
        }
        return p;
    }

    /// <summary>初始化：组内首个 part 不透明度=1，其余=0。</summary>
    public void Reset(CubismModel m)
    {
        foreach (var g in ResolvedGroups)
            for (int i = 0; i < g.Length; i++)
                if (g[i].PartIndex >= 0) m.SetPartOpacity(g[i].PartIndex, i == 0 ? 1f : 0f);
    }

    /// <summary>每帧 pose 更新：等价 CubismPose::doFade + copyPartOpacities。</summary>
    public void Update(CubismModel m, float dt)
    {
        foreach (var g in ResolvedGroups)
        {
            int visibleIdx = -1; float newOpacity = 1f;
            for (int i = 0; i < g.Length; i++)
            {
                // 框架用 part 对应 param 值>epsilon 判可见；pose 部件 param 与 part 同名，此处直接用 part 不透明度
                if (g[i].PartIndex >= 0 && m.GetPartOpacity(g[i].PartIndex) > Epsilon)
                {
                    if (visibleIdx >= 0) break;
                    visibleIdx = i;
                    newOpacity = _fadeTime > 0
                        ? MathF.Min(1f, m.GetPartOpacity(g[i].PartIndex) + dt / _fadeTime)
                        : 1f;
                }
            }
            if (visibleIdx < 0) { visibleIdx = 0; newOpacity = 1f; }

            for (int i = 0; i < g.Length; i++)
            {
                if (g[i].PartIndex < 0) continue;
                if (i == visibleIdx) { m.SetPartOpacity(g[i].PartIndex, newOpacity); continue; }
                float opacity = m.GetPartOpacity(g[i].PartIndex);
                float a1 = newOpacity < Phi
                    ? newOpacity * (Phi - 1) / Phi + 1f
                    : (1f - newOpacity) * Phi / (1f - Phi);
                float backOpacity = (1f - a1) * (1f - newOpacity);
                if (backOpacity > BackOpacityThreshold)
                    a1 = 1f - BackOpacityThreshold / (1f - newOpacity);
                m.SetPartOpacity(g[i].PartIndex, MathF.Min(opacity, a1));
            }
        }
        // link 部件不透明度跟随
        foreach (var g in ResolvedGroups)
            foreach (var p in g)
                if (p.LinkPartIndices.Length > 0 && p.PartIndex >= 0)
                    foreach (var li in p.LinkPartIndices)
                        if (li >= 0) m.SetPartOpacity(li, m.GetPartOpacity(p.PartIndex));
    }
}

/// <summary>CubismEyeBlink 等价物：interval 4s、closing 0.1s、closed 0.05s、opening 0.15s。</summary>
public sealed class EyeBlink
{
    private enum S { Interval, Closing, Closed, Opening }
    private S _state = S.Interval;
    private float _nextBlink = 2f, _stateStart, _userTime;
    private readonly Random _rng = new(12345);
    private const float Interval = 4f, ClosingT = 0.1f, ClosedT = 0.05f, OpeningT = 0.15f;

    /// <summary>返回 [0,1] 的眼部开合乘子；应用到 eyelid 参数：param *= v。</summary>
    public float Evaluate(float dt)
    {
        _userTime += dt;
        float t = _userTime - _stateStart;
        switch (_state)
        {
            case S.Closing:
                if (t >= ClosingT) { _state = S.Closed; _stateStart = _userTime; return 0f; }
                return 1f - t / ClosingT;
            case S.Closed:
                if (t >= ClosedT) { _state = S.Opening; _stateStart = _userTime; return 0f; }
                return 0f;
            case S.Opening:
                if (t >= OpeningT) { _state = S.Interval; _stateStart = _userTime; _nextBlink = _userTime + (float)_rng.NextDouble() * (2 * Interval - 1); return 1f; }
                return t / OpeningT;
            default:
                if (_userTime >= _nextBlink) { _state = S.Closing; _stateStart = _userTime; }
                return 1f;
        }
    }
}

/// <summary>CubismBreath 等价物：默认对 ParamAngleX/Y/Z、ParamBodyAngleX、ParamBreath 施加正弦呼吸偏移。</summary>
public sealed class Breath
{
    private float _t;
    private static readonly (string Id, float Weight, float Offset)[] DefaultParams =
    {
        ("ParamAngleX", 0.5f, 0f), ("ParamAngleY", 0.5f, 0f), ("ParamAngleZ", 0.5f, 0f),
        ("ParamBodyAngleX", 0.5f, 0f), ("ParamBreath", 0.5f, 0f),
    };
    private const float Cycle = 6.5345f, Peak = 3.5345f;

    public void Apply(CubismModel m, float dt)
    {
        _t += dt;
        float t = _t % Cycle;
        float v = Peak * MathF.Sin(MathF.PI * t / Cycle) * 0.5f;
        foreach (var (id, _, _) in DefaultParams)
        {
            int idx = m.GetParameterIndex(id);
            if (idx >= 0) m.AddParameterValue(idx, v);
        }
    }
}
