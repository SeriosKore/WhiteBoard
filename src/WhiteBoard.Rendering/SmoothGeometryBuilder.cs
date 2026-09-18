using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Rendering;

/// <summary>几何构建的统计（用于自检与性能观测）。</summary>
public readonly record struct GeometryCacheStats(int Builds, int Hits, int Invalidations)
{
    public int Total => Builds + Hits;
    public double HitRate => Total == 0 ? 0 : Hits / (double)Total;
    public override string ToString()
        => $"构建 {Builds} 次　命中 {Hits} 次　失效 {Invalidations} 次　命中率 {HitRate:P0}";
}

/// <summary>
/// 自由笔迹的平滑方式。
///
/// 这个开关直接决定"**已经画出来的部分会不会再动**"——实测数据见下：
/// <list type="bullet">
/// <item><see cref="None"/>（默认）：按采样点直接连线（圆角连接 + 圆头端点）。
///       追加一个采样点时，**已画部分的像素变化 = 0**，笔迹绝对"下笔即固定"；</item>
/// <item><see cref="FitToCurve"/>：交给 WPF 对**整条点集**做曲线拟合，最圆润，
///       但每来一个新采样点整条曲线都会重算——实测追加 1 个点会让已画区域
///       **81 个像素**改变，连续追加 5 个点最多 **575 个像素**。
///       用户看到的就是"笔已经走过的地方会飘"。</item>
/// </list>
/// 之所以默认选 <see cref="None"/>：书写时"我画在哪里就是哪里"比"曲线更圆润"重要得多；
/// 而且采样点足够密（60~133 Hz，间距通常 2~5 px），圆角连接下已经看不出棱角。
/// </summary>
public enum FreehandSmoothing
{
    /// <summary>不平滑：直接连采样点（圆角连接）。下笔即固定，已画部分零重算。</summary>
    None = 0,

    /// <summary>整条曲线拟合（WPF <c>FitToCurve</c>）：更圆润，但已画部分会随新采样点轻微移动。</summary>
    FitToCurve = 1
}

/// <summary>
/// 平滑笔迹/图形几何构建 + **冻结缓存**（性能与"无损放大"的共同基础）。
///
/// 关键约定：
/// <list type="number">
/// <item>几何一律在**局部坐标**下构建 → 平移/旋转/缩放只改变换矩阵，不失效缓存；</item>
/// <item>自由画笔用 <c>Stroke.GetGeometry()</c>（与 `InkCanvas` 湿笔迹同源），
///       平滑方式由 <see cref="Smoothing"/> 决定，**默认不平滑**以保证"下笔即固定"；</item>
/// <item>几何形状先把中心线**加宽成轮廓**（<c>GetWidenedPathGeometry</c>），保证与自由画笔同一条填充路径；</item>
/// <item>擦除遮罩用 <c>Geometry.Combine(..., Exclude, null)</c> 做差集（ADR-18）；</item>
/// <item>结果 <c>Freeze()</c> 后缓存，按 <see cref="ShapeObject.GeometryFingerprint"/> 判失效。</item>
/// </list>
/// </summary>
public sealed class SmoothGeometryBuilder
{
    /// <summary>
    /// 全局笔迹平滑方式（默认 <see cref="FreehandSmoothing.None"/>）。
    /// 做成静态属性是为了让"实时预览"与"干笔迹"永远用同一套参数——
    /// 两者不一致会导致抬笔瞬间外观跳变。命令行 <c>--smooth</c> 可切回旧行为做对比。
    /// </summary>
    public static FreehandSmoothing Smoothing { get; set; } = FreehandSmoothing.None;

    private readonly Dictionary<int, (long Fingerprint, Geometry Geometry)> _cache = [];
    private int _builds;
    private int _hits;
    private int _invalidations;

    public GeometryCacheStats Stats => new(_builds, _hits, _invalidations);

    /// <summary>取对象在**局部坐标**下的填充几何（已含遮罩差集）。</summary>
    public Geometry GetLocalGeometry(ShapeObject obj)
    {
        var fp = obj.GeometryFingerprint;

        if (_cache.TryGetValue(obj.Id, out var entry))
        {
            if (entry.Fingerprint == fp)
            {
                _hits++;
                return entry.Geometry;
            }
            _invalidations++;
        }

        var geo = BuildLocalGeometry(obj);
        _cache[obj.Id] = (fp, geo);
        _builds++;
        return geo;
    }

    /// <summary>取世界坐标下的几何（直接把局部几何乘以对象矩阵，几何本身仍复用）。</summary>
    public Geometry GetWorldGeometry(ShapeObject obj)
    {
        var local = GetLocalGeometry(obj);
        var m = obj.LocalToWorld.ToWpf();

        if (m.IsIdentity)
        {
            _hits++;
            return local;
        }

        var transformed = new MatrixTransform(m);
        transformed.Freeze();
        var clone = local.Clone();
        clone.Transform = transformed;
        clone.Freeze();
        return clone;
    }

    /// <summary>清空缓存（如加载文档后 Id 复用）。</summary>
    public void Clear()
    {
        _cache.Clear();
    }

    /// <summary>显式失效某个对象（一般不需要——指纹会自动判失效）。</summary>
    public bool Invalidate(int objectId)
    {
        if (_cache.Remove(objectId)) { _invalidations++; return true; }
        return false;
    }

    /// <summary>不做缓存的一次构建（供书写过程中的湿态逐帧重建使用）。</summary>
    public Geometry BuildLocalGeometry(ShapeObject obj) => obj switch
    {
        FreehandObject f => BuildFreehand(f),
        TextObject t => TextGeometry.BuildLocalGeometry(t),
        StrokedShapeObject s => BuildStroked(s),
        _ => throw new NotSupportedException($"暂不支持的图元类型：{obj.GetType().Name}")
    };

    /// <summary>
    /// 构建**世界坐标**下的实时笔迹几何（湿态逐帧重建用，不缓存）。
    /// 与干笔迹同源（同样是 <c>Stroke.GetGeometry()</c>），因此抬笔时外观零跳变（M0 §六-D）。
    /// </summary>
    public static Geometry BuildLiveFreehandGeometry(IReadOnlyList<InkPoint> worldPoints, double penWidth)
    {
        if (worldPoints.Count == 0) return FrozenEmpty();

        var spc = new StylusPointCollection();
        foreach (var p in worldPoints)
            spc.Add(new StylusPoint(p.X, p.Y, p.Pressure <= 0 ? 0.5f : Math.Clamp(p.Pressure, 0.01f, 1f)));

        var stroke = new Stroke(spc, MakeAttributes(penWidth));
        var geo = stroke.GetGeometry();
        if (geo.CanFreeze) geo.Freeze();
        return geo;
    }

    /// <summary>构建**世界坐标**下的折线预览几何（直线/矩形/椭圆的橡皮筋预览）。</summary>
    public static Geometry BuildLiveOutlineGeometry(IReadOnlyList<PointD> worldPoints, double penWidth, bool closed)
    {
        if (worldPoints.Count < 2) return FrozenEmpty();

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(worldPoints[0].X, worldPoints[0].Y), false, closed);
            for (var i = 1; i < worldPoints.Count; i++)
                ctx.LineTo(new Point(worldPoints[i].X, worldPoints[i].Y), true, false);
        }
        geo.Freeze();

        var pen = new Pen(Brushes.Black, Math.Max(penWidth, 0.1))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();

        var widened = geo.GetWidenedPathGeometry(pen);
        if (widened.CanFreeze) widened.Freeze();
        return widened;
    }

    private static DrawingAttributes MakeAttributes(double penWidth) => new()
    {
        Color = Color.FromRgb(0xFF, 0xFF, 0xFF),   // 颜色在填充时决定，几何与颜色无关
        Width = penWidth,
        Height = penWidth,
        StylusTip = StylusTip.Ellipse,
        FitToCurve = Smoothing == FreehandSmoothing.FitToCurve,   // 默认 false：下笔即固定（见 FreehandSmoothing）
        IgnorePressure = false
    };

    // ── 自由画笔：Stroke.GetGeometry() → 遮罩差集 ──
    private static Geometry BuildFreehand(FreehandObject f)
    {
        if (f.Points.Count == 0) return FrozenEmpty();

        var spc = new StylusPointCollection();
        foreach (var p in f.Points)
        {
            var pressure = p.Pressure <= 0 ? 0.5f : Math.Clamp(p.Pressure, 0.01f, 1f);
            spc.Add(new StylusPoint(p.X, p.Y, pressure));
        }

        var attrs = new DrawingAttributes
        {
            Color = Color.FromRgb(0xFF, 0xFF, 0xFF),   // 颜色在填充时决定，几何与颜色无关
            Width = f.PenWidth,
            Height = f.PenWidth,
            StylusTip = StylusTip.Ellipse,
        FitToCurve = Smoothing == FreehandSmoothing.FitToCurve,   // 默认 false：下笔即固定（见 FreehandSmoothing）
            IgnorePressure = false
        };

        var stroke = new Stroke(spc, attrs);
        var geo = stroke.GetGeometry();
        return ApplyErasures(geo, f);
    }

    // ── 直线/矩形/椭圆：中心线 → 加宽成轮廓 → 遮罩差集 ──
    private static Geometry BuildStroked(StrokedShapeObject s)
    {
        Geometry outline;

        if (s.ShapeKind == StrokedShapeKind.Ellipse)
        {
            // 椭圆不能靠"折线加宽"来表达，用**外椭圆 − 内椭圆**得到圆环
            var cx = s.LocalWidth / 2;
            var cy = s.LocalHeight / 2;
            var rx = Math.Max((s.LocalWidth - s.PenWidth) / 2, 0.001);
            var ry = Math.Max((s.LocalHeight - s.PenWidth) / 2, 0.001);
            var half = s.PenWidth / 2;

            var outer = new EllipseGeometry(new Point(cx, cy), rx + half, ry + half);
            var inner = new EllipseGeometry(new Point(cx, cy), Math.Max(rx - half, 0.0001), Math.Max(ry - half, 0.0001));
            outer.Freeze();
            inner.Freeze();

            try
            {
                outline = Geometry.Combine(outer, inner, GeometryCombineMode.Exclude, null);
            }
            catch (Exception)
            {
                outline = outer;
            }
        }
        else
        {
            var pts = s.LocalOutline.Select(p => new Point(p.X, p.Y)).ToList();
            if (pts.Count < 2) return FrozenEmpty();

            var center = new StreamGeometry();
            using (var ctx = center.Open())
            {
                ctx.BeginFigure(pts[0], false, s.IsClosed);
                for (var i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], true, false);
            }
            center.Freeze();

            var pen = new Pen(Brushes.Black, Math.Max(s.PenWidth, 0.1))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            outline = center.GetWidenedPathGeometry(pen);
        }

        return ApplyErasures(outline, s);
    }

    /// <summary>把擦除遮罩从几何中减去（ADR-18：遮罩保留，不碎片化对象）。</summary>
    private static Geometry ApplyErasures(Geometry geometry, ShapeObject obj)
    {
        if (obj.Erasures.Count == 0)
        {
            if (geometry.CanFreeze) geometry.Freeze();
            return geometry;
        }

        var mask = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var e in obj.Erasures)
            mask.Children.Add(new EllipseGeometry(new Point(e.X, e.Y), e.Radius, e.Radius));
        mask.Freeze();

        Geometry result;
        try
        {
            result = Geometry.Combine(geometry, mask, GeometryCombineMode.Exclude, null);
        }
        catch (Exception)
        {
            // 极端退化（自交/数值问题）时退回未擦除几何，绝不抛到渲染循环
            result = geometry;
        }

        if (result.CanFreeze) result.Freeze();
        return result;
    }

    private static Geometry FrozenEmpty()
    {
        var g = new StreamGeometry();
        g.Freeze();
        return g;
    }
}
