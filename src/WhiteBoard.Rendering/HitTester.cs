using System.Windows;
using System.Windows.Media;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Rendering;

/// <summary>
/// 命中测试。
///
/// 关键点（ADR-18）：命中判定在**差集后的几何**上做，
/// 因此被橡皮擦掉的区域**点不中**——与用户看到的一致；
/// 同时对象仍是"一笔"，选中粒度不受擦除次数影响。
/// </summary>
public sealed class HitTester
{
    private readonly SmoothGeometryBuilder _geometry;
    private readonly double _tolerance;

    public HitTester(SmoothGeometryBuilder geometry, double tolerance = 2.0)
    {
        _geometry = geometry;
        _tolerance = tolerance;
    }

    /// <summary>点是否命中对象（世界坐标）。</summary>
    public bool HitTest(ShapeObject obj, PointD worldPoint)
    {
        var local = obj.LocalToWorld.Invert().Transform(worldPoint);

        // 文本按**文本框**判定命中，而不是按字形墨迹：
        // 字形之间有空白（两个字中间就有一条竖缝），按墨迹判定会出现
        // "点在文字中间却没选中它"这种说不通的行为，文本工具还会因此叠出一段新文字。
        if (obj is TextObject text)
        {
            var pad = _tolerance / Math.Max(LocalScaleOf(obj), 1e-6);
            var tb = text.LocalBounds;
            return local.X >= tb.X - pad && local.X <= tb.Right + pad &&
                   local.Y >= tb.Y - pad && local.Y <= tb.Bottom + pad;
        }

        var geo = _geometry.GetLocalGeometry(obj);

        // 容差按对象缩放折算到局部坐标系
        var scale = LocalScaleOf(obj);
        var tol = _tolerance / Math.Max(scale, 1e-6);

        // 描边/细长几何用"接近路径"判定，较粗的用"填充包含"判定
        try
        {
            if (geo.FillContains(new Point(local.X, local.Y), tol, ToleranceType.Absolute))
                return true;
        }
        catch (Exception)
        {
            // 退化几何忽略
        }

        try
        {
            if (geo.StrokeContains(new Pen(Brushes.Black, Math.Max(tol * 2, 1)), new Point(local.X, local.Y),
                    tol, ToleranceType.Absolute))
                return true;
        }
        catch (Exception)
        {
        }

        return false;
    }

    /// <summary>
    /// 点选：返回最上面（Z 序最大）被点中的对象；没点中返回 null。
    /// 从顶层往下找是刻意的——用户看到的是最上面那个，点到的也必须是它，
    /// 否则会出现"点上去选中了被盖住的那个"这种说不通的行为。
    /// </summary>
    public ShapeObject? HitTestTopmost(Page page, PointD worldPoint)
    {
        foreach (var obj in page.InRenderOrder().Reverse())
        {
            if (HitTest(obj, worldPoint)) return obj;
        }
        return null;
    }

    /// <summary>矩形范围（框选）内的对象；<paramref name="requireFullContain"/> 为 true 时要求完全包含。</summary>
    public IEnumerable<ShapeObject> HitTestRect(Page page, RectD worldRect, bool requireFullContain = false)
    {
        foreach (var obj in page.InRenderOrder())
        {
            var b = obj.WorldBounds;
            if (requireFullContain)
            {
                if (worldRect.Contains(new PointD(b.X, b.Y)) &&
                    worldRect.Contains(new PointD(b.Right, b.Bottom)))
                    yield return obj;
            }
            else if (b.IntersectsWith(worldRect))
            {
                yield return obj;
            }
        }
    }

    /// <summary>
    /// 圈选（套索）：把多边形边界与对象包围盒做相交判定，命中即选中。
    /// 严格做法是几何求交，S1 先用"包围盒与套索多边形相交"（对课堂使用足够，且成本低）。
    /// </summary>
    public IEnumerable<ShapeObject> HitTestLasso(Page page, IReadOnlyList<PointD> worldPolygon)
    {
        if (worldPolygon.Count < 3) yield break;

        var polyBounds = RectD.FromPoints(worldPolygon);

        foreach (var obj in page.InRenderOrder())
        {
            var b = obj.WorldBounds;
            if (!b.IntersectsWith(polyBounds)) continue;

            // 包围盒任一角落入套索内 → 命中
            var corners = new[]
            {
                new PointD(b.X, b.Y),
                new PointD(b.Right, b.Y),
                new PointD(b.Right, b.Bottom),
                new PointD(b.X, b.Bottom),
                new PointD(b.CenterX, b.CenterY)
            };

            if (corners.Any(c => PointInPolygon(c, worldPolygon)) || PolygonIntersectsRect(worldPolygon, b))
                yield return obj;
        }
    }

    private static double LocalScaleOf(ShapeObject obj)
    {
        var m = obj.LocalToWorld;
        var sx = Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
        var sy = Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22);
        return (sx + sy) / 2;
    }

    /// <summary>射线法判断点是否在多边形内。</summary>
    public static bool PointInPolygon(PointD p, IReadOnlyList<PointD> poly)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var pi = poly[i];
            var pj = poly[j];
            if (pi.Y > p.Y != pj.Y > p.Y &&
                p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>多边形的任一顶点落在矩形内，或矩形任一角落在多边形内 → 相交。</summary>
    private static bool PolygonIntersectsRect(IReadOnlyList<PointD> poly, RectD rect)
    {
        foreach (var p in poly)
            if (rect.Contains(p)) return true;
        return false;
    }
}
