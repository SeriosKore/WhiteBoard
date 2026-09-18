using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Core.Tests;

/// <summary>基础几何：矩阵、矩形、视口坐标转换。</summary>
public static class GeometryTests
{
    public static void Test_Matrix_Identity_LeavesPointUnchanged()
    {
        var p = MatrixD.Identity.Transform(new PointD(3, 4));
        Check.Near(3, p.X, 1e-12, "X 应不变");
        Check.Near(4, p.Y, 1e-12, "Y 应不变");
    }

    public static void Test_Matrix_Translation()
    {
        var m = MatrixD.Translation(10, -5);
        var p = m.Transform(new PointD(1, 2));
        Check.Near(11, p.X, 1e-12, "平移 X");
        Check.Near(-3, p.Y, 1e-12, "平移 Y");
    }

    public static void Test_Matrix_Multiply_OrderIsThisThenOther()
    {
        // 先平移 (10,0)，再平移 (0,5) → 结果 (10,5)
        var m = MatrixD.Translation(10, 0).Multiply(MatrixD.Translation(0, 5));
        var p = m.Transform(new PointD(0, 0));
        Check.Near(10, p.X, 1e-12, "复合平移 X");
        Check.Near(5, p.Y, 1e-12, "复合平移 Y");
    }

    public static void Test_Matrix_Invert_RoundTrips()
    {
        var m = MatrixD.Translation(30, -12)
            .Multiply(MatrixD.Rotation(0.7))
            .Multiply(MatrixD.Scale(2.5, 0.5));

        var inv = m.Invert();
        var original = new PointD(7.5, -3.25);
        var back = inv.Transform(m.Transform(original));

        Check.Near(original.X, back.X, 1e-9, "逆变换 X");
        Check.Near(original.Y, back.Y, 1e-9, "逆变换 Y");
    }

    public static void Test_Matrix_Invert_SingularThrows()
    {
        var degenerate = MatrixD.Scale(1, 0); // 行列式为 0
        Check.Throws<InvalidOperationException>(() => degenerate.Invert(), "退化矩阵应抛异常");
    }

    public static void Test_Matrix_Rigid_Detection()
    {
        Check.True(MatrixD.Translation(5, 5).Multiply(MatrixD.Rotation(1.234)).IsRigid, "平移+旋转应为刚体变换");
        Check.True(!MatrixD.Scale(2, 1).IsRigid, "含缩放不应判为刚体变换");
    }

    public static void Test_Rect_IntersectsAndContains()
    {
        var a = new RectD(0, 0, 10, 10);
        Check.True(a.Contains(new PointD(5, 5)), "内部点应被包含");
        Check.True(!a.Contains(new PointD(11, 5)), "外部点不应被包含");
        Check.True(a.IntersectsWith(new RectD(9, 9, 5, 5)), "应相交");
        Check.True(!a.IntersectsWith(new RectD(11, 0, 5, 5)), "不应相交");
    }

    public static void Test_Rect_Union()
    {
        var r = RectD.Union(new RectD(0, 0, 10, 10), new RectD(20, 5, 10, 10));
        Check.Near(0, r.X, 1e-12, "并集 X");
        Check.Near(0, r.Y, 1e-12, "并集 Y");
        Check.Near(30, r.Width, 1e-12, "并集宽");
        Check.Near(15, r.Height, 1e-12, "并集高");
        Check.True(RectD.Union(RectD.Empty, new RectD(1, 1, 2, 2)).Width == 2, "与空矩形求并应返回另一个");
    }

    public static void Test_Viewport_ScreenWorld_RoundTrip()
    {
        var vp = new ViewportState { Zoom = 2.5, PanX = -40, PanY = 15 };
        var world = new PointD(123.5, -67.25);
        var back = vp.ScreenToWorld(vp.WorldToScreen(world));
        Check.Near(world.X, back.X, 1e-9, "往返 X");
        Check.Near(world.Y, back.Y, 1e-9, "往返 Y");
    }

    public static void Test_Viewport_ZoomAt_KeepsAnchorFixed()
    {
        var vp = new ViewportState { Zoom = 1.0, PanX = 0, PanY = 0 };
        var anchor = new PointD(300, 200);          // 屏幕点
        var worldBefore = vp.ScreenToWorld(anchor);

        vp.ZoomAt(anchor, 8.0);
        var worldAfter = vp.ScreenToWorld(anchor);

        Check.Near(worldBefore.X, worldAfter.X, 1e-9, "锚点世界坐标 X 应不动");
        Check.Near(worldBefore.Y, worldAfter.Y, 1e-9, "锚点世界坐标 Y 应不动");
    }

    public static void Test_Viewport_Zoom_ClampedToRange()
    {
        var vp = new ViewportState();
        vp.Zoom = 1000;
        Check.Equal(ViewportState.MaxZoom, vp.Zoom, "缩放上限应为 50");
        vp.Zoom = 0.0001;
        Check.Equal(ViewportState.MinZoom, vp.Zoom, "缩放下限应为 0.1");
    }

    public static void Test_Viewport_VisibleWorldRect()
    {
        var vp = new ViewportState { Zoom = 2.0, PanX = 10, PanY = 20 };
        var r = vp.VisibleWorldRect(800, 600);
        Check.Near(10, r.X, 1e-9, "可见区左边界");
        Check.Near(20, r.Y, 1e-9, "可见区上边界");
        Check.Near(400, r.Width, 1e-9, "可见区宽 = 800/2");
        Check.Near(300, r.Height, 1e-9, "可见区高 = 600/2");
    }

    public static void Test_Viewport_PanByScreen()
    {
        var vp = new ViewportState { Zoom = 2.0 };
        vp.PanByScreen(100, 50);   // 屏幕上拖 100px，世界位移应是 100/2 = 50
        Check.Near(-50, vp.PanX, 1e-9, "PanX");
        Check.Near(-25, vp.PanY, 1e-9, "PanY");
    }
}
