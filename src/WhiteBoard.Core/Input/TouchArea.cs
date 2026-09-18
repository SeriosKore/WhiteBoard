using WhiteBoard.Core.Geometry;

namespace WhiteBoard.Core.Input;

/// <summary>
/// 触摸面积估算（用于橡皮擦直径自适应）。
///
/// 流程：触点集 → **单调链凸包** → **鞋带公式**求面积 → 映射为直径。
/// 映射沿用原需求给定的经验式：<c>diameter = clamp(√area × 1.6, 60, 420)</c>（屏幕像素），
/// **下限 60px**（原需求："最小直径不小于 40px，建议 60px 起"）。
/// </summary>
public static class TouchAreaEstimator
{
    public const double MinDiameter = 60.0;
    public const double MaxDiameter = 420.0;
    public const double AreaFactor = 1.6;

    /// <summary>由触点集估算橡皮擦直径（屏幕像素）。</summary>
    public static double EstimateDiameter(
        IReadOnlyList<PointD> points,
        double min = MinDiameter,
        double max = MaxDiameter,
        double factor = AreaFactor)
    {
        var area = HullArea(points);
        var d = Math.Sqrt(Math.Max(area, 0)) * factor;
        return Math.Clamp(d, min, max);
    }

    /// <summary>凸包面积（触点少于 3 个时为 0）。</summary>
    public static double HullArea(IReadOnlyList<PointD> points)
    {
        if (points.Count < 3) return 0;
        var hull = ConvexHull(points);
        return PolygonArea(hull);
    }

    /// <summary>单调链（Andrew monotone chain）凸包；返回逆时针顺序。</summary>
    public static IReadOnlyList<PointD> ConvexHull(IReadOnlyList<PointD> points)
    {
        if (points.Count <= 2) return points.ToList();

        var pts = points
            .OrderBy(p => p.X).ThenBy(p => p.Y)
            .ToList();

        var hull = new List<PointD>(pts.Count * 2);

        // 下凸壳
        foreach (var p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }

        // 上凸壳
        var lower = hull.Count + 1;
        for (var i = pts.Count - 2; i >= 0; i--)
        {
            var p = pts[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }

        hull.RemoveAt(hull.Count - 1);   // 末点与起点重复
        return hull;
    }

    /// <summary>鞋带公式求多边形面积（取绝对值）。</summary>
    public static double PolygonArea(IReadOnlyList<PointD> polygon)
    {
        if (polygon.Count < 3) return 0;
        double sum = 0;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            sum += (polygon[j].X + polygon[i].X) * (polygon[j].Y - polygon[i].Y);
        return Math.Abs(sum) / 2.0;
    }

    private static double Cross(PointD o, PointD a, PointD b)
        => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
}

/// <summary>
/// 掌拒与手掌擦除（决策 D3/ADR-11）：
/// <list type="bullet">
/// <item>画笔/形状/选择工具下：面积超阈值的触点视为**手掌 → 拒绝**；笔在感应范围内时也拒绝触摸；</item>
/// <item>**橡皮工具下：手掌 = 大号橡皮**（不拒绝，交给擦除逻辑按面积算直径）。</item>
/// </list>
/// </summary>
public sealed class PalmRejector
{
    /// <summary>触点宽度超过该值视为手掌（屏幕像素）。</summary>
    public double PalmContactWidth { get; init; } = 45.0;

    /// <summary>笔活跃时是否拒绝一切触摸。</summary>
    public bool RejectTouchWhileStylusActive { get; init; } = true;

    public bool IsPalm(PointerSample s)
        => s.Kind == PointerKind.Touch && s.ContactWidth >= PalmContactWidth;

    /// <summary>当前工具是否会把手掌当作橡皮（橡皮工具 → true）。</summary>
    public bool PalmActsAsEraser { get; set; }

    /// <summary>
    /// 是否应拒绝该触点。返回 false 时，触摸应当被正常处理
    /// （若此时 <see cref="PalmActsAsEraser"/> 且是手掌，则由擦除逻辑按大号橡皮处理）。
    /// </summary>
    public bool ShouldReject(PointerSample s, bool stylusActive)
    {
        if (s.Kind != PointerKind.Touch) return false;

        // 橡皮工具下：手掌转为擦除，不拒绝
        if (PalmActsAsEraser && IsPalm(s)) return false;

        if (RejectTouchWhileStylusActive && stylusActive) return true;
        return IsPalm(s);
    }
}
