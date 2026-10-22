using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Live2D.Core;
using SkiaSharp;
using static Live2D.Core.CubismCore;

namespace ProbeNative;

/// <summary>
/// Live2D 渲染视图：模型 drawable 网格 → SKCanvas.DrawVertices（纹理 shader + alpha）。
/// 顶点坐标经 CubismModel.ToScreen 映射；蒙版 drawables 用 ClipPath 合成。
/// </summary>
public sealed class Live2DSkiaView : Control
{
    public CubismModel? Model;
    private readonly Dictionary<int, SKBitmap> _textures = new();
    private CubismModel.DrawableSnapshot[] _frame = Array.Empty<CubismModel.DrawableSnapshot>();
    private SKPaint _paint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
    private readonly Dictionary<int, SKShader> _shaders = new();
    public int HitDrawableIndex = -1;
    public Point? LastPointer;
    public bool ShowHitRegions;
    public bool NoMask = Environment.GetEnvironmentVariable("FUFU_NO_MASK") == "1";
    private readonly object _renderLock = new();   // LoadModel(UI线程) 与 DrawSkia(渲染线程) 互斥

    public void LoadModel(CubismModel model)
    {
        lock (_renderLock)
        {
            foreach (var b in _textures.Values) b.Dispose();
            foreach (var s in _shaders.Values) s.Dispose();
            _textures.Clear(); _shaders.Clear();
            Model = model;
            if (model.Spec != null)
            {
                for (int i = 0; i < model.Spec.Textures.Length; i++)
                {
                    var path = Path.Combine(model.Spec.Dir, model.Spec.Textures[i]);
                    if (File.Exists(path))
                    {
                        var bmp = SKBitmap.Decode(path);
                        _textures[i] = bmp;
                        _shaders[i] = SKShader.CreateBitmap(bmp, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                    }
                }
            }
        }
        InvalidateVisual();
    }

    /// <summary>点击命中：返回命中的 HitArea 名（或任意可见 drawable → "Model"）。屏幕坐标。</summary>
    public string? HitTest(Point p)
    {
        if (Model == null) return null;
        // 优先按 model3.json HitAreas（drawable id → name）
        if (Model.Spec != null)
            foreach (var (id, name) in Model.Spec.HitAreas)
            {
                int di = Model.GetDrawableIndex(id);
                if (di >= 0 && PointInDrawable(di, p)) return name;
            }
        // 回退：任意可见 drawable 的网格
        foreach (var d in _frame)
            if (d.Visible && PointInDrawable(d.DrawableIndex, p)) return "Model";
        return null;
    }

    public bool PointInDrawable(int drawableIndex, Point p)
    {
        if (Model == null || drawableIndex < 0) return false;
        var d = _frame.FirstOrDefault(x => x.DrawableIndex == drawableIndex);
        if (d == null) return false;
        var w = (float)Bounds.Width; var h = (float)Bounds.Height;
        for (int t = 0; t < d.IndexCount; t += 3)
        {
            var a = Model.ToScreen(d.Vertices[d.Indices[t]], w, h);
            var b = Model.ToScreen(d.Vertices[d.Indices[t + 1]], w, h);
            var c = Model.ToScreen(d.Vertices[d.Indices[t + 2]], w, h);
            if (PointInTriangle((float)p.X, (float)p.Y, a, b, c)) return true;
        }
        return false;
    }

    private static bool PointInTriangle(float px, float py, (float X, float Y) a, (float X, float Y) b, (float X, float Y) c)
    {
        float d1 = Sign(px, py, a, b), d2 = Sign(px, py, b, c), d3 = Sign(px, py, c, a);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
        static float Sign(float px, float py, (float X, float Y) p1, (float X, float Y) p2)
            => (px - p2.X) * (p1.Y - p2.Y) - (p1.X - p2.X) * (py - p2.Y);
    }

    public void RenderTick()
    {
        if (Model == null) return;
        _frame = Model.SnapshotDrawables();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.Custom(new DrawOp(new Rect(0, 0, Bounds.Width, Bounds.Height), this));
    }

    private void DrawSkia(SKCanvas canvas, Rect bounds)
    {
        lock (_renderLock)
        {
        canvas.Clear(SKColors.Transparent);
        if (Model == null || _frame.Length == 0) return;

        float w = (float)bounds.Width, h = (float)bounds.Height;
        // 蒙版路径缓存（drawableIndex → path）
        var maskPaths = new Dictionary<int, SKPath>();
        SKPath PathOf(CubismModel.DrawableSnapshot d)
        {
            if (maskPaths.TryGetValue(d.DrawableIndex, out var p)) return p;
            var path = new SKPath();
            for (int t = 0; t < d.IndexCount; t += 3)
            {
                var a = Model.ToScreen(d.Vertices[d.Indices[t]], w, h);
                var b = Model.ToScreen(d.Vertices[d.Indices[t + 1]], w, h);
                var c = Model.ToScreen(d.Vertices[d.Indices[t + 2]], w, h);
                path.MoveTo(a.Sx, a.Sy); path.LineTo(b.Sx, b.Sy); path.LineTo(c.Sx, c.Sy); path.Close();
            }
            maskPaths[d.DrawableIndex] = path;
            return path;
        }

        foreach (var d in _frame.OrderBy(x => x.RenderOrder))
        {
            if (!d.Visible) continue;
            if (ShowHitRegions) { _ = PathOf(d); }
            int saved = -1;
            if (!NoMask && d.MaskIndices is { Length: > 0 })
            {
                saved = canvas.Save();
                var clip = new SKPath();
                foreach (var mi in d.MaskIndices)
                {
                    var md = _frame.FirstOrDefault(x => x.DrawableIndex == mi);
                    if (md != null) clip.AddPath(PathOf(md));
                }
                canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
            }

            if (_textures.TryGetValue(d.TextureIndex, out var bmp))
            {
                var pts = new SKPoint[d.VertexCount];
                var tex = new SKPoint[d.VertexCount];
                for (int i = 0; i < d.VertexCount; i++)
                {
                    var s = Model.ToScreen(d.Vertices[i], w, h);
                    pts[i] = new SKPoint(s.Sx, s.Sy);
                    // Cubism UV v=0 为贴图底部（GL 约定），Skia bitmap 行 0 在顶部 → 翻转 v
                    tex[i] = new SKPoint(d.Uvs[i].X * bmp.Width, (1 - d.Uvs[i].Y) * bmp.Height);
                }
                float alpha = Math.Clamp(d.Opacity * Model.ModelOpacity, 0, 1);
                _paint.Shader = _shaders[d.TextureIndex];
                _paint.Color = new SKColor(255, 255, 255, (byte)(alpha * 255));
                _paint.BlendMode = d.BlendAdditive ? SKBlendMode.Plus : d.BlendMultiplicative ? SKBlendMode.Multiply : SKBlendMode.SrcOver;
                canvas.DrawVertices(SKVertexMode.Triangles, pts, tex, null, _paint.BlendMode, d.Indices, _paint);
            }
            if (saved >= 0) canvas.RestoreToCount(saved);
        }

        if (ShowHitRegions)
        {
            using var lp = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(255, 0, 0, 120), StrokeWidth = 1, IsAntialias = true };
            foreach (var p in maskPaths.Values) canvas.DrawPath(p, lp);
        }
        }
    }

    private sealed class DrawOp : ICustomDrawOperation
    {
        private readonly Live2DSkiaView _view;
        public DrawOp(Rect bounds, Live2DSkiaView view) { Bounds = bounds; _view = view; }
        public Rect Bounds { get; }
        public void Dispose() { }
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature != null)
            {
                using var api = feature.Lease();
                _view.DrawSkia(api.SkCanvas, Bounds);
            }
        }
    }
}
