using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Tests;
using WhiteBoard.Core.Model;
using WhiteBoard.Rendering;

namespace WhiteBoard.Rendering.Tests;

/// <summary>
/// 渲染层测试：几何构建、**擦除遮罩差集**、缓存失效、命中测试、视口裁剪。
/// 全部可离屏自动跑（不需要窗口）。
/// </summary>
public static class GeometryBuilderTests
{
    private static FreehandObject Stroke(int id, double y = 0, int n = 120, double pen = 6)
    {
        var pts = new List<InkPoint>();
        for (var i = 0; i < n; i++)
            pts.Add(new InkPoint(i * 3.0, y, 0.5f));
        return FreehandObject.FromWorldPoints(id, pts, "#F5F5F0", pen);
    }

    public static void Test_Freehand_BuildsNonEmptyGeometry()
    {
        var b = new SmoothGeometryBuilder();
        var o = Stroke(1);
        var geo = b.GetLocalGeometry(o);

        Check.True(geo.GetArea() > 0, "笔迹几何面积应大于 0");
        Check.True(geo.IsFrozen, "缓存几何应已冻结");
    }

    public static void Test_Freehand_EmptyPointsYieldsEmptyGeometry()
    {
        var b = new SmoothGeometryBuilder();
        var o = FreehandObject.FromWorldPoints(1, [new InkPoint(0, 0, 0.5f)], "#FFFFFF", 3);
        o.Points.Clear();
        var geo = b.GetLocalGeometry(o);
        Check.Near(0, geo.GetArea(), 1e-6, "无点时面积应为 0");
    }

    public static void Test_Erasure_SubtractsArea()
    {
        // ADR-18 的核心可视化断言：遮罩差集确实"挖掉"了面积
        var b = new SmoothGeometryBuilder();
        var o = Stroke(1);
        var areaBefore = b.GetLocalGeometry(o).GetArea();
        Check.True(areaBefore > 0, "初始应有面积");

        // 在笔迹中部打一个擦除圆（局部坐标）
        var mid = o.Points[o.Points.Count / 2];
        o.AddErasure(new EraserCircle(mid.X, mid.Y, 15));

        var areaAfter = b.GetLocalGeometry(o).GetArea();
        Check.True(areaAfter < areaBefore, $"擦除后面积应减少（前 {areaBefore:0} 后 {areaAfter:0}）");
        Check.True(areaAfter > 0, "只擦中间不应把整条擦没");
    }

    public static void Test_Erasure_MoreCirclesRemoveMoreArea()
    {
        var b = new SmoothGeometryBuilder();
        var o = Stroke(1);
        var a0 = b.GetLocalGeometry(o).GetArea();

        for (var k = 0; k < 5; k++)
        {
            var p = o.Points[20 + k * 15];
            o.AddErasure(new EraserCircle(p.X, p.Y, 10));
        }
        var a1 = b.GetLocalGeometry(o).GetArea();

        Check.True(a1 < a0, "加 5 个遮罩后面积应更小");
        Check.True(a1 > 0, "仍有剩余");
    }

    public static void Test_Erasure_CoveringEverything_IsFullyErased()
    {
        var b = new SmoothGeometryBuilder();
        var r = new PageRenderer(b);
        var o = Stroke(1, n: 40, pen: 4);

        // 沿整条笔迹密集打大圆 → 应判定为"完全擦除"
        for (var k = 0; k < 60; k++)
        {
            var t = k / 59.0;
            var x = t * (o.Points[^1].X);
            o.AddErasure(new EraserCircle(x, 0, 40));
        }

        Check.True(r.IsFullyErased(o), "全覆盖后应判定为完全擦除（需求：结果为空则删除）");
    }

    public static void Test_Erasure_DoesNotFragmentObject()
    {
        // 擦 10 次后仍是同一个对象（不是 10 个碎片）
        var page = new Page { Id = 1 };
        var b = new SmoothGeometryBuilder();
        var o = Stroke(1);
        page.Add(o);

        var cmd = new EraseCommand(page);
        for (var k = 0; k < 10; k++)
        {
            var p = o.Points[k * 10];
            cmd.Record(o, new EraserCircle(p.X, p.Y, 8));
        }
        cmd.Do();

        Check.Equal(1, page.Count, "页面仍只有 1 个对象");
        Check.Equal(10, o.Erasures.Count, "有 10 个遮罩");
        Check.True(b.GetLocalGeometry(o).GetArea() > 0, "仍有可见部分");
    }

    // ---- 缓存 ----

    public static void Test_Cache_HitOnRepeat()
    {
        var b = new SmoothGeometryBuilder();
        var o = Stroke(1);

        var g1 = b.GetLocalGeometry(o);
        var g2 = b.GetLocalGeometry(o);

        Check.True(ReferenceEquals(g1, g2), "同一对象重复取用应命中缓存（同一实例）");
        Check.Equal(1, b.Stats.Builds, "只构建一次");
        Check.Equal(1, b.Stats.Hits, "命中一次");
    }

    public static void Test_Cache_InvalidatedOnErasureChange()
    {
        var b = new SmoothGeometryBuilder();
        var o = Stroke(1);
        b.GetLocalGeometry(o);

        o.AddErasure(new EraserCircle(50, 0, 10));
        var g2 = b.GetLocalGeometry(o);

        Check.Equal(2, b.Stats.Builds, "遮罩变化后应重建几何");
        Check.Equal(1, b.Stats.Invalidations, "应记录一次失效");
        Check.True(g2.GetArea() > 0, "重建后的几何仍应有面积");
    }

    public static void Test_Cache_NotInvalidatedByMoveAndRotate()
    {
        // 关键：几何在局部坐标构建 → 平移/旋转不应触发重建（性能与"无损放大"的基础）
        var b = new SmoothGeometryBuilder();
        var o = Stroke(1);
        b.GetLocalGeometry(o);

        o.X += 500;
        o.Y -= 300;
        o.Rotation = 0.7;

        b.GetLocalGeometry(o);
        Check.Equal(1, b.Stats.Builds, "平移/旋转不应重建几何");
    }

    public static void Test_Cache_InvalidatedOnPenWidthChange()
    {
        var b = new SmoothGeometryBuilder();
        var o = Stroke(1);
        var a1 = b.GetLocalGeometry(o).GetArea();

        o.PenWidth *= 3;
        var a2 = b.GetLocalGeometry(o).GetArea();

        Check.Equal(2, b.Stats.Builds, "笔宽变化应重建");
        Check.True(a2 > a1, "笔宽变大 → 面积应变大");
    }

    public static void Test_Cache_InvalidatedWhenPointsAppended()
    {
        var b = new SmoothGeometryBuilder();
        var o = Stroke(1, n: 10);
        b.GetLocalGeometry(o);

        o.Points.Add(new InkPoint(100, 0, 0.5f));
        b.GetLocalGeometry(o);

        Check.Equal(2, b.Stats.Builds, "点数变化应重建（湿态逐帧重建走的就是这条路）");
    }

    public static void Test_Cache_ClearDropsEverything()
    {
        var b = new SmoothGeometryBuilder();
        var o = Stroke(1);
        b.GetLocalGeometry(o);
        b.Clear();
        b.GetLocalGeometry(o);
        Check.Equal(2, b.Stats.Builds, "清空后应重新构建");
    }

    // ---- 几何形状 ----

    public static void Test_LineGeometry_BuiltAndWidened()
    {
        var b = new SmoothGeometryBuilder();
        var line = LineObject.FromWorldPoints(1, new PointD(0, 0), new PointD(100, 0), "#FFFFFF", 8);
        var geo = b.GetLocalGeometry(line);

        Check.True(geo.GetArea() > 0, "直线应有面积（加宽成轮廓后填充）");
        // 100 长 × 8 宽 ≈ 800（圆头会略多）
        var area = geo.GetArea();
        Check.True(area > 600 && area < 1000, $"直线面积应接近 100×8，实际 {area:0}");
    }

    public static void Test_RectGeometry_BuiltAsClosedOutline()
    {
        var b = new SmoothGeometryBuilder();
        var rect = RectObject.FromWorldCorners(1, new PointD(0, 0), new PointD(100, 50), "#FFFFFF", 4);
        var geo = b.GetLocalGeometry(rect);

        var area = geo.GetArea();
        // 外框 104×54 减去内框 96×46 ≈ 5616 - 4416 = 1200
        Check.True(area > 900 && area < 1500, $"矩形应为空心描边轮廓（面积约 1200），实际 {area:0}");
    }

    public static void Test_EllipseGeometry_Built()
    {
        var b = new SmoothGeometryBuilder();
        var el = EllipseObject.FromWorldCorners(1, new PointD(0, 0), new PointD(100, 100), "#FFFFFF", 4);
        var geo = b.GetLocalGeometry(el);
        var area = geo.GetArea();

        // 圆环：(52² - 48²)π ≈ 1257
        Check.True(area > 900 && area < 1600, $"椭圆应为圆环轮廓（面积约 1257），实际 {area:0}");
    }

    public static void Test_ShapeObject_ErasureAlsoWorks()
    {
        var b = new SmoothGeometryBuilder();
        var rect = RectObject.FromWorldCorners(1, new PointD(0, 0), new PointD(100, 100), "#FFFFFF", 6);
        var before = b.GetLocalGeometry(rect).GetArea();

        rect.AddErasure(new EraserCircle(0, 0, 20));   // 擦掉左上角一段边
        var after = b.GetLocalGeometry(rect).GetArea();

        Check.True(after < before, $"几何形状也应按遮罩擦除（前 {before:0} 后 {after:0}）");
    }
}

/// <summary>命中测试与视口裁剪。</summary>
public static class HitTestTests
{
    private static FreehandObject Stroke(int id, double y = 0, int n = 100, double pen = 10)
    {
        var pts = new List<InkPoint>();
        for (var i = 0; i < n; i++) pts.Add(new InkPoint(i * 5.0, y, 0.5f));
        return FreehandObject.FromWorldPoints(id, pts, "#F5F5F0", pen);
    }

    public static void Test_HitTest_HitsOnStroke()
    {
        var b = new SmoothGeometryBuilder();
        var hit = new HitTester(b);
        var o = Stroke(1);

        Check.True(hit.HitTest(o, new PointD(250, 0)), "笔迹上的点应命中");
        Check.True(!hit.HitTest(o, new PointD(250, 500)), "远处不应命中");
    }

    public static void Test_HitTest_ErasedAreaIsNotHittable()
    {
        // ADR-18 的关键行为：被擦掉的区域点不中，但对象仍是"一笔"
        var b = new SmoothGeometryBuilder();
        var hit = new HitTester(b);
        var o = Stroke(1);

        var midLocal = o.Points[o.Points.Count / 2];
        var midWorld = o.LocalToWorld.Transform(midLocal.ToPoint());
        Check.True(hit.HitTest(o, midWorld), "擦除前中点应命中");

        o.AddErasure(new EraserCircle(midLocal.X, midLocal.Y, 20));
        Check.True(!hit.HitTest(o, midWorld), "擦除后中点不应命中");

        var farLocal = o.Points[5];
        var farWorld = o.LocalToWorld.Transform(farLocal.ToPoint());
        Check.True(hit.HitTest(o, farWorld), "未擦除处仍应命中（对象没被拆碎）");
    }

    public static void Test_HitTestRect_BoxSelection()
    {
        var b = new SmoothGeometryBuilder();
        var hit = new HitTester(b);
        var page = new Page { Id = 1 };
        page.Add(Stroke(1, 0));
        page.Add(Stroke(2, 1000));

        var selected = hit.HitTestRect(page, new RectD(-50, -50, 600, 300)).ToList();
        Check.Equal(1, selected.Count, "框选应只命中框内那一笔");
        Check.Equal(1, selected[0].Id, "命中的应是第一笔");
    }

    public static void Test_HitTestLasso_SelectsEnclosed()
    {
        var b = new SmoothGeometryBuilder();
        var hit = new HitTester(b);
        var page = new Page { Id = 1 };
        var inside = Stroke(1, 0);
        var outside = Stroke(2, 2000);
        page.Add(inside);
        page.Add(outside);

        // 用一个大三角形套索圈住第一笔
        var lasso = new List<PointD>
        {
            new(-100, -200), new(700, -200), new(300, 300)
        };

        var picked = hit.HitTestLasso(page, lasso).ToList();
        Check.Equal(1, picked.Count, "套索应只选中被圈住的那一笔");
        Check.Equal(inside.Id, picked[0].Id, "命中的应是 inside");
    }

    public static void Test_PointInPolygon_Basic()
    {
        var tri = new List<PointD> { new(0, 0), new(10, 0), new(5, 10) };
        Check.True(HitTester.PointInPolygon(new PointD(5, 5), tri), "内部点");
        Check.True(!HitTester.PointInPolygon(new PointD(20, 20), tri), "外部点");
    }
}

/// <summary>页面渲染与视口裁剪（离屏渲染到位图验证）。</summary>
public static class RenderTests
{
    public static void Test_Render_DrawsOnBitmap()
    {
        var b = new SmoothGeometryBuilder();
        var renderer = new PageRenderer(b);
        var page = new Page { Id = 1 };
        page.Add(FreehandObject.FromWorldPoints(1,
            [new InkPoint(100, 100, 0.5f), new InkPoint(400, 300, 0.5f)], "#F5F5F0", 12));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            renderer.Render(dc, page, new ViewportState(), 800, 600);

        var bmp = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);

        var colored = CountNonBackground(bmp);
        Check.True(colored > 50, $"应画出可见笔迹，实际非背景像素 {colored}");
    }

    public static void Test_Render_CullsOffscreenObjects()
    {
        var b = new SmoothGeometryBuilder();
        var renderer = new PageRenderer(b);
        var page = new Page { Id = 1 };
        page.Add(FreehandObject.FromWorldPoints(1,
            [new InkPoint(100, 100, 0.5f), new InkPoint(200, 100, 0.5f)], "#FF0000", 10));
        page.Add(FreehandObject.FromWorldPoints(2,
            [new InkPoint(50000, 50000, 0.5f), new InkPoint(50100, 50000, 0.5f)], "#FF0000", 10));

        var visual = new DrawingVisual();
        FrameStats stats;
        using (var dc = visual.RenderOpen())
            stats = renderer.Render(dc, page, new ViewportState(), 800, 600);

        Check.Equal(2, stats.TotalObjects, "总共 2 个对象");
        Check.Equal(1, stats.CulledObjects, "远处对象应被裁剪");
        Check.Equal(1, stats.DrawnObjects, "只实绘 1 个");
    }

    public static void Test_Render_Zoom_ScalesContent()
    {
        var b = new SmoothGeometryBuilder();
        var renderer = new PageRenderer(b);
        var page = new Page { Id = 1 };
        page.Add(FreehandObject.FromWorldPoints(1,
            [new InkPoint(100, 300, 0.5f), new InkPoint(200, 300, 0.5f)], "#F5F5F0", 6));

        // 以笔迹中心为锚点缩放（模拟"滚轮以鼠标为中心"），否则放大后内容会跑出视口
        var anchorWorld = new PointD(150, 300);
        int AtZoom(double z)
        {
            var vp = new ViewportState();
            vp.ZoomAt(anchorWorld, 1.0);      // 先让锚点落在屏幕上
            vp.ZoomAt(anchorWorld, z);        // 再缩放，锚点保持不动
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) renderer.Render(dc, page, vp, 800, 600);
            var bmp = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            return CountNonBackground(bmp);
        }

        var at1 = AtZoom(1);
        var at4 = AtZoom(4);
        Check.True(at4 > at1, $"放大后可见像素应更多（1x={at1} 4x={at4}）");
    }

    public static void Test_Render_FullyErasedObjectDrawsNothing()
    {
        var b = new SmoothGeometryBuilder();
        var renderer = new PageRenderer(b);
        var page = new Page { Id = 1 };

        var o = FreehandObject.FromWorldPoints(1,
            [new InkPoint(100, 300, 0.5f), new InkPoint(300, 300, 0.5f)], "#F5F5F0", 10);
        for (var k = 0; k < 40; k++)
            o.AddErasure(new EraserCircle(k * 6.0, 0, 50));
        page.Add(o);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) renderer.Render(dc, page, new ViewportState(), 800, 600);
        var bmp = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);

        var colored = CountNonBackground(bmp);
        Check.True(colored < 20, $"完全擦除的对象不应绘制出内容，实际非背景像素 {colored}");
    }

    private static int CountNonBackground(RenderTargetBitmap bmp)
    {
        var stride = bmp.PixelWidth * 4;
        var buf = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(buf, stride, 0);

        // 背景 #2F4F3A → B=0x3A,G=0x4F,R=0x2F
        var count = 0;
        for (var i = 0; i + 3 < buf.Length; i += 4)
        {
            if (Math.Abs(buf[i] - 0x3A) > 12 || Math.Abs(buf[i + 1] - 0x4F) > 12 || Math.Abs(buf[i + 2] - 0x2F) > 12)
                count++;
        }
        return count;
    }
}
