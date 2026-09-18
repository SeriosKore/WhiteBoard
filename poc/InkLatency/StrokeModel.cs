using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace WhiteBoard.Poc.InkLatency;

public readonly record struct InputPoint(double X, double Y, float Pressure, PointerKind Kind);

/// <summary>
/// 一条笔迹（**平滑风**，依 ADR-09）。
///
/// 干笔迹几何不再自研抖动/平滑算法，而是：
///   原始点 + DrawingAttributes → 重建 WPF <see cref="Stroke"/> → <c>GetGeometry()</c> → 冻结缓存
/// 这样得到的轮廓与 <c>InkCanvas</c> 画湿笔迹用的是**同一套几何**，
/// 实测与湿渲染**逐像素 0 差异**，抬笔时外观不会发生任何变化。
///
/// 注意渲染时必须用**填充**（<c>DrawGeometry(brush, null, geo)</c>），
/// 因为 InkCanvas 画的是"压感变宽的填充轮廓"，不是等宽描边（描边会差约 1.7%）。
/// </summary>
public sealed class InkStroke
{
    private Geometry? _cached;

    public required int Id { get; init; }
    public required Color Color { get; init; }
    public required double Width { get; init; }
    public List<InputPoint> Points { get; } = [];

    public void Add(InputPoint p) => Points.Add(p);

    /// <summary>平滑笔迹几何（与湿笔迹同源）。构建一次后冻结复用——用于**已完成**的笔迹。</summary>
    public Geometry GetSmoothGeometry()
        => _cached ??= BuildSmooth();

    /// <summary>
    /// 每次都重建（不缓存）——用于**书写过程中的实时笔迹**。
    ///
    /// 这是解决"抬笔外观突变"的关键：湿态与干态用**完全相同**的几何构建方式，
    /// 因此抬笔时点集不再变化 → 几何不再变化 → 外观零跳变。
    /// （若湿态改用 InkCanvas 自带的增量廉价渲染，抬笔时它会切换到拟合几何，就会"变细腻"。）
    /// </summary>
    public Geometry BuildSmooth()
    {
        if (Points.Count == 0) return Geometry.Empty;

        var spc = new StylusPointCollection();
        foreach (var p in Points)
            spc.Add(new StylusPoint(p.X, p.Y, Math.Clamp(p.Pressure <= 0 ? 0.5f : p.Pressure, 0.01f, 1f)));

        var attrs = new DrawingAttributes
        {
            Color = Color,
            Width = Width,
            Height = Width,
            StylusTip = StylusTip.Ellipse,
            FitToCurve = true,
            IgnorePressure = false
        };

        var stroke = new Stroke(spc, attrs);
        var geo = stroke.GetGeometry();
        geo.Freeze();
        return geo;
    }

    /// <summary>实时笔迹用的廉价折线几何（逐帧构建，不做几何填充）。</summary>
    public static Geometry BuildRawPolyline(IReadOnlyList<InputPoint> pts)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            if (pts.Count == 1)
            {
                var p = pts[0];
                ctx.BeginFigure(new Point(p.X, p.Y), false, false);
                ctx.LineTo(new Point(p.X + 0.01, p.Y), true, false);
            }
            else if (pts.Count > 1)
            {
                ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), false, false);
                for (var i = 1; i < pts.Count; i++)
                    ctx.LineTo(new Point(pts[i].X, pts[i].Y), true, false);
            }
        }
        geo.Freeze();
        return geo;
    }
}
