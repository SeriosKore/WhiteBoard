using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Input;
using WhiteBoard.Core.Model;
using WhiteBoard.Core.Text;
using WhiteBoard.Rendering;
using WhiteBoard.Rendering.Tools;

namespace WhiteBoard.App.Diagnostics;

/// <summary>
/// 示例画板 + 离屏渲染自检。
///
/// 用法：<c>WhiteBoard.exe --render-smoke --out &lt;png&gt; [--report &lt;txt&gt;]</c>
/// 退出码：0 = 通过。
///
/// 为什么要有它：<c>--self-test</c> 只能证明"环境/编码/主题没问题"，
/// 证明不了"笔真的能画出来"。这里用**生产代码链路**（ToolDispatcher → 工具 → 命令 → PageRenderer）
/// 合成一次真实的绘制过程，再渲染成 PNG 并逐区域数像素——
/// 于是"画得出来、画在对的位置、擦除真的生效"变成可自动判定的一条命令。
/// </summary>
public static class BoardSmoke
{
    public static bool IsRequested(string[] args)
        => args.Any(a => a.Equals("--render-smoke", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        SelfTest.TryAttachParentConsole();

        var outPath = GetArgValue(args, "--out");
        var reportPath = GetArgValue(args, "--report");

        var lines = new List<string>();
        var failures = 0;

        void Check(string name, Func<(bool ok, string detail)> probe)
        {
            try
            {
                var (ok, detail) = probe();
                lines.Add($"{(ok ? "PASS" : "FAIL")}\t{name}\t{detail}");
                if (!ok) failures++;
            }
            catch (Exception ex)
            {
                lines.Add($"FAIL\t{name}\t异常：{ex.GetType().Name}: {ex.Message}");
                failures++;
            }
        }

        const int W = 1280;
        const int H = 800;

        var build = DemoBoard.Build(W, H);

        lines.Add($"WhiteBoard 渲染自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        lines.Add($"画布：{W}×{H}　页面数：{build.Document.Pages.Count}");
        lines.Add("");

        Check("绘制链路产出对象", () =>
        {
            var page = build.Document.Pages[0];
            return (page.Count >= 9,
                $"第 1 页对象数 {page.Count}（笔迹 {build.FreehandCount}、形状 {build.ShapeCount}、文本 {build.TextCount}）");
        });

        Check("撤销栈可用（每页独立）", () =>
        {
            var ok = build.Commands.CanUndo;
            return (ok, ok ? $"可撤销项 {build.Commands.Current.UndoCount} 条" : "撤销栈为空");
        });

        Check("擦除遮罩已生效（对象未被切碎）", () =>
        {
            var page = build.Document.Pages[0];
            var masked = page.Objects.Where(o => o.Erasures.Count > 0).ToList();
            var expected = build.FreehandCount + build.ShapeCount + build.TextCount;
            return (masked.Count == 1 && page.Count == expected,
                $"带遮罩对象 {masked.Count} 个，总对象 {page.Count} 个（切碎会变成十几个）");
        });

        // 渲染
        var visual = new DrawingVisual();
        FrameStats stats;
        using (var dc = visual.RenderOpen())
            stats = build.Renderer.Render(dc, build.Document.Pages[0], build.Document.Pages[0].Viewport, W, H);

        var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);

        Check("渲染统计", () => (stats.DrawnObjects >= 9, stats.ToString()));

        // 文本：既要画出来，也要落在文字该在的地方
        Check("文本已渲染且位置正确", () =>
        {
            // 标题在 (90,10) 起、大字号 → 屏幕 y 10..90、x 90..700
            var band = CountNonBackground(bmp, 95, 15, 690, 88);
            var above = CountNonBackground(bmp, 95, 0, 690, 8);
            return (band > 300 && above == 0,
                $"标题带内像素 {band}（应 >300），其上沿之外 {above}（应为 0）");
        });

        // 逐区域数像素：既证明"画出来了"，也证明"没画错地方"
        Check("笔迹区有像素", () =>
        {
            var n = CountNonBackground(bmp, 60, 90, W - 60, 270);
            return (n > 2000, $"笔迹带非背景像素 {n}");
        });

        Check("矩形/椭圆/直线区有像素", () =>
        {
            var n = CountNonBackground(bmp, 60, 300, W - 60, 580);
            return (n > 2000, $"形状带非背景像素 {n}");
        });

        Check("擦除处缺像素（遮罩真的挖掉了内容）", () =>
        {
            var mask = build.ErasedRegionScreen;
            var before = CountNonBackground(bmp, (int)mask.X - 60, (int)mask.Y - 20,
                (int)mask.X, (int)mask.Bottom + 20);
            var inside = CountNonBackground(bmp, (int)mask.X + 4, (int)mask.Y - 20,
                (int)mask.Right - 4, (int)mask.Bottom + 20);
            return (inside == 0 && before > 100,
                $"擦除区内部像素 {inside}（应为 0），其左侧同样高度处像素 {before}（应 >0）");
        });

        Check("空白区完全干净", () =>
        {
            var n = CountNonBackground(bmp, 60, 660, W - 60, H - 20);
            return (n == 0, $"底部空白带非背景像素 {n}");
        });

        // ── 形状语义：不只看"有像素"，还要看"像素在对的位置" ───────────────
        Check("椭圆真的是椭圆（不是矩形）", () =>
        {
            // 椭圆外接框 (470,330)-(800,560)：中心 (635,445)，rx=165，ry=115
            var center = IsBackground(bmp, 635, 445);
            var top = !IsBackground(bmp, 635, 331);
            // 外接框的角上必须是空白——如果 ellipse 被画成矩形，这里一定有像素
            var cornerTL = IsBackground(bmp, 480, 340);
            var cornerBR = IsBackground(bmp, 790, 550);
            return (center && top && cornerTL && cornerBR,
                $"中心空白={center}　上弧有像素={top}　左上角空白={cornerTL}　右下角空白={cornerBR}");
        });

        Check("矩形是空心的（内部无像素）", () =>
        {
            var inside = IsBackground(bmp, 255, 445);
            var edge = !IsBackground(bmp, 255, 331);
            return (inside && edge, $"内部空白={inside}　上边有像素={edge}");
        });

        Check("直线只在对角线上", () =>
        {
            // (850,330)-(1190,560)：中点 (1020,445)，斜率 230/340≈0.676
            var onLine = !IsBackground(bmp, 1020, 445);
            // 取远离直线的采样点：x=900 时线心 y≈364、x=1150 时线心 y≈533
            var aboveLine = IsBackground(bmp, 900, 330);
            var belowLine = IsBackground(bmp, 1150, 590);
            return (onLine && aboveLine && belowLine,
                $"线上有像素={onLine}　上方空白={aboveLine}　下方空白={belowLine}");
        });

        Check("每个对象的颜色都正确落地", () =>
        {
            var counts = DemoBoard.Palette
                .Select(c => (Hex: c, Count: CountNearColor(bmp, c)))
                .ToList();
            var all = counts.All(x => x.Count > 200);
            return (all, string.Join("　", counts.Select(x => $"{x.Hex}:{x.Count}")));
        });

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            Check("写出 PNG", () =>
            {
                var full = Path.GetFullPath(outPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = File.Create(full);
                encoder.Save(fs);
                var size = new FileInfo(full).Length;
                return (size > 1024, $"{full}（{size / 1024} KB）");
            });
        }

        lines.Add("");
        lines.Add($"结果：{(failures == 0 ? "全部通过" : $"{failures} 项失败")}");

        var text = string.Join(Environment.NewLine, lines);
        Console.WriteLine(text);
        Debug.WriteLine(text);

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            try { TextFileCodec.WriteAllText(Path.GetFullPath(reportPath), text); }
            catch (Exception ex) { Console.Error.WriteLine($"写报告失败：{ex.Message}"); }
        }

        return failures == 0 ? 0 : 1;
    }

    private static int CountNonBackground(RenderTargetBitmap bmp, int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        x1 = Math.Min(bmp.PixelWidth, x1);
        y1 = Math.Min(bmp.PixelHeight, y1);

        var stride = bmp.PixelWidth * 4;
        var buf = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(buf, stride, 0);

        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = y * stride + x * 4;
                var isBg = Math.Abs(buf[i] - 0x3A) <= 12
                           && Math.Abs(buf[i + 1] - 0x4F) <= 12
                           && Math.Abs(buf[i + 2] - 0x2F) <= 12;
                if (!isBg) count++;
            }
        }
        return count;
    }

    private static bool IsBackground(RenderTargetBitmap bmp, int x, int y)
    {
        var buf = new byte[4];
        bmp.CopyPixels(new Int32Rect(x, y, 1, 1), buf, 4, 0);
        // Pbgra32：B,G,R,A；背景 #2F4F3A → B=0x3A G=0x4F R=0x2F
        return Math.Abs(buf[0] - 0x3A) <= 12
               && Math.Abs(buf[1] - 0x4F) <= 12
               && Math.Abs(buf[2] - 0x2F) <= 12;
    }

    /// <summary>统计接近某个颜色的像素数（容差 24，用于"颜色是否落地"的粗判定）。</summary>
    private static int CountNearColor(RenderTargetBitmap bmp, string hex)
    {
        var (_, r, g, b) = Core.Theme.ColorMath.ParseHex(hex);

        var stride = bmp.PixelWidth * 4;
        var buf = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(buf, stride, 0);

        var count = 0;
        for (var i = 0; i + 3 < buf.Length; i += 4)
        {
            // Pbgra32：B,G,R,A
            if (Math.Abs(buf[i] - b) <= 24 && Math.Abs(buf[i + 1] - g) <= 24 && Math.Abs(buf[i + 2] - r) <= 24)
                count++;
        }
        return count;
    }

    private static string? GetArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}

/// <summary>一次示例画板的构建结果。</summary>
public sealed class DemoBoardResult
{
    public required WhiteboardDocument Document { get; init; }
    public required SmoothGeometryBuilder Geometry { get; init; }
    public required PageRenderer Renderer { get; init; }
    public required CommandManager Commands { get; init; }
    public required int FreehandCount { get; init; }
    public required int ShapeCount { get; init; }

    /// <summary>示例板里的文本对象数。</summary>
    public required int TextCount { get; init; }

    /// <summary>被擦除区间的屏幕矩形（自检用）。</summary>
    public required RectD ErasedRegionScreen { get; init; }
}

/// <summary>
/// 示例画板：**用生产链路合成**（合成 PointerSample → ToolDispatcher → 工具 → 命令），
/// 而不是直接 new 对象塞进页面——这样"示例能画出来"等价于"真实输入能画出来"。
/// </summary>
public static class DemoBoard
{
    private static long _ticks;

    /// <summary>示例板用到的颜色（自检按颜色数像素时会复用）。</summary>
    public static readonly string[] Palette =
    [
        "#F5F5F0", // 白
        "#EED858", // 黄
        "#F47A6E", // 红
        "#9CDCFE"  // 蓝
    ];

    public static DemoBoardResult Build(int width, int height)
    {
        _ticks = Stopwatch.GetTimestamp();

        var doc = new WhiteboardDocument { Id = 1 };
        var page = doc.EnsureAtLeastOnePage();
        var geometry = new SmoothGeometryBuilder();
        var renderer = new PageRenderer(geometry);
        var commands = new CommandManager();
        commands.SetCurrentPage(page.Id);

        var dispatcher = new ToolDispatcher();

        ToolContext Ctx() => new()
        {
            Page = page,
            Viewport = page.Viewport,
            Commands = commands,
            Geometry = geometry,
            AllocateObjectId = () => doc.AllocateObjectId(),
            PenColor = Palette[0],
            PenWidth = 6
        };

        var freehand = 0;
        var shapes = 0;

        // ── 三条手写波浪（细/中/粗，不同颜色）────────────────────────────
        var widths = new[] { 3.0, 6.0, 10.0 };
        for (var row = 0; row < 3; row++)
        {
            var pen = new PenTool();
            var ctx = Ctx();
            ctx.PenColor = Palette[row];
            ctx.PenWidth = widths[row];

            var pts = new List<PointD>();
            for (var x = 90.0; x <= width - 90; x += 6)
            {
                var y = 120 + row * 55 + Math.Sin((x - 90) / 46.0) * 22;
                pts.Add(new PointD(x, y));
            }

            Feed(dispatcher, pen, ctx, pts);
            freehand++;
        }

        // ── 矩形 / 椭圆 / 直线 ────────────────────────────────────────────
        var rect = new RectTool();
        var rctx = Ctx();
        rctx.PenColor = Palette[1];
        rctx.PenWidth = 6;
        Feed(dispatcher, rect, rctx, [new PointD(90, 330), new PointD(420, 560)]);
        shapes++;

        var ellipse = new EllipseTool();
        var ectx = Ctx();
        ectx.PenColor = Palette[3];
        ectx.PenWidth = 6;
        Feed(dispatcher, ellipse, ectx, [new PointD(470, 330), new PointD(800, 560)]);
        shapes++;

        var line = new LineTool();
        var lctx = Ctx();
        lctx.PenColor = Palette[2];
        lctx.PenWidth = 10;
        Feed(dispatcher, line, lctx, [new PointD(850, 330), new PointD(1190, 560)]);
        shapes++;

        // ── 一条长笔迹，再用橡皮擦掉中间一段（演示 ADR-18 遮罩擦除）──────
        var bar = new PenTool();
        var bctx = Ctx();
        bctx.PenColor = Palette[0];
        bctx.PenWidth = 10;

        var barPts = new List<PointD>();
        for (var x = 90.0; x <= width - 90; x += 6) barPts.Add(new PointD(x, 640));
        Feed(dispatcher, bar, bctx, barPts);
        freehand++;

        var eraseFrom = 520.0;
        var eraseTo = 760.0;

        var eraser = new EraserTool(geometry) { DiameterPx = 46 };
        dispatcher.SetTool(null, eraser, Ctx());
        var erctx = Ctx();

        var erasePts = new List<PointD>();
        for (var x = eraseFrom; x <= eraseTo; x += 8) erasePts.Add(new PointD(x, 640));
        Feed(dispatcher, eraser, erctx, erasePts);

        // ── 两个文本对象（S1 文本工具：静态文本、不旋转）──────────────────
        // 放在最上面那条笔迹之上（y<90），这样不会落进自检用的"空白区"与"笔迹带"检查区域
        var textCount = 0;
        foreach (var (content, at, size, color) in new (string, PointD, double, string)[]
                 {
                     ("白板 S1 · 示例画板", new PointD(90, 10), TextObject.LargeFontSize, Palette[1]),
                     ("用工具条上的「文本」按钮写字", new PointD(700, 34), TextObject.SmallFontSize, Palette[3])
                 })
        {
            var (w, h) = TextGeometry.Measure(content, size, TextObject.DefaultFontFamily);
            var textObject = TextObject.FromWorldTopLeft(
                doc.AllocateObjectId(), content, at, w, h, size, color);
            page.Add(textObject);
            textCount++;
        }

        // ── 第 2 页：复制（演示多页与每页独立视口/撤销栈）─────────────────
        doc.DuplicatePage(page);
        doc.CurrentPageIndex = 0;
        commands.SetCurrentPage(page.Id);

        return new DemoBoardResult
        {
            Document = doc,
            Geometry = geometry,
            Renderer = renderer,
            Commands = commands,
            FreehandCount = freehand,
            ShapeCount = shapes,
            TextCount = textCount,
            ErasedRegionScreen = new RectD(eraseFrom - 23, 640 - 23, (eraseTo - eraseFrom) + 46, 46)
        };
    }

    /// <summary>把一串世界坐标点当作一次完整的落笔→走笔→抬笔喂给工具（经路由）。</summary>
    private static void Feed(ToolDispatcher dispatcher, ITool tool, ToolContext ctx, IReadOnlyList<PointD> worldPoints)
    {
        var samples = new List<PointerSample>(worldPoints.Count);
        for (var i = 0; i < worldPoints.Count; i++)
        {
            var screen = ctx.WorldToScreen(worldPoints[i]);
            _ticks += Stopwatch.Frequency / 100; // 每点 10 ms，模拟真实采样间隔
            samples.Add(new PointerSample(0, PointerKind.Mouse, screen.X, screen.Y, 0.5f, 0, _ticks));
        }

        ToolDispatcher.Replay(dispatcher, tool, ctx, samples);
    }
}
