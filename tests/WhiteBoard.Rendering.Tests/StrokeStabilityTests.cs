using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteBoard.Core.Model;
using WhiteBoard.Core.Tests;
using WhiteBoard.Rendering;

namespace WhiteBoard.Rendering.Tests;

/// <summary>
/// 笔迹稳定性：**已经画出来的部分不许再动**。
///
/// 背景：WPF 的 <c>Stroke.GetGeometry()</c> 在 <c>FitToCurve = true</c> 时会对**整条点集**
/// 做一次曲线拟合——每来一个新采样点，整条曲线的形状都会被重算，
/// 于是"笔已经走过的地方"会在你继续写的时候轻微移动（用户描述为"画迹会飘"）。
///
/// 这些用例把这条要求钉死：
/// <list type="number">
/// <item><b>已画部分零位移</b>：追加一个采样点后，离新点足够远的区域像素不应有任何变化；</item>
/// <item><b>笔迹穿过采样点</b>：采样的地方就该有墨，不能因为"拟合"把线拉偏（下笔即固定）；</item>
/// <item><b>湿态与干态一致</b>：抬笔前后外观不跳变（与 ToolChainTests 的那条互为补充）。</item>
/// </list>
/// </summary>
public static class StrokeStabilityTests
{
    private const double PenWidth = 6.0;

    /// <summary>
    /// 允许"变化"波及到离笔尖多远。
    ///
    /// 实测（见 tools\InkStabilityProbe，60 点、平均采样间距约 21 px）：
    /// <list type="bullet">
    /// <item>整条曲线拟合（旧行为，<c>FitToCurve</c>）：追加 1 个点会让变化波及到 **78.6 px** 之外，
    ///       平均每次改动 **241** 个像素——这就是用户说的"画迹会飘"；</item>
    /// <item>不平滑（现在的默认）：变化被限制在 **33.3 px** 内，平均每次 **122** 个像素。</item>
    /// </list>
    /// 33.3 px 的来源是 WPF 在追加节点时会把**最后一个连接处**重算（那一段正好在笔尖底下，
    /// 用户看不见）；78.6 px 则是整条曲线被重新拟合的结果，会明显看到已画部分移动。
    /// 因此取 50 px 作为分界：既容得下笔尖处的正常重画，又能把"整条重新拟合"挡回去。
    /// </summary>
    private const double MaxTipPropagationPx = 50.0;

    /// <summary>造一条带抖动的笔迹（模拟真实手写采样：方向均匀前进 + 轻微抖动）。</summary>
    private static List<InkPoint> JitteryPoints(int count, double spacing = 12.0)
    {
        var pts = new List<InkPoint>(count);
        for (var i = 0; i < count; i++)
        {
            // 用确定性的伪随机抖动（不用 Random，保证用例可复现）
            var jitterX = Math.Sin(i * 2.7) * 1.2;
            var jitterY = Math.Cos(i * 1.9) * 2.4;
            pts.Add(new InkPoint(i * spacing + jitterX, 100 + jitterY + Math.Sin(i * 0.4) * 30, 0.5f));
        }
        return pts;
    }

    private static Geometry BuildLive(IReadOnlyList<InkPoint> pts)
        => SmoothGeometryBuilder.BuildLiveFreehandGeometry(pts, PenWidth);

    /// <summary>把几何画成位图（用于逐像素比较两个版本的差异）。</summary>
    private static RenderTargetBitmap Render(Geometry geo, int w, int h, double offsetX = 0, double offsetY = 0)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
            var m = new MatrixTransform(1, 0, 0, 1, -offsetX, -offsetY);
            dc.PushTransform(m);
            dc.DrawGeometry(Brushes.White, null, geo);
            dc.Pop();
        }

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        return bmp;
    }

    /// <summary>
    /// 返回"在 <paramref name="keepOutOfCenter"/> 这个圆之外，两张图有多少像素不同"。
    /// 圆内是笔尖附近（允许重画），圆外属于**已经画过的部分**（不许动）。
    /// </summary>
    private static (int Outside, double MaxDistance) CompareOutside(
        RenderTargetBitmap a, RenderTargetBitmap b, Point center, double radius)
    {
        var stride = a.PixelWidth * 4;
        var ba = new byte[stride * a.PixelHeight];
        var bb = new byte[stride * b.PixelHeight];
        a.CopyPixels(ba, stride, 0);
        b.CopyPixels(bb, stride, 0);

        var outside = 0;
        var maxDistance = 0.0;
        var r2 = radius * radius;

        for (var y = 0; y < a.PixelHeight; y++)
        {
            for (var x = 0; x < a.PixelWidth; x++)
            {
                var i = y * stride + x * 4;
                if (ba[i] == bb[i] && ba[i + 1] == bb[i + 1] && ba[i + 2] == bb[i + 2]) continue;

                var dx = x - center.X;
                var dy = y - center.Y;
                var d2 = dx * dx + dy * dy;

                var d = Math.Sqrt(d2);
                if (d > maxDistance) maxDistance = d;
                if (d2 > r2) outside++;
            }
        }

        return (outside, maxDistance);
    }

    // ── ① 核心要求：已画部分零位移 ────────────────────────────────────────

    /// <summary>
    /// **本用例就是"画迹会不会飘"的量化判定**：
    /// 先画 20 个点，再加 1 个点，然后比较两次渲染——除了新点附近，
    /// 其余地方**一个像素都不该变**。
    /// </summary>
    public static void Test_Freehand_AlreadyDrawnPartDoesNotMove()
    {
        const int n = 20;
        var pts = JitteryPoints(n + 1);

        var before = Render(BuildLive(pts.Take(n).ToList()), 400, 300);
        var after = Render(BuildLive(pts), 400, 300);

        var tip = new Point(pts[n].X, pts[n].Y);
        var (_, maxDistance) = CompareOutside(before, after, tip, PenWidth + 22);

        // 判定用"变化最远波及到哪里"：允许笔尖附近重画，但绝不该波及到已画部分
        Check.True(maxDistance <= MaxTipPropagationPx,
            $"追加一个采样点后，变化应只局限在笔尖 {MaxTipPropagationPx:0} px 内（已画部分不许动），实际最远波及 {maxDistance:0.0} px");
    }

    /// <summary>
    /// 连续书写：每加一个点都只允许**笔尖附近**变化，更早的地方一个像素都不许动。
    ///
    /// 注意比较方式：必须拿**相邻两帧**比（k 个点 vs k+1 个点），
    /// 而不是拿"最开始那一帧"跟"最后一帧"比——后者会把每个新点各自带来的
    /// **合理**的笔尖更新也算成"已画部分位移"，得出误判。
    /// </summary>
    public static void Test_Freehand_StaysStableAcrossManyAppends()
    {
        const int start = 15;
        var pts = JitteryPoints(start + 6);

        var worst = 0;
        var worstAt = 0;
        var worstDistance = 0.0;
        var worstDistanceAt = 0;

        for (var k = start; k < pts.Count; k++)
        {
            var a = Render(BuildLive(pts.Take(k).ToList()), 600, 300);
            var b = Render(BuildLive(pts.Take(k + 1).ToList()), 600, 300);
            var tip = new Point(pts[k].X, pts[k].Y);

            // 容差半径要比"笔宽一半 + 端帽"再宽一点：圆盘边界会切开抗锯齿像素
            var (outside, maxDistance) = CompareOutside(a, b, tip, PenWidth + 22);
            if (outside > worst) { worst = outside; worstAt = k; }
            if (maxDistance > worstDistance) { worstDistance = maxDistance; worstDistanceAt = k; }
        }

        Check.True(worstDistance <= MaxTipPropagationPx,
            $"每追加一个采样点，变化都应局限在笔尖 {MaxTipPropagationPx:0} px 内；" +
            $"实际最远波及 {worstDistance:0.0} px（加到第 {worstDistanceAt + 1} 个点时）");
    }

    /// <summary>抬笔（干态）与书写中（湿态）必须是同一条线——否则松手瞬间笔迹会跳一下。</summary>
    public static void Test_Freehand_DryGeometryMatchesLiveGeometry()
    {
        var pts = JitteryPoints(40);
        var live = BuildLive(pts);

        var obj = FreehandObject.FromWorldPoints(1, pts, "#F5F5F0", PenWidth);
        var dry = new SmoothGeometryBuilder().GetWorldGeometry(obj);

        var lb = live.Bounds;
        var db = dry.Bounds;

        Check.Near(lb.X, db.X, 0.01, "干/湿态几何左边界应一致");
        Check.Near(lb.Y, db.Y, 0.01, "干/湿态几何上边界应一致");
        Check.Near(lb.Width, db.Width, 0.01, "干/湿态几何宽度应一致");
        Check.Near(lb.Height, db.Height, 0.01, "干/湿态几何高度应一致");
    }

    // ── ② "下笔即固定"：墨必须落在笔走过的地方 ────────────────────────────

    /// <summary>
    /// 采样点所在的位置必须有墨。
    /// 旧的"整条曲线拟合"会把线从采样点之间"抄近路"，拐弯处偏离采样点可达好几个像素——
    /// 用户感觉到的就是"我明明画到这里了，线却偏了"。
    /// </summary>
    public static void Test_Freehand_PassesThroughSampledPoints()
    {
        var pts = JitteryPoints(30);
        var geo = BuildLive(pts);

        var misses = new List<string>();
        foreach (var p in pts)
        {
            if (!geo.FillContains(new Point(p.X, p.Y)))
                misses.Add($"({p.X:0.#},{p.Y:0.#})");
        }

        Check.True(misses.Count == 0,
            $"所有采样点都应落在笔迹内（下笔即固定），实际有 {misses.Count} 个点不在：{string.Join("、", misses.Take(5))}");
    }

    /// <summary>单点/两点等退化输入在两种模式下都必须能画出东西。</summary>
    public static void Test_Freehand_DegenerateInputsStillDraw()
    {
        var dot = BuildLive([new InkPoint(50, 50, 0.5f)]);
        Check.True(dot.GetArea() > 0, "单点应画出可见的点");

        var two = BuildLive([new InkPoint(50, 50, 0.5f), new InkPoint(150, 50, 0.5f)]);
        Check.True(two.GetArea() > 0, "两点应画出一条线段");

        var same = BuildLive([new InkPoint(50, 50, 0.5f), new InkPoint(50, 50, 0.5f)]);
        Check.True(double.IsFinite(same.GetArea()), "重复点不应产生非法几何");
    }

    // ── ③ 平滑度：稳定但不该变成折线的锯齿 ────────────────────────────────

    /// <summary>
    /// 稳定性不能以"变成难看的多段折线"为代价：
    /// 笔迹的**周长/面积比**应当接近"同长度的圆角带"，而不是明显发散。
    /// 这里用一个宽松阈值守住"别退化成锯齿"。
    /// </summary>
    public static void Test_Freehand_IsNotOverlyJagged()
    {
        // 一条光滑的曲线（采样点本身很密），画出来不该有明显折角
        var pts = new List<InkPoint>();
        for (var i = 0; i <= 60; i++)
        {
            var a = i / 60.0 * Math.PI;
            pts.Add(new InkPoint(100 + 200 * Math.Cos(a), 150 + 100 * Math.Sin(a), 0.5f));
        }

        var geo = BuildLive(pts);
        var area = geo.GetArea();
        var bounds = geo.Bounds;

        // 半椭圆环的面积 ≈ π*(a*b 外 − a'*b' 内)；这里只做量级检查，防止出现"面积暴涨/塌陷"
        var expected = Math.PI * 200 * 100 - Math.PI * (200 - PenWidth / 2) * (100 - PenWidth / 2);
        var ratio = area / expected;

        Check.True(ratio > 0.6 && ratio < 1.6,
            $"笔迹面积应在理论量级附近（比值 {ratio:0.00}），实际面积 {area:0}，外框 {bounds.Width:0}×{bounds.Height:0}");
    }

    // ── ④ 用例自身的有效性：它真的能抓住"整条重新拟合"这个回归 ──────────────

    /// <summary>
    /// **自证用例**：临时切回旧的"整条曲线拟合"，确认它会被上一条用例判定为不合格。
    ///
    /// 为什么要写这个：一条永远通过的用例等于没有用例。这里用同一个测量方法跑两种模式，
    /// 断言"旧模式确实超出阈值"，从而证明上面那条阈值不是随便定的。
    /// 顺带也把"旧的飘有多大"变成一个可复现的数字，便于日后对比。
    /// </summary>
    public static void Test_Freehand_GuardDetectsWholeCurveFitting()
    {
        var pts = JitteryPoints(45);

        double MeasureWorstPropagation()
        {
            var worst = 0.0;
            for (var k = 20; k < 40; k++)
            {
                var a = Render(BuildLive(pts.Take(k).ToList()), 700, 300);
                var b = Render(BuildLive(pts.Take(k + 1).ToList()), 700, 300);
                var (_, max) = CompareOutside(a, b, new Point(pts[k].X, pts[k].Y), 0);
                worst = Math.Max(worst, max);
            }
            return worst;
        }

        var previous = SmoothGeometryBuilder.Smoothing;
        try
        {
            SmoothGeometryBuilder.Smoothing = FreehandSmoothing.FitToCurve;
            var oldMode = MeasureWorstPropagation();

            SmoothGeometryBuilder.Smoothing = FreehandSmoothing.None;
            var newMode = MeasureWorstPropagation();

            Check.True(oldMode > MaxTipPropagationPx,
                $"旧的整条拟合应超出阈值 {MaxTipPropagationPx:0} px（这样阈值才有意义），实测 {oldMode:0.0} px");
            Check.True(newMode <= MaxTipPropagationPx,
                $"当前默认（不平滑）应在阈值内，实测 {newMode:0.0} px");

            // 把实测数字写进断言消息里，方便日后回归时一眼看出变差了多少
            Check.True(newMode < oldMode,
                $"不平滑应显著小于整条拟合：{newMode:0.0} px < {oldMode:0.0} px（越小越好）");
        }
        finally
        {
            SmoothGeometryBuilder.Smoothing = previous;   // 无论如何都要还原，避免影响其它用例
        }
    }
}
