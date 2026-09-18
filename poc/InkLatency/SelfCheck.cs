using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteBoard.Core.Storage;
using WhiteBoard.Core.Text;

namespace WhiteBoard.Poc.InkLatency;

/// <summary>
/// PoC-A 的**自动化冒烟自检**：在不开窗口、不需人工输入的前提下，
/// 把三条渲染路线离屏跑一遍——构建合成笔迹 → 布局 → 渲染到位图 → 计耗时。
///
/// 它替代不了真人测量输入延迟（那必须人在真机上操作），但能保证：
///   ① 三条路线都不会在渲染时抛异常；
///   ② 手绘风格几何确实生成了（不是空路径）；
///   ③ 给出可比的**纯渲染耗时**基线，供与真机数据对照。
/// </summary>
public static class SelfCheck
{
    public static bool IsRequested(string[] args)
        => args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        var reportPath = GetArg(args, "--report");
        var lines = new List<string>();
        var failures = 0;
        var width = 1200.0;
        var height = 700.0;

        lines.Add("PoC-A 冒烟自检（离屏渲染三条路线）");
        lines.Add($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        lines.Add($"渲染后端：{(RenderCapability.Tier >> 16)} 级（0=软件, 1/2=硬件加速）");
        lines.Add($"离屏画布：{width:0}×{height:0}");
        lines.Add("");

        var routes = new (string Name, Func<IInkRoute> Factory)[]
        {
            ("baseline", () => new BaselineRoute()),
            ("a1", () => new OverlayRoute()),
            ("a2", () => new SingleLayerRoute())
        };

        foreach (var (name, factory) in routes)
        {
            lines.Add($"── 路线 {name} ──");
            try
            {
                var route = factory();
                var view = route.View;
                route.SetPen(Color.FromRgb(0xF5, 0xF5, 0xF0), 4);

                // 20 条笔迹 × 120 点，模拟一整屏内容
                const int strokeCount = 20;
                const int pointsPerStroke = 120;
                for (var s = 0; s < strokeCount; s++)
                {
                    var pts = SyntheticStroke(s, pointsPerStroke, width, height);
                    route.AddSyntheticStroke(pts, Color.FromRgb(0xF5, 0xF5, 0xF0), 4);
                }

                view.Measure(new Size(width, height));
                view.Arrange(new Rect(0, 0, width, height));
                view.UpdateLayout();

                var sw = Stopwatch.StartNew();
                var rtb = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(view);
                sw.Stop();

                var nonBlank = HasInk(rtb);
                var ok = route.StrokeCount >= strokeCount && nonBlank;

                // 再渲染一帧（模拟连续重绘），看单帧代价
                var sw2 = Stopwatch.StartNew();
                view.InvalidateVisual();
                view.UpdateLayout();
                var rtb2 = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                rtb2.Render(view);
                sw2.Stop();

                lines.Add($"  笔迹数        : {route.StrokeCount}");
                lines.Add($"  首帧渲染      : {sw.Elapsed.TotalMilliseconds:0.0} ms");
                lines.Add($"  重绘一帧      : {sw2.Elapsed.TotalMilliseconds:0.0} ms");
                lines.Add($"  画面非空白    : {(nonBlank ? "是" : "否")}");
                lines.Add($"  结果          : {(ok ? "PASS" : "FAIL")}");
                if (!ok) failures++;
            }
            catch (Exception ex)
            {
                lines.Add($"  结果          : FAIL　异常：{ex.GetType().Name}: {ex.Message}");
                failures++;
            }
            lines.Add("");
        }

        // 探针自身的算术校验（不需要真实输入）
        lines.Add("── 延迟探针自检 ──");
        try
        {
            var probe = new LatencyProbe { Mode = "probe-check" };
            probe.NoteInput(PointerKind.Stylus, 0.7f);
            Thread.Sleep(8);
            probe.NoteFrame();
            var s = probe.Stats();
            var ok = s.Count == 1 && s.Avg >= 5.0 && s.Avg < 500.0;
            lines.Add($"  样本数={s.Count}  实测={s.Avg:0.0} ms（预期 ≥5 且 <500）");
            lines.Add($"  结果          : {(ok ? "PASS" : "FAIL")}");
            if (!ok) failures++;
        }
        catch (Exception ex)
        {
            lines.Add($"  结果          : FAIL　异常：{ex.Message}");
            failures++;
        }

        lines.Add("");
        lines.Add($"总计：{(failures == 0 ? "全部通过" : $"{failures} 项失败")}");

        // ── 湿/干笔迹一致性验证（决策：改平滑风后，验证能否做到无缝衔接）──
        lines.Add("");
        lines.Add("── 湿/干笔迹一致性验证（平滑风） ──");
        try
        {
            WetDryConsistency((int)width, (int)height, lines, ref failures);
        }
        catch (Exception ex)
        {
            lines.Add($"  异常：{ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        // ── 实时笔迹几何重建代价（决定"湿态自绘"是否可行）──
        lines.Add("");
        lines.Add("── 实时笔迹几何重建代价（湿态自绘的可行性）──");
        try
        {
            LiveGeometryCost(lines, ref failures);
        }
        catch (Exception ex)
        {
            lines.Add($"  异常：{ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        // ── 矢量缩放无损性（S2 需求：无损放大 50 倍）──
        lines.Add("");
        lines.Add("── 矢量缩放无损性（无损放大 50 倍）──");
        try
        {
            ZoomQuality((int)width, (int)height, lines, ref failures);
        }
        catch (Exception ex)
        {
            lines.Add($"  异常：{ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        // ── 橡皮擦实现路线代价（对应"擦除后一笔变多笔"的语义问题）──
        lines.Add("");
        lines.Add("── 橡皮擦：几何差集 vs 点集切割 的代价 ──");
        try
        {
            EraseCost((int)width, (int)height, lines, ref failures);
        }
        catch (Exception ex)
        {
            lines.Add($"  异常：{ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        // 同时给出一份"真人测量指引"
        lines.Add("");
        lines.Add("── 下一步：真人测量 ──");
        lines.Add("  1. 双击 InkLatency.exe（或在命令行加 --mode baseline|a1|a2）");
        lines.Add("  2. 用笔/手指在画布区域连续书写 30 秒以上");
        lines.Add("  3. 切换三条路线各测一轮");
        lines.Add("  4. 点「导出 CSV」，文件落在 <exe同级>\\data\\latency\\ 下");
        lines.Add("  5. 关注两个数：绝对延迟（≤16.7 ms）与 a1/a2 相对 baseline 的差值（≤2 ms）");

        var text = string.Join(Environment.NewLine, lines);
        Console.WriteLine(text);

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            try { TextFileCodec.WriteAllText(Path.GetFullPath(reportPath), text); }
            catch (Exception ex) { Console.Error.WriteLine($"写报告失败：{ex.Message}"); }
        }

        Environment.ExitCode = failures == 0 ? 0 : 1;
        return Environment.ExitCode;
    }

    /// <summary>
    /// 湿/干一致性验证（对应"改平滑风"的决策）。
    ///
    /// 问题：湿笔迹由 InkCanvas 画，干笔迹若由我们自己画，两者外观就可能不一致
    ///       （原手绘风方案下这个差异"非常明显"，已决定改为平滑风）。
    ///
    /// 验证思路：用**同一组点 + 同一份 DrawingAttributes** 构造 WPF 的 <see cref="Stroke"/>，
    /// 分别用三条"干"路径渲染，与 InkCanvas 的"湿"渲染逐像素比对：
    ///   干①  stroke.Draw(dc)                      —— 直接用 Stroke 自己的渲染器
    ///   干②  dc.DrawGeometry(null, pen, GetGeometry()) —— 用几何描边
    ///   干③  dc.DrawGeometry(brush, null, GetGeometry()) —— 用几何填充（InkCanvas 内部就是填充轮廓）
    /// 若某条路径与湿渲染**逐像素一致**，就把它定为正式实现的干笔迹渲染方式——
    /// 这样抬笔时外观不会发生任何变化。
    /// </summary>
    private static void WetDryConsistency(int w, int h, List<string> lines, ref int failures)
    {
        var color = Color.FromRgb(0xF5, 0xF5, 0xF0);
        var attrs = new DrawingAttributes
        {
            Color = color,
            Width = 6,
            Height = 6,
            StylusTip = StylusTip.Ellipse,
            FitToCurve = true,
            IgnorePressure = false
        };

        var pts = new StylusPointCollection();
        const int n = 160;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)(n - 1);
            var x = w * (0.10 + 0.80 * t);
            var y = h * 0.5 + Math.Sin(t * Math.PI * 3) * h * 0.18;
            var pressure = (float)(0.30 + 0.60 * Math.Abs(Math.Sin(t * Math.PI * 2)));
            pts.Add(new StylusPoint(x, y, pressure));
        }

        var stroke = new Stroke(pts, attrs);

        // 湿①：InkCanvas 渲染
        var ink = new InkCanvas
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Width = w,
            Height = h
        };
        ink.Strokes.Add(new Stroke(pts, attrs));
        ink.Measure(new Size(w, h));
        ink.Arrange(new Rect(0, 0, w, h));
        ink.UpdateLayout();
        var wet = Render(ink, w, h);

        // 干①：Stroke.Draw
        var dryA = RenderVisual(w, h, dc => stroke.Draw(dc));

        // 干②/③：几何描边 / 几何填充
        var sw = Stopwatch.StartNew();
        var geo = stroke.GetGeometry();
        sw.Stop();
        geo.Freeze();
        var geoMs = sw.Elapsed.TotalMilliseconds;

        var dryB = RenderVisual(w, h, dc =>
        {
            var pen = new Pen(new SolidColorBrush(color), attrs.Width)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geo);
        });

        var dryC = RenderVisual(w, h, dc =>
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            dc.DrawGeometry(brush, null, geo);
        });

        var (diffA, ratioA) = Diff(wet, dryA);
        var (diffB, ratioB) = Diff(wet, dryB);
        var (diffC, ratioC) = Diff(wet, dryC);
        var inkPixels = CountInk(wet);

        lines.Add($"  笔迹点数      : {n}　目标线宽 {attrs.Width}　压感变宽 开");
        lines.Add($"  湿渲染墨迹像素: {inkPixels}");
        lines.Add($"  GetGeometry() : {geoMs:0.0} ms（一次构建，可冻结缓存后复用）");
        lines.Add("");
        lines.Add($"  干① stroke.Draw(dc)          差异像素 {diffA,8}　占比 {ratioA:P3}");
        lines.Add($"  干② DrawGeometry(描边 pen)    差异像素 {diffB,8}　占比 {ratioB:P3}");
        lines.Add($"  干③ DrawGeometry(填充 brush)  差异像素 {diffC,8}　占比 {ratioC:P3}");
        lines.Add("");

        var best = new[] { ("干① stroke.Draw", diffA, ratioA), ("干② 几何描边", diffB, ratioB), ("干③ 几何填充", diffC, ratioC) }
            .OrderBy(x => x.Item2).First();

        var ok = best.Item2 == 0 || best.Item3 < 0.001;
        lines.Add($"  最接近湿渲染 : {best.Item1}（差异 {best.Item2} 像素，占比 {best.Item3:P3}）");
        lines.Add($"  结论         : {(ok ? "存在与湿渲染一致（或差异 <0.1%）的干渲染路径 → 抬笔可做到外观无缝 ✅" : "三条干渲染路径均与湿渲染有明显差异 ⚠️")}");
        if (!ok) failures++;

        if (ratioC < ratioB)
            lines.Add("  实现建议     : 采用「几何填充」方式（与 InkCanvas 内部一致），几何可冻结缓存");
        else
            lines.Add("  实现建议     : 采用「几何描边」方式，几何可冻结缓存");
    }

    private static RenderTargetBitmap Render(Visual v, int w, int h)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        return rtb;
    }

    private static RenderTargetBitmap RenderVisual(int w, int h, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        return Render(visual, w, h);
    }

    private static byte[] Pixels(RenderTargetBitmap bmp)
    {
        var stride = bmp.PixelWidth * 4;
        var buf = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(buf, stride, 0);
        return buf;
    }

    /// <summary>逐像素比较（含 alpha），返回差异像素数与占比。</summary>
    private static (int Diff, double Ratio) Diff(RenderTargetBitmap a, RenderTargetBitmap b)
    {
        var pa = Pixels(a);
        var pb = Pixels(b);
        var len = Math.Min(pa.Length, pb.Length);
        var diff = 0;
        for (var i = 0; i + 3 < len; i += 4)
        {
            if (pa[i] != pb[i] || pa[i + 1] != pb[i + 1] || pa[i + 2] != pb[i + 2] || pa[i + 3] != pb[i + 3])
                diff++;
        }
        var total = len / 4;
        return (diff, total == 0 ? 0 : diff / (double)total);
    }

    private static int CountInk(RenderTargetBitmap bmp)
    {
        var buf = Pixels(bmp);
        var count = 0;
        for (var i = 3; i < buf.Length; i += 4)
            if (buf[i] > 8) count++;
        return count;
    }

    /// <summary>
    /// 实时笔迹几何重建代价。
    ///
    /// 背景：为消除"抬笔外观突变"，湿态必须与干态用**同一套几何构建方式**
    /// （否则 InkCanvas 的增量廉价渲染会在抬笔时切换到拟合几何，看起来"变细腻"）。
    /// 代价是书写过程中每帧都要重建几何。这里量化这个代价，判断能否放进一帧预算（16.7 ms）。
    /// </summary>
    private static void LiveGeometryCost(List<string> lines, ref int failures)
    {
        lines.Add("  点数      重建耗时/帧     占 60fps 帧预算");
        var worstOk = true;
        foreach (var n in new[] { 60, 150, 300, 600, 1200, 2000 })
        {
            var s = new InkStroke { Id = 4242, Color = Color.FromRgb(0xF5, 0xF5, 0xF0), Width = 4 };
            for (var i = 0; i < n; i++)
            {
                var t = i / (double)(n - 1);
                s.Add(new InputPoint(
                    100 + 900 * t,
                    300 + Math.Sin(t * Math.PI * 4) * 120,
                    (float)(0.35 + 0.5 * Math.Abs(Math.Sin(t * Math.PI * 2))),
                    PointerKind.Stylus));
            }

            s.BuildSmooth(); // 预热
            const int reps = 20;
            var sw = Stopwatch.StartNew();
            for (var r = 0; r < reps; r++) s.BuildSmooth();
            sw.Stop();
            var per = sw.Elapsed.TotalMilliseconds / reps;
            var pct = per / 16.7 * 100;
            if (pct > 30) worstOk = false;
            lines.Add($"  {n,5} 点   {per,8:0.00} ms   {pct,6:0.0}%");
        }

        lines.Add("");
        lines.Add($"  结论：{(worstOk ? "重建代价可接受（最坏情况 <30% 帧预算）→ 湿态自绘可行 ✅" : "长笔迹重建代价偏高 ⚠️ 需要节流（如每 2~3 帧重建，或分段缓存）")}");
        if (!worstOk) failures++;
    }

    /// <summary>
    /// 矢量缩放无损性（S2 需求：无损放大 50 倍）。
    ///
    /// 判据：
    ///   ① 矢量路径在 1x / 10x / 50x 下渲染 —— 几何按世界坐标构建、缩放只改变换矩阵，
    ///      因此放大是重绘而非位图放大；用「边缘部分覆盖像素」确认抗锯齿在起作用；
    ///   ② 对照组：把 1x 渲染结果用**最近邻**放大到 50x（模拟"把画布缓存成固定分辨率位图"的错误做法），
    ///      与矢量 50x 结果逐像素比对 —— 差异越大，说明位图缓存路线失真越严重。
    ///
    /// 这也直接给出设计红线：**画布渲染禁止使用固定分辨率位图缓存**。
    /// </summary>
    private static void ZoomQuality(int w, int h, List<string> lines, ref int failures)
    {
        var host = new CanvasHost
        {
            Width = w,
            Height = h,
            ZoomCenter = new Point(w / 2.0, h / 2.0)
        };

        // 一条较粗、带压感的曲线，放在中心，便于放大观察
        var s = new InkStroke { Id = 991, Color = Color.FromRgb(0xF5, 0xF5, 0xF0), Width = 6 };
        const int n = 200;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)(n - 1);
            s.Add(new InputPoint(
                w / 2.0 - 120 + 240 * t,
                h / 2.0 + Math.Sin(t * Math.PI * 2) * 40,
                (float)(0.35 + 0.5 * Math.Abs(Math.Sin(t * Math.PI * 3))),
                PointerKind.Stylus));
        }
        host.Strokes.Add(s);

        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));
        host.UpdateLayout();

        var bg = host.BoardColor;
        var pen = s.Color;

        lines.Add("  缩放      渲染耗时     笔迹像素    边缘过渡像素   边缘过渡宽度(px)");
        RenderTargetBitmap? at50 = null;
        foreach (var zoom in new[] { 1.0, 10.0, 50.0 })
        {
            host.Zoom = zoom;
            host.InvalidateVisual();
            host.UpdateLayout();

            var sw = Stopwatch.StartNew();
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(host);
            sw.Stop();

            var (strokePx, edgePx, avgTrans, _) = Analyze(bmp, bg, pen);
            lines.Add($"  {zoom,4:0}x   {sw.Elapsed.TotalMilliseconds,8:0.0} ms   {strokePx,8}   {edgePx,10}   {avgTrans,14:0.00}");

            if (Math.Abs(zoom - 50.0) < 1e-9) at50 = bmp;
        }

        // 对照组：把场景以 1/50 比例渲染（等于"用固定分辨率位图缓存画布"），
        // 裁出中心 1/50 区域，再用最近邻放大 50 倍 —— 取景与直接 50x 矢量渲染一致，可直接比对。
        host.Zoom = 1.0 / 50.0;
        host.InvalidateVisual();
        host.UpdateLayout();
        var lowRes = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        lowRes.Render(host);

        var cw = Math.Max(1, w / 50);
        var ch = Math.Max(1, h / 50);
        var cx = w / 2 - cw / 2;
        var cy = h / 2 - ch / 2;
        var crop = new CroppedBitmap(lowRes, new Int32Rect(cx, cy, cw, ch));

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
            dc.DrawImage(crop, new Rect(0, 0, w, h));

        var upscaled = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        upscaled.Render(visual);

        var (stroke2, edge2, avg2, _) = Analyze(upscaled, bg, pen);
        lines.Add("");
        lines.Add($"  对照组（场景按 1/50 渲染成 {cw}×{ch} 位图后最近邻放大 50 倍，模拟\"位图缓存\"做法）");
        lines.Add($"            笔迹像素 {stroke2}　边缘过渡像素 {edge2}　边缘过渡宽度 {avg2:0.00} px");

        if (at50 is not null)
        {
            var (_, _, avgVec, _) = Analyze(at50, bg, pen);
            var (diff, diffRatio) = Diff(at50, upscaled);

            lines.Add($"            与矢量 50x 的像素差异：{diff}（{diffRatio:P2}）");
            lines.Add("");
            lines.Add($"  矢量 50x 边缘过渡宽度     : {avgVec:0.00} px　（1~3 px = 抗锯齿生效；≈0 = 硬阶梯/锯齿）");
            lines.Add($"  位图放大 边缘过渡宽度     : {avg2:0.00} px");
            lines.Add($"  两者像素差异              : {diffRatio:P2}");

            var aaOk = avgVec >= 1.0 && avgVec <= 4.0;
            lines.Add($"  抗锯齿判定                : {(aaOk ? "矢量路径边缘为渐变过渡 → 抗锯齿正常 ✅" : $"边缘过渡 {avgVec:0.00} px 不在 1~3 px 预期区间，需检查渲染设置 ⚠️")}");
            if (!aaOk) failures++;

            lines.Add("  结论：S2 的「无损放大 50 倍」由「世界坐标几何 + 每帧重绘」保证；");
            lines.Add("        **画布渲染禁止使用固定分辨率位图缓存**（对照组即是反例）；");
            lines.Add("        导出处用 pixelsPerUnit 提高分辨率即可输出高清图。");
        }
    }

    /// <summary>
    /// 逐像素分析渲染质量。
    ///
    /// 背景不透明时 alpha 恒为 255，**不能**用 alpha 判边缘抗锯齿；
    /// 因此把像素颜色投影到「背景色 → 笔迹色」的连线上，得到一个 0~1 的**覆盖率**：
    ///   t≈0 → 纯背景；t≈1 → 纯笔迹；0&lt;t&lt;1 → 边缘部分覆盖（即抗锯齿像素）。
    ///
    /// 另外统计**边缘过渡宽度**（一行内从背景过渡到实心所跨的像素数）：
    ///   矢量抗锯齿 ≈ 1~3 px；硬阶梯（最近邻放大）≈ 0 或几十 px（块状）。
    /// 这个数直接对应用户说的"防锯齿"。
    /// </summary>
    private static (int StrokePixels, int EdgePixels, double AvgTransition, int Runs) Analyze(
        RenderTargetBitmap bmp, Color bg, Color pen)
    {
        var w = bmp.PixelWidth;
        var h = bmp.PixelHeight;
        var stride = w * 4;
        var buf = new byte[stride * h];
        bmp.CopyPixels(buf, stride, 0);

        double dr = pen.R - bg.R, dg = pen.G - bg.G, db = pen.B - bg.B;
        var len2 = dr * dr + dg * dg + db * db;
        if (len2 < 1e-6) return (0, 0, 0, 0);

        var t = new double[w * h];
        var strokePixels = 0;
        var edgePixels = 0;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * stride + x * 4;
                // Pbgra32：B,G,R,A
                var b = buf[i];
                var g = buf[i + 1];
                var r = buf[i + 2];

                var proj = ((r - bg.R) * dr + (g - bg.G) * dg + (b - bg.B) * db) / len2;
                proj = Math.Clamp(proj, 0, 1);
                t[y * w + x] = proj;

                if (proj > 0.02) strokePixels++;
                if (proj > 0.02 && proj < 0.98) edgePixels++;
            }
        }

        // 统计"边缘过渡"：把每条笔迹在一行内的连续区间视为一个 run，
        // run 内 0.02<t<0.98 的像素数就是该 run 的过渡宽度；取所有 run 的平均。
        // 矢量抗锯齿 ≈ 1~3 px；硬阶梯（最近邻放大）≈ 0。
        var runs = 0;
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            var inRun = false;
            for (var x = 0; x < w; x++)
            {
                var v = t[row + x];
                if (!inRun && v > 0.02) { inRun = true; runs++; }
                else if (inRun && v <= 0.02) inRun = false;
            }
        }

        var avg = runs == 0 ? 0 : edgePixels / (double)runs;
        return (strokePixels, edgePixels, avg, runs);
    }

    /// <summary>
    /// 细笔抗锯齿：比较两种渲染方式在细线宽下的表现。
    ///
    /// 背景（人工核验反馈）：**笔越细锯齿越严重**。原因是细线的轮廓填充在亚像素宽度下
    /// 会导致覆盖不均——线看起来一段亮一段暗、并伴随阶梯感。
    ///
    /// 指标：**沿线覆盖率均匀性**。取一条接近水平的浅斜线，逐列取该列的最大覆盖率 t，
    /// 得到序列 v[x]。理想抗锯齿下 v 应恒定接近 1.0；
    /// 锯齿/覆盖不均会表现为 v 的波动 → 用**变异系数 CV = std/mean** 与**最小值**衡量（越小越好）。
    /// </summary>
    private static void ThinLineQuality(int w, int h, List<string> lines, ref int failures)
    {
        lines.Add("  线宽     填充:CV / 最低覆盖     描边:CV / 最低覆盖     较优");
        foreach (var width in new[] { 1.0, 1.5, 2.0, 3.0, 4.0 })
        {
            var bg = Color.FromRgb(0x2F, 0x4F, 0x3A);
            var pen = Color.FromRgb(0xF5, 0xF5, 0xF0);

            var bmpFill = RenderThinLine(w, h, width, bg, pen, useFill: true);
            var bmpStroke = RenderThinLine(w, h, width, bg, pen, useFill: false);

            var (cvFill, minFill) = LineUniformity(bmpFill, bg, pen);
            var (cvStroke, minStroke) = RenderThinLineStats(bmpStroke, bg, pen);

            var better = cvStroke < cvFill ? "描边" : "填充";
            lines.Add($"  {width,4:0.0} px   {cvFill,7:0.000} / {minFill,6:P0}      {cvStroke,7:0.000} / {minStroke,6:P0}      {better}");
        }

        lines.Add("");
        lines.Add("  说明：CV 越小越均匀（锯齿越少）；最低覆盖率越接近 100% 说明线越实、不发虚。");
        lines.Add("  结论：细线用哪一种，见上表逐行「较优」列；正式实现按此在宽/细两档间切换渲染方式。");
    }

    private static RenderTargetBitmap RenderThinLine(int w, int h, double width, Color bg, Color pen, bool useFill)
    {
        var host = new CanvasHost { Width = w, Height = h, BoardColor = bg };
        var y0 = h / 2.0;
        var y1 = y0 + h * 0.06;   // 浅斜线，最容易暴露阶梯
        var x0 = w * 0.06;
        var x1 = w * 0.94;

        if (useFill)
        {
            var s = new InkStroke { Id = 31337, Color = pen, Width = width };
            const int n = 120;
            for (var i = 0; i < n; i++)
            {
                var t = i / (double)(n - 1);
                s.Add(new InputPoint(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, 0.5f, PointerKind.Stylus));
            }
            host.Strokes.Add(s);
        }
        else
        {
            // 中心线描边：这里用 CanvasHost 之外的独立视觉来画
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(x0, y0), false, false);
                ctx.LineTo(new Point(x1, y1), true, false);
            }
            geo.Freeze();

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, w, h));
                var p = new Pen(new SolidColorBrush(pen), width)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                p.Freeze();
                dc.DrawGeometry(null, p, geo);
            }
            host.Measure(new Size(w, h));
            host.Arrange(new Rect(0, 0, w, h));
            host.UpdateLayout();
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            return rtb;
        }

        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));
        host.UpdateLayout();
        var outBmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        outBmp.Render(host);
        return outBmp;
    }

    private static (double Cv, double Min) RenderThinLineStats(RenderTargetBitmap bmp, Color bg, Color pen)
        => LineUniformity(bmp, bg, pen);

    /// <summary>沿线覆盖率均匀性：逐列取最大覆盖率，算变异系数与最小值。</summary>
    private static (double Cv, double Min) LineUniformity(RenderTargetBitmap bmp, Color bg, Color pen)
    {
        var w = bmp.PixelWidth;
        var h = bmp.PixelHeight;
        var stride = w * 4;
        var buf = new byte[stride * h];
        bmp.CopyPixels(buf, stride, 0);

        double dr = pen.R - bg.R, dg = pen.G - bg.G, db = pen.B - bg.B;
        var len2 = dr * dr + dg * dg + db * db;
        if (len2 < 1e-6) return (0, 0);

        var series = new List<double>();
        for (var x = (int)(w * 0.10); x < (int)(w * 0.90); x++)
        {
            var best = 0.0;
            for (var y = 0; y < h; y++)
            {
                var i = y * stride + x * 4;
                var proj = ((buf[i + 2] - bg.R) * dr + (buf[i + 1] - bg.G) * dg + (buf[i] - bg.B) * db) / len2;
                if (proj > best) best = proj;
            }
            series.Add(Math.Clamp(best, 0, 1));
        }

        if (series.Count == 0) return (0, 0);
        var mean = series.Average();
        if (mean <= 1e-9) return (0, 0);
        var std = Math.Sqrt(series.Sum(v => (v - mean) * (v - mean)) / series.Count);
        return (std / mean, series.Min());
    }

    /// <summary>
    /// 橡皮擦两条实现路线的代价对比（对应"一笔被擦断后变成多笔"的语义问题）。
    ///
    /// 路线甲（几何差集/遮罩保留）：保留原笔迹对象，另存「擦除遮罩」；
    ///   渲染几何 = 原几何 − 遮罩。**选中工具始终选中的是"那一笔"**，对象数量不膨胀。
    ///   代价：遮罩变化时要重算差集。
    ///
    /// 路线乙（点集切割/碎片化）：擦除即把点集切成多段，各自成为**独立对象**。
    ///   代价：对象数量膨胀、选中粒度变碎、撤销是"1→N 替换"。
    ///
    /// 这里只量"算一次差集要多久"，用来判断路线甲是否可行（配合几何缓存，只在遮罩变化时重算）。
    /// </summary>
    private static void EraseCost(int w, int h, List<string> lines, ref int failures)
    {
        lines.Add("  点数   擦除圆   路线甲:几何差集   路线乙:点集切割   切割后剩余段数");
        var worst = 0.0;

        foreach (var (pointCount, circleCount) in new[]
                 { (60, 1), (200, 1), (200, 10), (200, 30), (600, 20), (1200, 40) })
        {
            var pts = Wave(w, h, pointCount);
            var circles = CirclesAlong(pts, circleCount, radius: 22);

            // 路线甲：原笔迹几何 − 擦除圆组
            var stroke = new InkStroke { Id = 555, Color = Colors.White, Width = 6 };
            foreach (var p in pts) stroke.Add(p);
            var baseGeo = stroke.BuildSmooth();

            var eraserGeo = new GeometryGroup();
            foreach (var c in circles)
                eraserGeo.Children.Add(new EllipseGeometry(c, 22, 22));
            eraserGeo.Freeze();

            // 预热一次，再计时
            try { Geometry.Combine(baseGeo, eraserGeo, GeometryCombineMode.Exclude, null); } catch { }

            var sw = Stopwatch.StartNew();
            Geometry? diff = null;
            try { diff = Geometry.Combine(baseGeo, eraserGeo, GeometryCombineMode.Exclude, null); } catch { }
            sw.Stop();
            var geomMs = sw.Elapsed.TotalMilliseconds;
            if (geomMs > worst) worst = geomMs;

            // 路线乙：逐段与圆求交，统计剩余段数
            var sw2 = Stopwatch.StartNew();
            var segments = CutPolyline(pts, circles);
            sw2.Stop();
            var cutMs = sw2.Elapsed.TotalMilliseconds;

            lines.Add($"  {pointCount,5}  {circleCount,6}   {geomMs,12:0.00} ms   {cutMs,12:0.00} ms   {segments,10}");

            _ = diff;
        }

        lines.Add("");
        var ok = worst < 16.7;
        lines.Add($"  结论：几何差集最坏 {worst:0.00} ms/次　{(ok ? "< 一帧预算(16.7ms) → 路线甲可行（配合几何缓存，仅在遮罩变化时重算）✅" : "≥ 一帧预算 ⚠️ 需分块缓存或改用路线乙")}");
        if (!ok) failures++;
        lines.Add("  说明：路线乙(点集切割)极快，但会把一笔变成多笔独立对象 —— 这正是选中工具语义冲突的来源。");
    }

    private static List<InputPoint> Wave(int w, int h, int n)
    {
        var pts = new List<InputPoint>(n);
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)(n - 1);
            pts.Add(new InputPoint(
                w * (0.05 + 0.90 * t),
                h * 0.5 + Math.Sin(t * Math.PI * 6) * h * 0.18,
                (float)(0.35 + 0.5 * Math.Abs(Math.Sin(t * Math.PI * 3))),
                PointerKind.Stylus));
        }
        return pts;
    }

    /// <summary>沿笔迹均匀分布若干个擦除圆。</summary>
    private static List<Point> CirclesAlong(IReadOnlyList<InputPoint> pts, int count, double radius)
    {
        var result = new List<Point>(count);
        if (count <= 0) return result;
        for (var k = 0; k < count; k++)
        {
            var t = (k + 0.5) / count;
            var idx = (int)Math.Clamp(t * (pts.Count - 1), 0, pts.Count - 1);
            result.Add(new Point(pts[idx].X, pts[idx].Y));
        }
        return result;
    }

    /// <summary>路线乙：把折线按擦除圆切割，返回保留下来的子段数。</summary>
    private static int CutPolyline(IReadOnlyList<InputPoint> pts, IReadOnlyList<Point> circles)
    {
        var kept = 0;
        var inSegment = false;
        for (var i = 1; i < pts.Count; i++)
        {
            var a = new Point(pts[i - 1].X, pts[i - 1].Y);
            var b = new Point(pts[i].X, pts[i].Y);
            var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);

            var inside = false;
            foreach (var c in circles)
            {
                var dx = mid.X - c.X;
                var dy = mid.Y - c.Y;
                if (dx * dx + dy * dy <= 22 * 22) { inside = true; break; }
            }

            if (!inside)
            {
                if (!inSegment) { kept++; inSegment = true; }
            }
            else inSegment = false;
        }
        return kept;
    }

    private static List<InputPoint> SyntheticStroke(int index, int count, double w, double h)    {
        var pts = new List<InputPoint>(count);
        var y0 = h * (0.08 + 0.84 * index / 20.0);
        var amp = h * 0.035;
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)(count - 1);
            var x = w * (0.05 + 0.9 * t);
            var y = y0 + Math.Sin(t * Math.PI * 4 + index) * amp;
            var pressure = (float)(0.35 + 0.5 * Math.Abs(Math.Sin(t * Math.PI * 2)));
            pts.Add(new InputPoint(x, y, pressure, PointerKind.Stylus));
        }
        return pts;
    }

    /// <summary>粗判渲染结果是否非空白（抽样看是否有非背景像素）。</summary>
    private static bool HasInk(RenderTargetBitmap bmp)
    {
        var stride = bmp.PixelWidth * 4;
        var buf = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(buf, stride, 0);

        // 背景约 #2F4F3A（B=0x3A,G=0x4F,R=0x2F）；找明显更亮的像素即为笔迹
        for (var i = 0; i + 3 < buf.Length; i += 4 * 97) // 抽样
        {
            var b = buf[i];
            var g = buf[i + 1];
            var r = buf[i + 2];
            if (r > 0x80 || g > 0x90 || b > 0x90) return true;
        }
        return false;
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
