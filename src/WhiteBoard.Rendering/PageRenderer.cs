using System.Windows;
using System.Windows.Media;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Rendering;

/// <summary>一帧渲染的统计（自检用）。</summary>
public readonly record struct FrameStats(int TotalObjects, int CulledObjects, int DrawnObjects)
{
    public override string ToString()
        => $"总对象 {TotalObjects}　裁剪 {CulledObjects}　实绘 {DrawnObjects}";
}

/// <summary>
/// 页面渲染器：把 <see cref="Page"/> 画到 <see cref="DrawingContext"/>。
///
/// 设计要点（与 ADR / M0 实测结论对齐）：
/// <list type="bullet">
/// <item>几何在**局部坐标**构建并缓存，变换只改矩阵 → 平移缩放不重建几何；</item>
/// <item>每帧**矢量重绘**，不做固定分辨率位图缓存 → 支持无损放大（50x 实测边缘过渡 2.71 px）；</item>
/// <item>视口裁剪：只绘制与可见区相交的对象；</item>
/// <item>渲染顺序按 ZIndex 升序。</item>
/// </list>
/// </summary>
public sealed class PageRenderer
{
    private readonly SmoothGeometryBuilder _geometry;

    public PageRenderer(SmoothGeometryBuilder geometry) => _geometry = geometry;

    /// <summary>背景色（黑板绿）。</summary>
    public Color BackgroundColor { get; set; } = Color.FromRgb(0x2F, 0x4F, 0x3A);

    /// <summary>渲染一页。</summary>
    public FrameStats Render(DrawingContext dc, Page page, ViewportState viewport,
        double screenWidth, double screenHeight)
    {
        // 背景
        dc.DrawRectangle(new SolidColorBrush(BackgroundColor), null,
            new Rect(0, 0, screenWidth, screenHeight));

        var visible = viewport.VisibleWorldRect(screenWidth, screenHeight);

        // 视口 → 世界 的变换（screen = (world - pan) * zoom）
        var worldToScreen = new Matrix(viewport.Zoom, 0, 0, viewport.Zoom,
            -viewport.PanX * viewport.Zoom, -viewport.PanY * viewport.Zoom);
        dc.PushTransform(new MatrixTransform(worldToScreen));

        var total = 0;
        var culled = 0;
        var drawn = 0;

        foreach (var obj in page.InRenderOrder())
        {
            total++;
            if (!obj.WorldBounds.IntersectsWith(visible))
            {
                culled++;
                continue;
            }

            // 世界坐标几何 = 局部几何 × 对象矩阵（几何本身复用缓存）
            var geo = _geometry.GetWorldGeometry(obj);
            if (geo.Bounds.IsEmpty && geo.ToString().Length == 0)
            {
                // 完全被擦除 → 跳过（不绘制）
                continue;
            }

            var brush = GeometryInterop.BrushFromHex(obj.Color);
            dc.DrawGeometry(brush, null, geo);
            drawn++;
        }

        dc.Pop();

        return new FrameStats(total, culled, drawn);
    }

    /// <summary>
    /// 判断对象是否"被完全擦除"（差集后面积≈0）。
    /// 用于擦除时决定是否删除对象（需求：结果为空则删除）。
    /// </summary>
    public bool IsFullyErased(ShapeObject obj, double areaEpsilon = 0.5)
    {
        try
        {
            var geo = _geometry.GetLocalGeometry(obj);
            return geo.GetArea() <= areaEpsilon;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>取对象当前渲染面积（测试与诊断用）。</summary>
    public double GetArea(ShapeObject obj)
    {
        try { return _geometry.GetLocalGeometry(obj).GetArea(); }
        catch (Exception) { return 0; }
    }
}
