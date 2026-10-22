using System.Runtime.InteropServices;
using static Live2D.Core.CubismCore;

namespace Live2D.Core;

/// <summary>
/// 基于 Live2DCubismCore.dll 的最小模型封装：
/// moc 载入 → 参数管线（motion 加权 + pose + eyeBlink + breath）→ csmUpdateModel → 取 drawable 快照。
/// 注意：SDK5 的 Core 只做"参数→顶点"计算，渲染由上层（本 probe 的 Skia）完成。
/// 未实现：physics3.json（见 REPORT 已知问题）、expression3.json、userdata3.json。
/// </summary>
public sealed unsafe class CubismModel : IDisposable
{
    private IntPtr _mocBuf, _moc, _model;
    private uint _mocSize, _modelSize;
    private float* _paramValues; private int _paramCount;
    private float* _partOpacities; private int _partCount;
    public string[] ParamIds = Array.Empty<string>();
    public string[] PartIds = Array.Empty<string>();
    public string[] DrawableIds = Array.Empty<string>();
    public int[] PartParent = Array.Empty<int>();

    // canvas 信息（单位制：顶点坐标单位 = canvasPixels / ppu，原点在 originInPixels/ppu）
    public float CanvasWPx, CanvasHPx, OriginPxX, OriginPxY, Ppu;
    public float CanvasWUnits => CanvasWPx / Ppu;
    public float CanvasHUnits => CanvasHPx / Ppu;

    public int DrawableCount { get; private set; }

    private readonly List<ActiveMotion> _active = new();
    private Pose3? _pose;
    private readonly EyeBlink _eyeBlink = new();
    private readonly Breath _breath = new();
    public float ModelOpacity = 1f;
    public float Clock;
    public ModelSpec? Spec;
    private int[] _eyeBlinkParamIdx = Array.Empty<int>();
    private int[] _lipSyncParamIdx = Array.Empty<int>();

    public static CubismModel Load(string model3JsonPath)
    {
        var spec = ModelSpec.Load(model3JsonPath);
        return Load(spec);
    }

    public static CubismModel Load(ModelSpec spec)
    {
        VendorPaths.EnsureNativeCore();
        var m = new CubismModel { Spec = spec };

        byte[] mocBytes = File.ReadAllBytes(Path.Combine(spec.Dir, spec.Moc));
        m._mocSize = (uint)mocBytes.Length;
        m._mocBuf = (IntPtr)NativeMemory.AlignedAlloc(m._mocSize + CubismCore.AlignofMoc, CubismCore.AlignofMoc);
        Marshal.Copy(mocBytes, 0, m._mocBuf, mocBytes.Length);

        if (csmHasMocConsistency(m._mocBuf, m._mocSize) != 1)
            throw new InvalidDataException("moc3 consistency check failed (corrupt or unsupported moc version).");
        var mocVer = csmGetMocVersion(m._mocBuf, m._mocSize);

        m._moc = csmReviveMocInPlace(m._mocBuf, m._mocSize);
        if (m._moc == IntPtr.Zero) throw new InvalidOperationException("csmReviveMocInPlace failed");

        m._modelSize = csmGetSizeofModel(m._moc);
        m._model = (IntPtr)NativeMemory.AlignedAlloc(m._modelSize + CubismCore.AlignofModel, CubismCore.AlignofModel);
        var init = csmInitializeModelInPlace(m._moc, m._model, m._modelSize);
        if (init == IntPtr.Zero) throw new InvalidOperationException("csmInitializeModelInPlace failed");
        m._model = init;

        csmUpdateModel(m._model);
        Vec2 sizePx, originPx; float ppu;
        csmReadCanvasInfo(m._model, out sizePx, out originPx, out ppu);
        m.CanvasWPx = sizePx.X; m.CanvasHPx = sizePx.Y;
        m.OriginPxX = originPx.X; m.OriginPxY = originPx.Y; m.Ppu = ppu;

        m._paramCount = csmGetParameterCount(m._model);
        m._partCount = csmGetPartCount(m._model);
        m.ParamIds = ReadStringTable(csmGetParameterIds(m._model), m._paramCount);
        m.PartIds = ReadStringTable(csmGetPartIds(m._model), m._partCount);
        m._paramValues = Ptr(csmGetParameterValues(m._model));
        m._partOpacities = Ptr(csmGetPartOpacities(m._model));
        m.PartParent = new int[m._partCount];
        var pp = IPtr(csmGetPartParentPartIndices(m._model));
        for (int i = 0; i < m._partCount; i++) m.PartParent[i] = pp[i];

        m.DrawableCount = csmGetDrawableCount(m._model);
        m.DrawableIds = ReadStringTable(csmGetDrawableIds(m._model), m.DrawableCount);

        if (spec.Pose != null)
        {
            m._pose = Pose3.LoadAndResolve(Path.Combine(spec.Dir, spec.Pose), m.GetPartIndex);
            m._pose?.Reset(m);
        }
        m._eyeBlinkParamIdx = spec.EyeBlinkParamIds.Select(m.GetParameterIndex).Where(i => i >= 0).ToArray();
        m._lipSyncParamIdx = spec.LipSyncParamIds.Select(m.GetParameterIndex).Where(i => i >= 0).ToArray();
        return m;
    }

    public int GetParameterIndex(string id)
    {
        for (int i = 0; i < ParamIds.Length; i++) if (ParamIds[i] == id) return i;
        return -1;
    }

    public int GetPartIndex(string id)
    {
        for (int i = 0; i < PartIds.Length; i++) if (PartIds[i] == id) return i;
        return -1;
    }

    public int GetDrawableIndex(string id)
    {
        for (int i = 0; i < DrawableIds.Length; i++) if (DrawableIds[i] == id) return i;
        return -1;
    }

    public float GetParameterValue(int i) => _paramValues[i];
    public void SetParameterValue(int i, float v) => _paramValues[i] = v;
    public void AddParameterValue(int i, float v) => _paramValues[i] += v;
    public float GetPartOpacity(int i) => _partOpacities[i];
    public void SetPartOpacity(int i, float v) => _partOpacities[i] = v;

    /// <summary>播放一组 motion 中随机一个；group 不存在则直接按文件名试。</summary>
    public bool PlayRandomMotion(string group, out string file)
    {
        file = "";
        if (Spec == null) return false;
        List<(string File, float FadeIn, float FadeOut)>? list = null;
        if (!Spec.Motions.TryGetValue(group, out list))
            list = Spec.Motions.SelectMany(kv => kv.Value).ToList();
        if (list == null || list.Count == 0) return false;
        var pick = list[Random.Shared.Next(list.Count)];
        file = pick.File;
        return PlayMotion(Path.Combine(Spec.Dir, pick.File), pick.FadeIn, pick.FadeOut, loop: false);
    }

    public bool PlayMotion(string path, float fadeIn, float fadeOut, bool loop)
    {
        if (!File.Exists(path)) return false;
        var motion = Motion3Json.Load(path);
        foreach (var a in _active) a.Finished = true;
        _active.Add(new ActiveMotion { Motion = motion, StartTime = Clock, Loop = loop });
        var a2 = _active[^1];
        if (!loop) a2.EndTime = Clock + motion.Duration + fadeIn;
        // 记录淡入淡出参数到实例
        a2.FadeIn = fadeIn; a2.FadeOut = fadeOut;
        return true;
    }

    /// <summary>每帧：应用活动 motion（带 fade），再 pose / eyeblink / breath，然后 csmUpdateModel。</summary>
    public void Update(float dt)
    {
        Clock += dt;

        // 1) 动作：所有 active motion 叠写（框架语义 v += (mv-v)*w）
        foreach (var a in _active)
        {
            if (a.Finished) continue;
            float t = Clock - a.StartTime;
            float duration = a.Motion.Duration;
            float w = a.FadeWeight(Clock, a.FadeIn, a.FadeOut);
            float evalTime = a.Loop && duration > 0 ? t % duration : MathF.Min(t, duration);

            float eyeBlinkVal = float.MaxValue, lipSyncVal = float.MaxValue;
            foreach (var c in a.Motion.Curves)
            {
                float v = a.Motion.Evaluate(c, evalTime);
                switch (c.Type)
                {
                    case Motion3Json.Target.Model:
                        if (c.Id == "Opacity") ModelOpacity = v;
                        else if (c.Id == "EyeBlink") eyeBlinkVal = v;
                        else if (c.Id == "LipSync") lipSyncVal = v;
                        break;
                    case Motion3Json.Target.Parameter:
                    {
                        int pi = GetParameterIndex(c.Id);
                        if (pi < 0) break;
                        // 眼眨/口型曲线特判（框架同序：先乘 eyeBlink，再加 lipSync）
                        if (eyeBlinkVal != float.MaxValue && _eyeBlinkParamIdx.Contains(pi)) v *= eyeBlinkVal;
                        if (lipSyncVal != float.MaxValue && _lipSyncParamIdx.Contains(pi)) v += lipSyncVal;
                        _paramValues[pi] += (v - _paramValues[pi]) * w;
                        break;
                    }
                    case Motion3Json.Target.PartOpacity:
                    {
                        int pi = GetPartIndex(c.Id);
                        if (pi >= 0) _partOpacities[pi] += (v - _partOpacities[pi]) * w;
                        break;
                    }
                }
            }
            if (!a.Loop && t >= duration + a.FadeIn) a.Finished = true;
        }
        _active.RemoveAll(a => a.Finished);

        // 2) 无动作时的常驻效果
        float blink = _eyeBlink.Evaluate(dt);
        foreach (var pi in _eyeBlinkParamIdx) _paramValues[pi] *= blink;
        _breath.Apply(this, dt);

        // 3) pose（部件互斥淡入淡出）
        _pose?.Update(this, dt);

        csmUpdateModel(_model);
    }

    /// <summary>渲染快照：一帧的 drawable 绘制数据（按 renderOrder 排序后调用方遍历）。</summary>
    public DrawableSnapshot[] SnapshotDrawables()
    {
        int n = DrawableCount;
        var dyn = BPtr(csmGetDrawableDynamicFlags(_model));
        var con = BPtr(csmGetDrawableConstantFlags(_model));
        var tex = IPtr(csmGetDrawableTextureIndices(_model));
        var renderOrder = IPtr(csmGetDrawableRenderOrders(_model));
        var opac = Ptr(csmGetDrawableOpacities(_model));
        var maskCnt = IPtr(csmGetDrawableMaskCounts(_model));
        var masks = PPtr(csmGetDrawableMasks(_model));
        var vcnt = IPtr(csmGetDrawableVertexCounts(_model));
        var icnt = IPtr(csmGetDrawableIndexCounts(_model));
        var vpos = PPtr(csmGetDrawableVertexPositions(_model));
        var vuv = PPtr(csmGetDrawableVertexUvs(_model));
        var idx = PPtr(csmGetDrawableIndices(_model));
        var parentPart = IPtr(csmGetDrawableParentPartIndices(_model));

        var list = new DrawableSnapshot[n];
        for (int i = 0; i < n; i++)
        {
            var d = new DrawableSnapshot
            {
                DrawableIndex = i,
                Id = DrawableIds[i],
                RenderOrder = renderOrder[i],
                TextureIndex = tex[i],
                Opacity = opac[i],
                Visible = (dyn[i] & 0x01) != 0,
                BlendAdditive = (con[i] & 0x01) != 0,
                BlendMultiplicative = (con[i] & 0x02) != 0,
                InvertedMask = (con[i] & 0x08) != 0,
                VertexCount = vcnt[i],
                IndexCount = icnt[i],
                ParentPartIndex = parentPart[i],
            };
            int mc = maskCnt[i];
            if (mc > 0)
            {
                d.MaskIndices = new int[mc];
                var mp = (int*)masks[i];
                for (int k = 0; k < mc; k++) d.MaskIndices[k] = mp[k];
            }
            d.Vertices = new Vec2[d.VertexCount];
            d.Uvs = new Vec2[d.VertexCount];
            var vp = (Vec2*)vpos[i]; var uv = (Vec2*)vuv[i];
            for (int k = 0; k < d.VertexCount; k++) { d.Vertices[k] = vp[k]; d.Uvs[k] = uv[k]; }
            d.Indices = new ushort[d.IndexCount];
            var ip = (ushort*)idx[i];
            for (int k = 0; k < d.IndexCount; k++) d.Indices[k] = ip[k];
            list[i] = d;
        }
        return list;
    }

    /// <summary>把 drawable 顶点坐标（model units，origin 为原点、Y 向上）映射到控件坐标（Y 向下、居中、等比缩放）。</summary>
    public (float Sx, float Sy) ToScreen(Vec2 v, float ctrlW, float ctrlH)
    {
        // vertex 单位 = canvas 像素/ppu，以 origin 为原点、Y 向上。
        // 画布矩形在单位空间为 x∈[-ox/ppu, (w-ox)/ppu], y∈[-(h-oy)/ppu, oy/ppu]
        float uX = v.X + OriginPxX / Ppu;   // → [0, canvasWUnits]
        float uY = OriginPxY / Ppu - v.Y;   // → [0, canvasHUnits]（Y 翻转）
        float s = MathF.Min(ctrlW / CanvasWUnits, ctrlH / CanvasHUnits);
        float offX = (ctrlW - CanvasWUnits * s) / 2, offY = (ctrlH - CanvasHUnits * s) / 2;
        return (offX + uX * s, offY + uY * s);
    }

    public void Dispose()
    {
        if (_model != IntPtr.Zero) NativeMemory.AlignedFree((void*)_model);
        if (_mocBuf != IntPtr.Zero) NativeMemory.AlignedFree((void*)_mocBuf);
        _model = _mocBuf = _moc = IntPtr.Zero;
        _paramValues = null; _partOpacities = null;
    }

    public sealed class DrawableSnapshot
    {
        public int DrawableIndex, RenderOrder, TextureIndex, VertexCount, IndexCount, ParentPartIndex;
        public string Id = "";
        public float Opacity;
        public bool Visible, BlendAdditive, BlendMultiplicative, InvertedMask;
        public int[]? MaskIndices;
        public Vec2[] Vertices = Array.Empty<Vec2>();
        public Vec2[] Uvs = Array.Empty<Vec2>();
        public ushort[] Indices = Array.Empty<ushort>();
    }
}
