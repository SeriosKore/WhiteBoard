using System.IO;
using System.Windows.Media;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;
using WhiteBoard.Core.Storage;
using WhiteBoard.Core.Tests;
using WhiteBoard.Rendering.Export;

namespace WhiteBoard.Rendering.Tests;

/// <summary>
/// PoC-B 退化用例：**极端输入下不许崩、不许画出怪东西**。
///
/// 这些都是真实会发生的输入，不是想出来的：
/// 点一下（单点）、拖了但没动（零长度）、手抖重复点、橡皮开很大一次擦掉整条、
/// 缩到 10% 或放到 50 倍、对象被拖到很远的地方、以及坐标出现非有限值的脏数据。
/// 退化情形是渲染与布尔差集最容易抛异常或产出"看不见但占资源"对象的地方。
/// </summary>
public static class DegenerateCaseTests
{
    private static readonly string Pen = "#F5F5F0";

    private static FreehandObject Stroke(int id, params InkPoint[] pts)
        => FreehandObject.FromWorldPoints(id, pts, Pen, 8);

    // ── ① 点一下 / 零长度 / 重复点 ────────────────────────────────────────

    /// <summary>点一下就是"一个点"的笔迹：必须画得出一个小圆点，而不是什么都没有。</summary>
    public static void Test_Degenerate_SinglePointStrokeIsVisible()
    {
        var geo = new SmoothGeometryBuilder();
        var o = Stroke(1, new InkPoint(100, 100, 0.5f));

        var g = geo.GetLocalGeometry(o);
        Check.True(g.GetArea() > 0, $"单点应画出可见的点，实际面积 {g.GetArea()}");
        Check.True(g.Bounds.Width > 0 && g.Bounds.Height > 0, "单点的外接矩形应有尺寸");
    }

    /// <summary>两个完全相同的点（手抖点了两下同一处）不应抛异常、也不应产生 NaN。</summary>
    public static void Test_Degenerate_DuplicatePointsAreHarmless()
    {
        var geo = new SmoothGeometryBuilder();
        var o = Stroke(1,
            new InkPoint(50, 50, 0.5f), new InkPoint(50, 50, 0.5f),
            new InkPoint(50, 50, 0.5f), new InkPoint(50, 50, 0.5f));

        var g = geo.GetLocalGeometry(o);
        Check.True(double.IsFinite(g.GetArea()), "面积应是有限值");
        Check.True(g.GetArea() >= 0, "面积不应为负");
    }

    /// <summary>零长度直线（起点终点相同）：宽度退化成笔宽，但不许崩。</summary>
    public static void Test_Degenerate_ZeroLengthLine()
    {
        var geo = new SmoothGeometryBuilder();
        var o = LineObject.FromWorldPoints(1, new PointD(200, 200), new PointD(200, 200), Pen, 10);

        var g = geo.GetLocalGeometry(o);
        Check.True(double.IsFinite(g.GetArea()), "面积应是有限值");
        Check.True(o.LocalWidth > 0 && o.LocalHeight > 0, "零长度直线的外框仍应有笔宽那么大");
    }

    /// <summary>零尺寸矩形（点一下没拖）：退化成一个小方块，不许抛异常。</summary>
    public static void Test_Degenerate_ZeroSizeRectAndEllipse()
    {
        var geo = new SmoothGeometryBuilder();

        var rect = RectObject.FromWorldCorners(1, new PointD(10, 10), new PointD(10, 10), Pen, 6);
        var ellipse = EllipseObject.FromWorldCorners(2, new PointD(10, 10), new PointD(10, 10), Pen, 6);

        var rg = geo.GetLocalGeometry(rect);
        var eg = geo.GetLocalGeometry(ellipse);

        Check.True(double.IsFinite(rg.GetArea()), "零尺寸矩形的面积应是有限值");
        Check.True(double.IsFinite(eg.GetArea()), "零尺寸椭圆的面积应是有限值");
    }

    // ── ② 擦除的退化 ──────────────────────────────────────────────────────

    /// <summary>单点笔迹被一个擦除圆盖住 → 应判定为"完全擦除"，渲染时跳过。</summary>
    public static void Test_Degenerate_SinglePointFullyErased()
    {
        var geo = new SmoothGeometryBuilder();
        var o = Stroke(1, new InkPoint(100, 100, 0.5f));
        o.AddErasure(new EraserCircle(0, 0, 100));   // 局部坐标：把整条盖住

        var renderer = new PageRenderer(geo);
        Check.True(renderer.IsFullyErased(o), "被完全盖住的单点应判定为已擦净");
        Check.True(geo.GetLocalGeometry(o).GetArea() <= 0.5, "擦净后面积应≈0");
    }

    /// <summary>半径极大的擦除圆（手掌大橡皮一次划过）不应让几何构建崩掉。</summary>
    public static void Test_Degenerate_HugeErasureRadius()
    {
        var geo = new SmoothGeometryBuilder();
        var o = Stroke(1, new InkPoint(0, 0, 0.5f), new InkPoint(500, 300, 0.5f));
        o.AddErasure(new EraserCircle(250, 150, 100000));

        var g = geo.GetLocalGeometry(o);
        Check.True(double.IsFinite(g.GetArea()), "超大擦除半径下面积应是有限值");
        Check.True(g.GetArea() <= 1, "超大半径应把整条擦净");
    }

    /// <summary>擦除圆半径 0 / 负数不应被记录（避免"看不见的擦除"让后续面积计算出错）。</summary>
    public static void Test_Degenerate_ZeroRadiusErasureIgnored()
    {
        var o = Stroke(1, new InkPoint(0, 0, 0.5f), new InkPoint(100, 0, 0.5f));
        Check.True(!o.AddErasure(new EraserCircle(50, 0, 0)), "半径为 0 的擦除应被忽略");
        Check.True(!o.AddErasure(new EraserCircle(50, 0, -5)), "负半径应被忽略");
        Check.Equal(0, o.Erasures.Count, "不应记录任何无效擦除");
    }

    // ── ③ 极端缩放与视口 ──────────────────────────────────────────────────

    /// <summary>缩到最小与放到最大时，几何都能构建、命中测试都能工作。</summary>
    public static void Test_Degenerate_ExtremeZoomLevels()
    {
        var geo = new SmoothGeometryBuilder();
        var page = new Page { Id = 1 };
        var o = Stroke(1, new InkPoint(0, 0, 0.5f), new InkPoint(400, 200, 0.5f));
        page.Add(o);

        var renderer = new PageRenderer(geo);
        var hits = new HitTester(geo);

        foreach (var zoom in new[] { ViewportState.MinZoom, 1.0, ViewportState.MaxZoom })
        {
            var vp = new ViewportState { Zoom = zoom };

            var visual = new DrawingVisual();
            FrameStats stats;
            using (var dc = visual.RenderOpen())
                stats = renderer.Render(dc, page, vp, 1280, 800);

            Check.True(stats.TotalObjects == 1, $"{zoom}× 时应统计到 1 个对象");

            // 命中测试与缩放无关（在世界坐标上做）
            Check.True(hits.HitTest(o, new PointD(200, 100)), $"{zoom}× 时应能点中笔迹");
        }
    }

    /// <summary>对象被拖到极远处（用户手滑）时：不该崩，且应被视口正确裁剪掉。</summary>
    public static void Test_Degenerate_ObjectFarAwayIsCulledNotCrashed()
    {
        var geo = new SmoothGeometryBuilder();
        var page = new Page { Id = 1 };
        var far = Stroke(1, new InkPoint(1e6, 1e6, 0.5f), new InkPoint(1e6 + 100, 1e6, 0.5f));
        page.Add(far);

        var renderer = new PageRenderer(geo);
        var visual = new DrawingVisual();
        FrameStats stats;
        using (var dc = visual.RenderOpen())
            stats = renderer.Render(dc, page, new ViewportState(), 1280, 800);

        Check.Equal(1, stats.CulledObjects, "极远处的对象应被裁剪");
        Check.Equal(0, stats.DrawnObjects, "不应实际绘制");
    }

    // ── ④ 脏数据与导出 ────────────────────────────────────────────────────

    /// <summary>
    /// 坐标出现 NaN / Infinity（理论上不该有，但别处 bug 可能产生）时，
    /// 保存必须把它变成有限值，而不是写出一个打不开的文件。
    /// </summary>
    public static void Test_Degenerate_NonFiniteCoordinatesAreSanitizedOnSave()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-deg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "脏坐标.wb");

        var doc = new WhiteboardDocument { Id = 1 };
        var page = doc.EnsureAtLeastOnePage();

        var o = Stroke(1, new InkPoint(0, 0, 0.5f), new InkPoint(100, 0, 0.5f));
        page.Add(o);
        o.X = double.NaN;              // 模拟别处的 bug 留下的脏数据
        o.LocalWidth = double.PositiveInfinity;

        WbPackage.Save(doc, file);
        var back = WbPackage.Load(file).Document;

        Check.Equal(1, back.Pages[0].Count, "文件应能被正常打开（不应写出坏 JSON）");
        var bo = back.Pages[0].Objects[0];
        Check.True(double.IsFinite(bo.X), $"NaN 应被净化为有限值，实际 {bo.X}");
        Check.True(double.IsFinite(bo.LocalWidth), $"Infinity 应被净化为有限值，实际 {bo.LocalWidth}");

        Directory.Delete(dir, true);
    }

    /// <summary>空页导出不应产出 0 字节或畸形 PNG（已有 MinSide 兜底，这里钉住行为）。</summary>
    public static void Test_Degenerate_EmptyPageExportIsValidPng()
    {
        var geo = new SmoothGeometryBuilder();
        var page = new Page { Id = 1 };

        var r = PngExporter.RenderPage(page, geo, new PngExportOptions { Scale = 1 });

        Check.True(r.Bytes.Length > 100, "空页导出也应有内容（背景）");
        Check.Equal(0x89, r.Bytes[0], "应为 PNG 魔数");
        Check.True(r.PixelWidth >= 64 && r.PixelHeight >= 64, $"应补足到最小画布，实际 {r.PixelWidth}×{r.PixelHeight}");
    }

    /// <summary>整页只有文字时，缩略图也应正常（文本框不会退化成 0×0）。</summary>
    public static void Test_Degenerate_TextOnlyPageThumbnail()
    {
        var geo = new SmoothGeometryBuilder();
        var page = new Page { Id = 1 };
        var (w, h) = TextGeometry.Measure("只有文字", 48);
        page.Add(TextObject.FromWorldTopLeft(1, "只有文字", new PointD(0, 0), w, h, 48, Pen));

        var thumb = new ThumbnailRenderer(geo) { MaxSide = 160 };
        var bmp = thumb.Render(page);

        Check.True(bmp.PixelWidth >= 8 && bmp.PixelHeight >= 8, $"缩略图应有有效尺寸，实际 {bmp.PixelWidth}×{bmp.PixelHeight}");
        Check.True(bmp.IsFrozen, "缩略图应已冻结");
    }

    /// <summary>反复对同一个对象追加遮罩（极端手势）时缓存应稳定失效，不会返回旧几何。</summary>
    public static void Test_Degenerate_ManyErasuresKeepCacheConsistent()
    {
        var geo = new SmoothGeometryBuilder();
        var o = Stroke(1, new InkPoint(0, 0, 0.5f), new InkPoint(400, 0, 0.5f));

        var first = geo.GetLocalGeometry(o).GetArea();

        // 沿笔迹打满遮罩（模拟"反复来回擦"）
        for (var x = 0.0; x <= 400; x += 4)
            o.AddErasure(new EraserCircle(x, 0, 3));

        var after = geo.GetLocalGeometry(o).GetArea();

        Check.True(after < first, $"大量遮罩后面积应减少（{first:0} → {after:0}）");
        Check.True(double.IsFinite(after), "面积应是有限值");
        Check.True(o.ShouldBake, "遮罩数超过阈值时应提示可烘焙（ADR-18 的延迟烘焙）");
    }
}
