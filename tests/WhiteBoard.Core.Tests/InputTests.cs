using System.Diagnostics;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Input;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Core.Tests;

/// <summary>输入层：优先级、合成鼠标抑制、双指与书写冲突、拖动阈值、触摸面积、掌拒。</summary>
public static class InputTests
{
    private const PointerKind Stylus = PointerKind.Stylus;
    private const PointerKind Touch = PointerKind.Touch;
    private const PointerKind Mouse = PointerKind.Mouse;

    private static PointerSample S(int id, PointerKind kind, double x, double y,
        double contactWidth = 0, long ticks = 0, float pressure = 0.5f)
        => new(id, kind, x, y, pressure, contactWidth, ticks);

    private static long Ticks(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    // ---- 优先级与合成鼠标 ----

    public static void Test_Router_MouseAloneBeginsStroke()
    {
        var r = new PointerRouter();
        var res = r.Process(PointerAction.Down, S(1, Mouse, 10, 10));
        Check.Equal(RouteDecision.BeginStroke, res.Decision, "鼠标按下应开始笔画");
        Check.Equal(PointerKind.Mouse, res.Owner, "owner 应为鼠标");
    }

    public static void Test_Router_StylusIsNotPreemptedByMouse()
    {
        var r = new PointerRouter();
        r.Process(PointerAction.Down, S(1, Stylus, 0, 0, ticks: Ticks(0)));
        // 笔正在写时来了鼠标按下（同一交互内）→ 应被忽略
        var res = r.Process(PointerAction.Down, S(2, Mouse, 5, 5, ticks: Ticks(10)));
        Check.Equal(RouteDecision.Ignore, res.Decision, "笔优先，鼠标不应抢占");
    }

    public static void Test_Router_SuppressesSynthesizedMouseAfterTouch()
    {
        // 触屏上 WPF 会先抛 Touch、再合成 Mouse —— 后者必须被忽略，否则"画两遍"
        var r = new PointerRouter { MouseSuppressMs = 2000 };
        var down = r.Process(PointerAction.Down, S(1, Touch, 10, 10, ticks: Ticks(0)));
        Check.Equal(RouteDecision.BeginStroke, down.Decision, "触摸应开始笔画");

        var synth = r.Process(PointerAction.Down, S(2, Mouse, 10, 10, ticks: Ticks(20)));
        Check.Equal(RouteDecision.Ignore, synth.Decision, "合成的鼠标事件应被忽略");
    }

    public static void Test_Router_MouseAllowedAfterGracePeriod()
    {
        var r = new PointerRouter { MouseSuppressMs = 2000 };
        r.Process(PointerAction.Down, S(1, Touch, 10, 10, ticks: Ticks(0)));
        r.Process(PointerAction.Up, S(1, Touch, 10, 10, ticks: Ticks(50)));

        var late = r.Process(PointerAction.Down, S(2, Mouse, 10, 10, ticks: Ticks(5000)));
        Check.Equal(RouteDecision.BeginStroke, late.Decision, "超过宽限期后鼠标应可用");
    }

    // ---- 单指书写 ----

    public static void Test_Router_SingleTouchStrokeLifecycle()
    {
        var r = new PointerRouter();
        Check.Equal(RouteDecision.BeginStroke,
            r.Process(PointerAction.Down, S(1, Touch, 0, 0, ticks: Ticks(0))).Decision, "按下");
        Check.Equal(RouteDecision.ContinueStroke,
            r.Process(PointerAction.Move, S(1, Touch, 10, 10, ticks: Ticks(10))).Decision, "移动");
        Check.Equal(RouteDecision.EndStroke,
            r.Process(PointerAction.Up, S(1, Touch, 20, 20, ticks: Ticks(20))).Decision, "抬起");
        Check.True(!r.IsStrokeActive, "结束后不应仍处于书写态");
    }

    public static void Test_Router_OtherPointerMoveIgnoredDuringStroke()
    {
        var r = new PointerRouter();
        r.Process(PointerAction.Down, S(1, Touch, 0, 0, ticks: Ticks(0)));
        var res = r.Process(PointerAction.Move, S(99, Touch, 50, 50, ticks: Ticks(5)));
        Check.Equal(RouteDecision.Ignore, res.Decision, "非当前指针的移动应被忽略");
    }

    // ---- 双指手势 vs 书写（决策 Q4）----

    public static void Test_Router_SecondFinger_BoundaryFollowsGestureWindow()
    {
        // 决策 Q4 的**细化**：单纯"笔画中一律忽略第二指"会让两指手势永远无法触发。
        // 实际规则按**手势判定窗口**（默认 150ms / 6 点）分界：
        //   窗口内 → 取消这点头笔画并进入手势
        //   窗口外 → 忽略第二指，保护书写

        // 边界内（100ms，2 点）
        var a = new PointerRouter { GestureWindowMs = 150, GestureMaxPoints = 6 };
        a.Process(PointerAction.Down, S(1, Touch, 0, 0, ticks: Ticks(0)));
        a.Process(PointerAction.Move, S(1, Touch, 5, 0, ticks: Ticks(50)));
        var inWindow = a.Process(PointerAction.Down, S(2, Touch, 100, 0, ticks: Ticks(100)));
        Check.Equal(RouteDecision.StartGesture, inWindow.Decision, "窗口内第二指应进入手势");
        Check.True(inWindow.CancelledStroke, "并丢弃这点头笔画");

        // 边界外（200ms，点数也超过 6）
        var b = new PointerRouter { GestureWindowMs = 150, GestureMaxPoints = 6 };
        b.Process(PointerAction.Down, S(1, Touch, 0, 0, ticks: Ticks(0)));
        for (var i = 0; i < 8; i++)
            b.Process(PointerAction.Move, S(1, Touch, i * 4, 0, ticks: Ticks(20 + i * 20)));
        var outOfWindow = b.Process(PointerAction.Down, S(2, Touch, 100, 0, ticks: Ticks(200)));
        Check.Equal(RouteDecision.Ignore, outOfWindow.Decision, "窗口外第二指应被忽略");
        Check.True(b.IsStrokeActive, "笔画应继续");
        Check.True(!b.IsGestureActive, "不应进入手势");
    }

    public static void Test_Router_TwoFingersQuicklyStartsGestureAndCancelsStroke()
    {
        // 手势判定窗口内第二指到来 → 丢弃这点头笔画并进入手势
        var r = new PointerRouter { GestureWindowMs = 150 };
        Check.Equal(RouteDecision.BeginStroke,
            r.Process(PointerAction.Down, S(1, Touch, 0, 0, ticks: Ticks(0))).Decision, "第一指先开始笔画");

        var res = r.Process(PointerAction.Down, S(2, Touch, 100, 0, ticks: Ticks(40)));
        Check.Equal(RouteDecision.StartGesture, res.Decision, "窗口内第二指应进入手势");
        Check.True(res.CancelledStroke, "应同时提示丢弃这点头笔画");
        Check.True(r.IsGestureActive, "应处于手势中");
    }

    public static void Test_Router_SecondFingerAfterWindowIsIgnored()
    {
        // 已经写了较久（超出窗口 / 点数超限）→ 保护书写，忽略第二指
        var r = new PointerRouter { GestureWindowMs = 150, GestureMaxPoints = 6 };
        r.Process(PointerAction.Down, S(1, Touch, 0, 0, ticks: Ticks(0)));
        for (var i = 0; i < 10; i++)
            r.Process(PointerAction.Move, S(1, Touch, i * 5, 0, ticks: Ticks(10 + i * 10)));

        var res = r.Process(PointerAction.Down, S(2, Touch, 200, 0, ticks: Ticks(600)));
        Check.Equal(RouteDecision.Ignore, res.Decision, "超出窗口的第二指应被忽略");
        Check.True(r.IsStrokeActive, "笔画应继续");
        Check.True(!r.IsGestureActive, "不应进入手势");
    }

    public static void Test_Router_GestureEndsWhenFingerLifted()
    {
        var r = new PointerRouter { GestureWindowMs = 500 };
        r.Process(PointerAction.Down, S(1, Touch, 0, 0, ticks: Ticks(0)));
        r.Process(PointerAction.Down, S(2, Touch, 100, 0, ticks: Ticks(30)));
        Check.True(r.IsGestureActive, "已进入手势");

        var res = r.Process(PointerAction.Up, S(1, Touch, 0, 0, ticks: Ticks(50)));
        Check.Equal(RouteDecision.EndGesture, res.Decision, "抬起一指应结束手势");
        Check.True(!r.IsGestureActive, "手势已结束");
    }

    public static void Test_Router_StylusIgnoredDuringGesture()
    {
        var r = new PointerRouter { GestureWindowMs = 500 };
        r.Process(PointerAction.Down, S(1, Touch, 0, 0, ticks: Ticks(0)));
        var g = r.Process(PointerAction.Down, S(2, Touch, 100, 0, ticks: Ticks(30)));
        Check.True(g.CancelledStroke, "应进入手势（丢弃笔画）");

        var res = r.Process(PointerAction.Down, S(3, Stylus, 50, 50, ticks: Ticks(60)));
        Check.Equal(RouteDecision.Ignore, res.Decision, "手势期间不应接受笔");
    }

    // ---- 手势数学 ----

    public static void Test_Gesture_ZoomKeepsMidpoint()
    {
        var vp = new ViewportState { Zoom = 1, PanX = 0, PanY = 0 };
        var g = new TouchGestureRecognizer();

        var start = new List<PointerSample> { S(1, Touch, 100, 100), S(2, Touch, 200, 100) };
        var worldUnderMid = vp.ScreenToWorld(TouchGestureRecognizer.Midpoint(start));

        Check.True(g.Begin(start, vp), "应能开始手势");

        // 两指间距翻倍 → 缩放 ×2；中点右移 50
        var moved = new List<PointerSample> { S(1, Touch, 150, 100), S(2, Touch, 350, 100) };
        g.ApplyTo(vp, moved);

        Check.Near(2.0, vp.Zoom, 1e-9, "缩放应变为 2");

        var newMid = TouchGestureRecognizer.Midpoint(moved);
        var worldUnderNewMid = vp.ScreenToWorld(newMid);
        Check.Near(worldUnderMid.X, worldUnderNewMid.X, 1e-6, "起始中点下的世界坐标 X 应跟随当前中点");
        Check.Near(worldUnderMid.Y, worldUnderNewMid.Y, 1e-6, "起始中点下的世界坐标 Y 应跟随当前中点");
    }

    public static void Test_Gesture_ZoomClamped()
    {
        var vp = new ViewportState { Zoom = 1 };
        var g = new TouchGestureRecognizer();
        g.Begin([S(1, Touch, 0, 0), S(2, Touch, 10, 0)], vp);

        // 间距放大 1000 倍 → 应被钳制在 50
        g.ApplyTo(vp, [S(1, Touch, 0, 0), S(2, Touch, 10000, 0)]);
        Check.Equal(ViewportState.MaxZoom, vp.Zoom, "缩放上限 50");
    }

    // ---- 拖动阈值（决策 D2：4px）----

    public static void Test_DragTracker_Threshold()
    {
        var d = new DragTracker { ThresholdPx = 4 };
        d.Begin(new PointD(100, 100));

        Check.True(!d.Update(new PointD(102, 100)), "位移 2px 未越阈值");
        Check.True(!d.IsDragging, "不应判定为拖动");

        Check.True(d.Update(new PointD(105, 100)), "位移 5px 越过阈值，应触发拖动起点");
        Check.True(d.IsDragging, "应判定为拖动");
        Check.True(!d.Update(new PointD(110, 100)), "已拖动后不再重复触发");
    }

    public static void Test_DragTracker_Delta()
    {
        var d = new DragTracker { ThresholdPx = 4 };
        d.Begin(new PointD(100, 100));
        d.Update(new PointD(120, 130));
        Check.Near(20, d.Delta.X, 1e-9, "位移 X");
        Check.Near(30, d.Delta.Y, 1e-9, "位移 Y");
    }

    // ---- 触摸面积 → 橡皮直径 ----

    public static void Test_TouchArea_ConvexHull()
    {
        var pts = new List<PointD>
        {
            new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(5, 5)  // 最后一个在内部
        };
        var hull = TouchAreaEstimator.ConvexHull(pts);
        Check.Equal(4, hull.Count, "内部点不应出现在凸包上");
        Check.Near(100, TouchAreaEstimator.PolygonArea(hull), 1e-9, "凸包面积应为 100");
    }

    public static void Test_TouchArea_DiameterClampedToMinimum()
    {
        // 很小的触点 → 直径取下限 60（原需求：最小不小于 40，建议 60 起）
        var tiny = new List<PointD> { new(0, 0), new(2, 0), new(0, 2) };
        var d = TouchAreaEstimator.EstimateDiameter(tiny);
        Check.Equal(TouchAreaEstimator.MinDiameter, d, "小面积应取下限 60");
    }

    public static void Test_TouchArea_DiameterGrowsWithArea()
    {
        var small = new List<PointD> { new(0, 0), new(20, 0), new(20, 20), new(0, 20) };   // 面积 400
        var large = new List<PointD> { new(0, 0), new(80, 0), new(80, 80), new(0, 80) };   // 面积 6400

        var ds = TouchAreaEstimator.EstimateDiameter(small);
        var dl = TouchAreaEstimator.EstimateDiameter(large);

        Check.True(dl > ds, $"面积大的触点应得到更大直径（{ds:0} → {dl:0}）");
        Check.Near(Math.Sqrt(6400) * 1.6, dl, 1e-6, "大触点直径 = √area×1.6");
    }

    public static void Test_TouchArea_DiameterClampedToMaximum()
    {
        var huge = new List<PointD> { new(0, 0), new(2000, 0), new(2000, 2000), new(0, 2000) };
        Check.Equal(TouchAreaEstimator.MaxDiameter, TouchAreaEstimator.EstimateDiameter(huge), "应取上限 420");
    }

    // ---- 掌拒 / 手掌擦除（决策 D3）----

    public static void Test_Palm_RejectsWideTouch()
    {
        var p = new PalmRejector { PalmContactWidth = 45 };
        Check.True(p.IsPalm(S(1, Touch, 0, 0, contactWidth: 60)), "宽触点应判为手掌");
        Check.True(!p.IsPalm(S(1, Touch, 0, 0, contactWidth: 20)), "窄触点不是手掌");
        Check.True(!p.IsPalm(S(1, Stylus, 0, 0, contactWidth: 60)), "笔不受影响");
    }

    public static void Test_Palm_RejectsTouchWhileStylusActive()
    {
        var p = new PalmRejector();
        Check.True(p.ShouldReject(S(1, Touch, 0, 0, contactWidth: 10), stylusActive: true),
            "笔活跃时应收起全部触摸");
        Check.True(!p.ShouldReject(S(1, Touch, 0, 0, contactWidth: 10), stylusActive: false),
            "笔不活跃时正常触摸应放行");
        Check.True(p.ShouldReject(S(1, Touch, 0, 0, contactWidth: 60), stylusActive: false),
            "手掌应被拒绝");
    }

    public static void Test_Palm_ActsAsEraserWhenEraserToolActive()
    {
        // 决策 D3：橡皮工具下，手掌 = 大号橡皮（不拒绝）
        var p = new PalmRejector { PalmActsAsEraser = true };
        var palm = S(1, Touch, 0, 0, contactWidth: 80);
        Check.True(p.IsPalm(palm), "应识别为手掌");
        Check.True(!p.ShouldReject(palm, stylusActive: false), "橡皮工具下不应拒绝手掌，应转为擦除");
    }

    public static void Test_Router_Reset()
    {
        var r = new PointerRouter();
        r.Process(PointerAction.Down, S(1, Touch, 0, 0, ticks: Ticks(0)));
        r.Reset();
        Check.True(!r.IsStrokeActive, "复位后不应处于书写态");
        Check.Equal(0, r.ActiveTouchCount, "复位后无活跃触点");
    }
}
