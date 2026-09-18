using WhiteBoard.Core.Geometry;

namespace WhiteBoard.Core.Model;

/// <summary>笔迹上的一个采样点（**局部坐标**）。</summary>
public readonly record struct InkPoint(double X, double Y, float Pressure)
{
    public PointD ToPoint() => new(X, Y);
}

/// <summary>
/// 一次擦除的圆形区域。
/// **存局部坐标**（ADR-18）：擦除是对象自身几何的一部分，
/// 这样对象平移/旋转/缩放时遮罩自然跟随，无需额外修正。
/// </summary>
public readonly record struct EraserCircle(double X, double Y, double Radius)
{
    public PointD Center => new(X, Y);

    public double DistanceTo(PointD p)
    {
        var dx = p.X - X;
        var dy = p.Y - Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public bool Contains(PointD p, double tolerance = 0) => DistanceTo(p) <= Radius + tolerance;

    /// <summary>把世界坐标的擦除圆换算到本对象的局部坐标。</summary>
    public static EraserCircle FromWorld(PointD worldCenter, double worldRadius, in MatrixD localToWorld)
    {
        var local = localToWorld.Invert().Transform(worldCenter);
        // 半径按局部→世界变换的平均缩放折算
        var sx = Math.Sqrt(localToWorld.M11 * localToWorld.M11 + localToWorld.M12 * localToWorld.M12);
        var sy = Math.Sqrt(localToWorld.M21 * localToWorld.M21 + localToWorld.M22 * localToWorld.M22);
        var scale = Math.Max(1e-6, (sx + sy) / 2);
        return new EraserCircle(local.X, local.Y, worldRadius / scale);
    }
}

/// <summary>
/// 图元基类。约定：
/// <list type="bullet">
/// <item>局部几何的坐标范围是 <c>[0..LocalWidth] × [0..LocalHeight]</c>；</item>
/// <item>世界坐标 = 局部坐标经 <see cref="LocalToWorld"/> 变换；</item>
/// <item><see cref="Erasures"/> 存**局部坐标**，随对象一起变换。</item>
/// </list>
/// </summary>
public abstract class ShapeObject
{
    /// <summary>遮罩数量超过该阈值时建议烘焙为独立对象（ADR-18 的"延迟烘焙"）。</summary>
    public const int BakeThreshold = 32;

    public required int Id { get; init; }

    /// <summary>局部原点在世界坐标系中的位置（对象左上）。</summary>
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>旋转弧度（S1 恒为 0，S2 开放旋转手柄）。</summary>
    public double Rotation { get; set; }

    public int ZIndex { get; set; }

    /// <summary>局部尺寸（创建时确定；S2 的缩放通过 ScaleX/ScaleY 实现）。</summary>
    public double LocalWidth { get; set; }
    public double LocalHeight { get; set; }

    /// <summary>缩放（S2 开放控制点缩放；S1 恒为 1）。</summary>
    public double ScaleX { get; set; } = 1.0;
    public double ScaleY { get; set; } = 1.0;

    /// <summary>描边颜色（#RRGGBB 或 #AARRGGBB）。</summary>
    public string Color { get; set; } = "#F5F5F0";

    /// <summary>是否参与橡皮擦擦除。文本/公式/化学式为 false。</summary>
    public virtual bool IsErasable => true;

    /// <summary>擦除遮罩（局部坐标）。见 ADR-18。</summary>
    public List<EraserCircle> Erasures { get; } = [];

    /// <summary>判别符（序列化用）。</summary>
    public abstract string Kind { get; }

    /// <summary>
    /// 几何指纹：只包含**局部几何相关**的状态（不含 X/Y/Rotation —— 那些靠变换矩阵处理）。
    /// 渲染层用它判断缓存的几何是否失效。
    /// </summary>
    public virtual long GeometryFingerprint
        => HashCode.Combine(Kind, LocalWidth, LocalHeight, Erasures.Count);

    public PointD Center => new(X + LocalWidth * ScaleX / 2, Y + LocalHeight * ScaleY / 2);

    /// <summary>局部 → 世界。</summary>
    public MatrixD LocalToWorld
    {
        get
        {
            var cx = LocalWidth / 2;
            var cy = LocalHeight / 2;
            var m = MatrixD.Translation(X, Y);
            m = m.Multiply(MatrixD.Translation(cx * ScaleX, cy * ScaleY));
            if (Math.Abs(Rotation) > 1e-12) m = m.Multiply(MatrixD.Rotation(Rotation));
            m = m.Multiply(MatrixD.Scale(ScaleX, ScaleY));
            m = m.Multiply(MatrixD.Translation(-cx, -cy));
            return m;
        }
    }

    /// <summary>局部几何的外接矩形（未含遮罩影响）。</summary>
    public virtual RectD LocalBounds => new(0, 0, LocalWidth, LocalHeight);

    /// <summary>世界坐标下的轴对齐外接矩形（局部四角变换后求 AABB）。</summary>
    public RectD WorldBounds
    {
        get
        {
            var m = LocalToWorld;
            var b = LocalBounds;
            var corners = new[]
            {
                m.Transform(new PointD(b.X, b.Y)),
                m.Transform(new PointD(b.Right, b.Y)),
                m.Transform(new PointD(b.Right, b.Bottom)),
                m.Transform(new PointD(b.X, b.Bottom))
            };
            return RectD.FromPoints(corners);
        }
    }

    /// <summary>追加一次擦除（局部坐标圆）；重复圆会被忽略。</summary>
    public bool AddErasure(EraserCircle circle)
    {
        if (circle.Radius <= 0) return false;
        foreach (var e in Erasures)
        {
            if (Math.Abs(e.X - circle.X) < 1e-9 &&
                Math.Abs(e.Y - circle.Y) < 1e-9 &&
                Math.Abs(e.Radius - circle.Radius) < 1e-9)
                return false;
        }
        Erasures.Add(circle);
        return true;
    }

    /// <summary>遮罩数量是否已达烘焙阈值。</summary>
    public bool ShouldBake => Erasures.Count >= BakeThreshold;

    public abstract ShapeObject Clone(int newId);

    /// <summary>把基类字段复制到克隆体。</summary>
    protected void CopyBaseTo(ShapeObject target, int newId)
    {
        _ = newId;
        target.X = X;
        target.Y = Y;
        target.Rotation = Rotation;
        target.ZIndex = ZIndex;
        target.LocalWidth = LocalWidth;
        target.LocalHeight = LocalHeight;
        target.ScaleX = ScaleX;
        target.ScaleY = ScaleY;
        target.Color = Color;
        target.Erasures.AddRange(Erasures);
    }
}

/// <summary>自由画笔（手写笔迹）。平滑风，几何由渲染层用 WPF Stroke 构建。</summary>
public sealed class FreehandObject : ShapeObject
{
    public override string Kind => "freehand";

    /// <summary>局部坐标下的采样点（含压感）。</summary>
    public List<InkPoint> Points { get; } = [];

    /// <summary>笔宽（**局部/世界单位**，随缩放变化 —— 无损放大的前提）。</summary>
    public double PenWidth { get; set; } = 3.0;

    /// <summary>几何指纹：点数、笔宽、遮罩数、首/中/末点采样（足以捕捉实际会发生的编辑）。</summary>
    public override long GeometryFingerprint
    {
        get
        {
            var h = new HashCode();
            h.Add(Kind);
            h.Add(Points.Count);
            h.Add(PenWidth);
            h.Add(Erasures.Count);
            if (Points.Count > 0)
            {
                var mid = Points[Points.Count / 2];
                h.Add(Points[0]);
                h.Add(mid);
                h.Add(Points[^1]);
            }
            return h.ToHashCode();
        }
    }

    /// <summary>
    /// 局部外接矩形。**必须包含笔宽**：笔迹的实际可见范围是中心线 ± 笔宽/2，
    /// 若只取采样点的包围盒，一条水平笔迹的高度就是 0，
    /// 于是"视口裁剪"会在笔迹仍有一半可见时把它整条裁掉（已由回归测试守护），
    /// 导出的内容包围盒也会退化成一条 1 像素高的线。
    /// </summary>
    public override RectD LocalBounds
    {
        get
        {
            if (Points.Count == 0) return new RectD(0, 0, LocalWidth, LocalHeight);
            var pts = Points.Select(p => p.ToPoint());
            var r = RectD.FromPoints(pts);
            var pad = Math.Max(PenWidth, 1e-6) / 2;
            return new RectD(r.X - pad, r.Y - pad, r.Width + pad * 2, r.Height + pad * 2);
        }
    }

    public override ShapeObject Clone(int newId)
    {
        var c = new FreehandObject { Id = newId, PenWidth = PenWidth };
        CopyBaseTo(c, newId);
        c.Points.AddRange(Points);
        return c;
    }

    /// <summary>由一串世界坐标点创建（自动平移到局部坐标并记录尺寸）。</summary>
    public static FreehandObject FromWorldPoints(
        int id, IReadOnlyList<InkPoint> worldPoints, string color, double penWidth)
    {
        if (worldPoints.Count == 0) throw new ArgumentException("笔迹至少需要一个点", nameof(worldPoints));

        var bounds = RectD.FromPoints(worldPoints.Select(p => p.ToPoint()));
        var obj = new FreehandObject
        {
            Id = id,
            X = bounds.X,
            Y = bounds.Y,
            LocalWidth = Math.Max(bounds.Width, 1e-6),
            LocalHeight = Math.Max(bounds.Height, 1e-6),
            Color = color,
            PenWidth = penWidth
        };
        foreach (var p in worldPoints)
            obj.Points.Add(new InkPoint(p.X - bounds.X, p.Y - bounds.Y, p.Pressure));
        return obj;
    }
}
