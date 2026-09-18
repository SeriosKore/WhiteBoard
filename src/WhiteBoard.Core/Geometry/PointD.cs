namespace WhiteBoard.Core.Geometry;

/// <summary>二维点（Core 不依赖 WPF，自带基础几何类型）。</summary>
public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
    public double DistanceTo(PointD o)
    {
        var dx = X - o.X;
        var dy = Y - o.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

/// <summary>轴对齐矩形。</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public PointD TopLeft => new(X, Y);

    public static readonly RectD Empty = new(0, 0, 0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(PointD p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    public bool IntersectsWith(RectD o)
        => !(o.X > Right || o.Right < X || o.Y > Bottom || o.Bottom < Y);

    public RectD Inflate(double d)
        => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);

    public static RectD Union(RectD a, RectD b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new RectD(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    /// <summary>由四个角点求最小外接矩形。</summary>
    public static RectD FromPoints(IEnumerable<PointD> points)
    {
        var first = true;
        double minX = 0, minY = 0, maxX = 0, maxY = 0;
        foreach (var p in points)
        {
            if (first) { minX = maxX = p.X; minY = maxY = p.Y; first = false; continue; }
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        return first ? Empty : new RectD(minX, minY, maxX - minX, maxY - minY);
    }

    public override string ToString() => $"[{X:0.##},{Y:0.##} {Width:0.##}×{Height:0.##}]";
}

/// <summary>
/// 2D 仿射矩阵（行向量约定：<c>p' = p * M</c>），与 WPF <c>System.Windows.Media.Matrix</c> 同构，
/// 便于渲染层零成本转换。
/// </summary>
public readonly record struct MatrixD(
    double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
{
    public static readonly MatrixD Identity = new(1, 0, 0, 1, 0, 0);

    public static MatrixD Translation(double dx, double dy) => new(1, 0, 0, 1, dx, dy);
    public static MatrixD Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    public static MatrixD Rotation(double radians)
    {
        var c = Math.Cos(radians);
        var s = Math.Sin(radians);
        return new MatrixD(c, s, -s, c, 0, 0);
    }

    public double Determinant => M11 * M22 - M12 * M21;

    /// <summary><c>this * other</c>：先应用 this，再应用 other。</summary>
    public MatrixD Multiply(in MatrixD o) => new(
        M11 * o.M11 + M12 * o.M21,
        M11 * o.M12 + M12 * o.M22,
        M21 * o.M11 + M22 * o.M21,
        M21 * o.M12 + M22 * o.M22,
        OffsetX * o.M11 + OffsetY * o.M21 + o.OffsetX,
        OffsetX * o.M12 + OffsetY * o.M22 + o.OffsetY);

    public PointD Transform(PointD p) => new(
        p.X * M11 + p.Y * M21 + OffsetX,
        p.X * M12 + p.Y * M22 + OffsetY);

    /// <summary>逆矩阵；行列式为 0（退化）时抛 <see cref="InvalidOperationException"/>。</summary>
    public MatrixD Invert()
    {
        var det = Determinant;
        if (Math.Abs(det) < 1e-12)
            throw new InvalidOperationException("矩阵不可逆（退化变换）");

        return new MatrixD(
            M22 / det,
            -M12 / det,
            -M21 / det,
            M11 / det,
            (M21 * OffsetY - M22 * OffsetX) / det,
            (M12 * OffsetX - M11 * OffsetY) / det);
    }

    /// <summary>是否为仅含平移+旋转的刚体变换（无缩放/切变）。</summary>
    public bool IsRigid
    {
        get
        {
            var a = M11 * M11 + M12 * M12;
            var b = M21 * M21 + M22 * M22;
            var dot = M11 * M21 + M12 * M22;
            return Math.Abs(a - 1) < 1e-9 && Math.Abs(b - 1) < 1e-9 && Math.Abs(dot) < 1e-9;
        }
    }

    public static MatrixD operator *(MatrixD a, MatrixD b) => a.Multiply(b);
}
