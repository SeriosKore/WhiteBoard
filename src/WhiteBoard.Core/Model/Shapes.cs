using WhiteBoard.Core.Geometry;

namespace WhiteBoard.Core.Model;

/// <summary>
/// 带描边的折线/闭合图形基类（直线、矩形、椭圆都用它表达"中心线 + 笔宽"）。
/// 渲染层负责把中心线**加宽成轮廓**（`GetWidenedPathGeometry`）再填充，
/// 这样"擦除遮罩差集"与自由画笔走同一条路径（ADR-18）。
/// </summary>
public abstract class StrokedShapeObject : ShapeObject
{
    /// <summary>笔宽（局部单位）。</summary>
    public double PenWidth { get; set; } = 3.0;

    /// <summary>形状种类（渲染层据此构建中心线几何 —— Core 不依赖 WPF）。</summary>
    public abstract StrokedShapeKind ShapeKind { get; }

    /// <summary>中心线是否闭合（矩形/椭圆为 true）。</summary>
    public virtual bool IsClosed => false;

    /// <summary>
    /// 局部坐标下的**中心线**关键点。
    /// 约定：对象的局部包围盒是**外框**（含笔宽），因此矩形/椭圆的中心线要**内缩 penWidth/2**，
    /// 这样"中心线加宽 penWidth"之后外沿恰好落在包围盒上。
    /// </summary>
    public abstract IReadOnlyList<PointD> LocalOutline { get; }

    public override long GeometryFingerprint
        => HashCode.Combine(base.GeometryFingerprint, PenWidth, IsClosed, LocalOutline.Count);
}

/// <summary>直线（局部坐标两点）。</summary>
public sealed class LineObject : StrokedShapeObject
{
    public override string Kind => "line";

    public double LocalX1 { get; set; }
    public double LocalY1 { get; set; }
    public double LocalX2 { get; set; }
    public double LocalY2 { get; set; }

    public override StrokedShapeKind ShapeKind => StrokedShapeKind.Line;

    public override IReadOnlyList<PointD> LocalOutline =>
        [new PointD(LocalX1, LocalY1), new PointD(LocalX2, LocalY2)];

    public override ShapeObject Clone(int newId)
    {
        var c = new LineObject { Id = newId, PenWidth = PenWidth, LocalX1 = LocalX1, LocalY1 = LocalY1, LocalX2 = LocalX2, LocalY2 = LocalY2 };
        CopyBaseTo(c, newId);
        return c;
    }

    public static LineObject FromWorldPoints(int id, PointD a, PointD b, string color, double penWidth)
    {
        var minX = Math.Min(a.X, b.X);
        var minY = Math.Min(a.Y, b.Y);
        var maxX = Math.Max(a.X, b.X);
        var maxY = Math.Max(a.Y, b.Y);
        var pad = penWidth / 2;

        return new LineObject
        {
            Id = id,
            X = minX - pad,
            Y = minY - pad,
            LocalWidth = Math.Max(maxX - minX, penWidth) + penWidth,
            LocalHeight = Math.Max(maxY - minY, penWidth) + penWidth,
            Color = color,
            PenWidth = penWidth,
            LocalX1 = a.X - (minX - pad),
            LocalY1 = a.Y - (minY - pad),
            LocalX2 = b.X - (minX - pad),
            LocalY2 = b.Y - (minY - pad)
        };
    }
}

/// <summary>矩形（局部坐标 [0..w]×[0..h]）。</summary>
public sealed class RectObject : StrokedShapeObject
{
    public override string Kind => "rect";
    public override bool IsClosed => true;

    public override StrokedShapeKind ShapeKind => StrokedShapeKind.Rectangle;

    public override IReadOnlyList<PointD> LocalOutline
    {
        get
        {
            var p = PenWidth / 2;
            var w = Math.Max(LocalWidth - PenWidth, 0.001);
            var h = Math.Max(LocalHeight - PenWidth, 0.001);
            return [new PointD(p, p), new PointD(p + w, p), new PointD(p + w, p + h), new PointD(p, p + h)];
        }
    }

    public override ShapeObject Clone(int newId)
    {
        var c = new RectObject { Id = newId, PenWidth = PenWidth };
        CopyBaseTo(c, newId);
        return c;
    }

    public static RectObject FromWorldCorners(int id, PointD a, PointD b, string color, double penWidth)
    {
        var pad = penWidth / 2;
        var x = Math.Min(a.X, b.X) - pad;
        var y = Math.Min(a.Y, b.Y) - pad;
        return new RectObject
        {
            Id = id,
            X = x,
            Y = y,
            LocalWidth = Math.Abs(b.X - a.X) + penWidth,
            LocalHeight = Math.Abs(b.Y - a.Y) + penWidth,
            Color = color,
            PenWidth = penWidth
        };
    }
}

/// <summary>椭圆（局部坐标内切于 [0..w]×[0..h]）。</summary>
public sealed class EllipseObject : StrokedShapeObject
{
    public override string Kind => "ellipse";
    public override bool IsClosed => true;

    public override StrokedShapeKind ShapeKind => StrokedShapeKind.Ellipse;

    public override IReadOnlyList<PointD> LocalOutline
    {
        get
        {
            var p = PenWidth / 2;
            var w = Math.Max(LocalWidth - PenWidth, 0.001);
            var h = Math.Max(LocalHeight - PenWidth, 0.001);
            return [new PointD(p, p), new PointD(p + w, p), new PointD(p + w, p + h), new PointD(p, p + h)];
        }
    }

    public override ShapeObject Clone(int newId)
    {
        var c = new EllipseObject { Id = newId, PenWidth = PenWidth };
        CopyBaseTo(c, newId);
        return c;
    }

    public static EllipseObject FromWorldCorners(int id, PointD a, PointD b, string color, double penWidth)
    {
        var pad = penWidth / 2;
        return new EllipseObject
        {
            Id = id,
            X = Math.Min(a.X, b.X) - pad,
            Y = Math.Min(a.Y, b.Y) - pad,
            LocalWidth = Math.Abs(b.X - a.X) + penWidth,
            LocalHeight = Math.Abs(b.Y - a.Y) + penWidth,
            Color = color,
            PenWidth = penWidth
        };
    }
}
/// <summary>描边图形的种类（渲染层据此构建中心线几何）。</summary>
public enum StrokedShapeKind
{
    Line,
    Rectangle,
    Ellipse
}