using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Input;
using WhiteBoard.Core.Model;
using WhiteBoard.Core.Tests;
using WhiteBoard.Rendering;
using WhiteBoard.Rendering.Tools;

namespace WhiteBoard.Rendering.Tests;

/// <summary>
/// 文本工具与文本渲染：
/// 几何构建、能点中、**橡皮不碰文本**、点击请求就地编辑（新建 / 改已有）、切工具时提交。
/// </summary>
public static class TextToolTests
{
    private const string Pen = "#F5F5F0";

    /// <summary>假的编辑宿主：只记录"被要求编辑了什么"，不做真 UI。</summary>
    private sealed class FakeTextHost : ITextEditHost
    {
        public bool IsEditing { get; private set; }
        public int BeginCount { get; private set; }
        public int CommitCount { get; private set; }
        public int CancelCount { get; private set; }
        public PointD? LastPoint { get; private set; }
        public TextObject? LastExisting { get; private set; }

        public void BeginTextEdit(PointD worldPoint, TextObject? existing)
        {
            IsEditing = true;
            BeginCount++;
            LastPoint = worldPoint;
            LastExisting = existing;
        }

        public void CommitTextEdit()
        {
            IsEditing = false;
            CommitCount++;
        }

        public void CancelTextEdit()
        {
            IsEditing = false;
            CancelCount++;
        }
    }

    private sealed class Harness
    {
        public readonly WhiteboardDocument Doc = new() { Id = 1 };
        public readonly SmoothGeometryBuilder Geometry = new();
        public readonly CommandManager Commands = new();
        public readonly ToolDispatcher Dispatcher = new();
        public readonly SelectionState Selection = new();
        public FakeTextHost Host { get; } = new();

        public Harness()
        {
            Doc.EnsureAtLeastOnePage();
            Commands.SetCurrentPage(Doc.CurrentPage.Id);
        }

        public Page Page => Doc.CurrentPage;

        public ToolContext Ctx(bool withHost = true) => new()
        {
            Page = Page,
            Viewport = Page.Viewport,
            Commands = Commands,
            Geometry = Geometry,
            AllocateObjectId = () => Doc.AllocateObjectId(),
            PenColor = Pen,
            PenWidth = 6,
            Selection = Selection,
            TextEditor = withHost ? Host : null
        };

        /// <summary>放一个文本对象（尺寸用渲染层的测量结果，和真实链路一致）。</summary>
        public TextObject AddText(string text, double x = 100, double y = 200, double size = 72)
        {
            var (w, h) = TextGeometry.Measure(text, size);
            var t = TextObject.FromWorldTopLeft(Doc.AllocateObjectId(), text, new PointD(x, y), w, h, size, Pen);
            Page.Add(t);
            return t;
        }
    }

    private static long _ticks = 9_000_000;

    private static PointerSample At(double x, double y)
        => new(0, PointerKind.Mouse, x, y, 0.5f, 0, _ticks += 8 * TimeSpan.TicksPerMillisecond / 10);

    // ── 测量与几何 ────────────────────────────────────────────────────────

    public static void Test_Text_MeasureScalesWithFontSizeAndLength()
    {
        var (w1, h1) = TextGeometry.Measure("中", 36);
        var (w2, h2) = TextGeometry.Measure("中", 72);
        var (w3, _) = TextGeometry.Measure("中中中", 36);

        Check.True(w2 > w1 * 1.6, $"字号翻倍宽度应接近翻倍（{w1:0.#} → {w2:0.#}）");
        Check.True(h2 > h1 * 1.6, $"字号翻倍高度应接近翻倍（{h1:0.#} → {h2:0.#}）");
        Check.True(w3 > w1 * 2.5, $"三个字应比一个字宽得多（{w1:0.#} → {w3:0.#}）");
        Check.True(h1 > 10, "字号 36 的高度应大于 10 世界单位");
    }

    public static void Test_Text_GeometryIsBuiltAndFitsMeasuredBox()
    {
        var h = new Harness();
        var t = h.AddText("测试 Ag", size: 72);
        var geo = h.Geometry.GetLocalGeometry(t);

        Check.True(geo.GetArea() > 0, "文本几何应有面积（不是空的）");

        var b = geo.Bounds;
        Check.True(b.Width <= t.LocalWidth + 1, $"几何宽度不应超出测量宽度（{b.Width:0.#} vs {t.LocalWidth:0.#}）");
        Check.True(b.Height <= t.LocalHeight + 1, $"几何高度不应超出测量高度（{b.Height:0.#} vs {t.LocalHeight:0.#}）");
    }

    public static void Test_Text_MultiLineGeometryIsTaller()
    {
        var h = new Harness();
        var one = h.AddText("一行", x: 0, y: 0, size: 48);
        var three = h.AddText("一行\n二行\n三行", x: 0, y: 400, size: 48);

        var h1 = h.Geometry.GetLocalGeometry(one).Bounds.Height;
        var h3 = h.Geometry.GetLocalGeometry(three).Bounds.Height;

        Check.True(h3 > h1 * 2.2, $"三行文本应明显更高（{h1:0.#} → {h3:0.#}）");
    }

    // ── 命中测试与擦除 ────────────────────────────────────────────────────

    /// <summary>文字要能点中（点选一个文本对象）。字形之间有空白，所以用"命中一片格子里的任意一点"来判。</summary>
    public static void Test_Text_IsClickable()
    {
        var h = new Harness();
        var t = h.AddText("选中我", size: 96);
        var hits = new HitTester(h.Geometry);

        var found = false;
        for (var i = 1; i <= 6 && !found; i++)
            for (var j = 1; j <= 6 && !found; j++)
            {
                var p = new PointD(t.X + t.LocalWidth * i / 7.0, t.Y + t.LocalHeight * j / 7.0);
                if (hits.HitTestTopmost(h.Page, p) is TextObject) found = true;
            }

        Check.True(found, "文本框内应至少有一个位置能点中文字");
        Check.True(hits.HitTestRect(h.Page, new RectD(t.X, t.Y, t.LocalWidth, t.LocalHeight)).Any(),
            "框选应能选中文本");
    }

    /// <summary>基线要求：**橡皮擦跳过文本**（文字不受擦除影响，要删请用选择工具 + Del）。</summary>
    public static void Test_Text_EraserDoesNotTouchIt()
    {
        var h = new Harness();
        var t = h.AddText("擦不掉的文字", size: 72);
        var areaBefore = h.Geometry.GetLocalGeometry(t).GetArea();

        var eraser = new EraserTool(h.Geometry) { DiameterPx = 200 };
        var ctx = h.Ctx();

        // 在文字中间来回擦
        h.Dispatcher.Dispatch(PointerAction.Down, At(t.X + 20, t.Y + 20), eraser, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, At(t.X + 120, t.Y + 40), eraser, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, At(t.X + 220, t.Y + 20), eraser, ctx, false);

        Check.Equal(0, t.Erasures.Count, "文本不应被追加擦除遮罩");
        Check.Near(areaBefore, h.Geometry.GetLocalGeometry(t).GetArea(), 0.5, "文本内容不应变化");
        Check.Equal(1, h.Page.Count, "文本对象不应被删除");
    }

    /// <summary>但铅笔/形状仍然照常可擦——确认"跳过文本"没有被误实现成"什么都擦不掉"。</summary>
    public static void Test_Text_EraserStillWorksOnFreehand()
    {
        var h = new Harness();
        var stroke = FreehandObject.FromWorldPoints(h.Doc.AllocateObjectId(),
            [new InkPoint(0, 500, 0.5f), new InkPoint(400, 500, 0.5f)], Pen, 20);
        h.Page.Add(stroke);
        var areaBefore = h.Geometry.GetLocalGeometry(stroke).GetArea();

        var eraser = new EraserTool(h.Geometry) { DiameterPx = 80 };
        var ctx = h.Ctx();
        h.Dispatcher.Dispatch(PointerAction.Down, At(100, 500), eraser, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, At(300, 500), eraser, ctx, false);

        Check.True(stroke.Erasures.Count > 0, "笔迹应被正常擦除");
        Check.True(h.Geometry.GetLocalGeometry(stroke).GetArea() < areaBefore, "笔迹面积应减少");
    }

    // ── 文本工具与编辑宿主 ────────────────────────────────────────────────

    public static void Test_TextTool_ClickOnEmptyRequestsNewEditAtThatPoint()
    {
        var h = new Harness();
        var tool = new TextTool(h.Geometry);

        h.Dispatcher.Dispatch(PointerAction.Down, At(321, 456), tool, h.Ctx(), false);

        Check.Equal(1, h.Host.BeginCount, "应请求开始编辑");
        Check.True(h.Host.LastExisting is null, "空白处点击应是新建");
        Check.True(h.Host.LastPoint is not null, "应记录点击位置");
        Check.Near(321, h.Host.LastPoint!.Value.X, 1e-6, "点击位置的世界坐标 X 应为文本框左上角");
        Check.Near(456, h.Host.LastPoint!.Value.Y, 1e-6, "点击位置的世界坐标 Y 应为文本框左上角");
    }

    public static void Test_TextTool_ClickOnExistingTextEditsIt()
    {
        var h = new Harness();
        var t = h.AddText("改我", size: 96);
        var tool = new TextTool(h.Geometry);

        // 找一个能点中文字的位置
        var target = new PointD(t.X + t.LocalWidth / 2, t.Y + t.LocalHeight / 2);
        h.Dispatcher.Dispatch(PointerAction.Down, At(target.X, target.Y), tool, h.Ctx(), false);

        Check.Equal(1, h.Host.BeginCount, "应请求开始编辑");
        Check.True(ReferenceEquals(h.Host.LastExisting, t),
            "点在已有文字上应编辑**那一段**，而不是在旁边叠一段新的");
    }

    public static void Test_TextTool_DeactivateCommitsPendingEdit()
    {
        var h = new Harness();
        var tool = new TextTool(h.Geometry);
        var ctx = h.Ctx();

        h.Dispatcher.Dispatch(PointerAction.Down, At(100, 100), tool, ctx, false);
        Check.True(h.Host.IsEditing, "应在编辑中");

        tool.OnDeactivated(ctx);
        Check.Equal(1, h.Host.CommitCount, "切走工具应提交编辑（避免内容不落库）");
        Check.True(!h.Host.IsEditing, "提交后不再处于编辑状态");
    }

    /// <summary>没有编辑宿主（例如离屏环境）时工具应安静地不做事，而不是抛异常。</summary>
    public static void Test_TextTool_WithoutHostDoesNothing()
    {
        var h = new Harness();
        var tool = new TextTool(h.Geometry);

        var result = h.Dispatcher.Dispatch(PointerAction.Down, At(100, 100), tool, h.Ctx(withHost: false), false);

        Check.Equal(0, h.Host.BeginCount, "没有宿主就不应请求编辑");
        Check.True(!result.Handled, "没有宿主时应报告未处理");
    }

    // ── 出像素 ────────────────────────────────────────────────────────────

    public static void Test_Text_RendersVisiblePixelsInsideItsBox()
    {
        var h = new Harness();
        var t = h.AddText("文字 Pixel", x: 100, y: 100, size: 96);
        var renderer = new PageRenderer(h.Geometry) { BackgroundColor = Color.FromRgb(0x2F, 0x4F, 0x3A) };

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            renderer.Render(dc, h.Page, new ViewportState(), 1280, 800);

        var bmp = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);

        var stride = 1280 * 4;
        var px = new byte[stride * 800];
        bmp.CopyPixels(px, stride, 0);

        int CountIn(double x0, double y0, double x1, double y1)
        {
            var n = 0;
            for (var y = Math.Max(0, (int)y0); y < Math.Min(800, (int)y1); y++)
                for (var x = Math.Max(0, (int)x0); x < Math.Min(1280, (int)x1); x++)
                {
                    var i = y * stride + x * 4;
                    if (Math.Abs(px[i] - 0x3A) > 14 || Math.Abs(px[i + 1] - 0x4F) > 14 || Math.Abs(px[i + 2] - 0x2F) > 14)
                        n++;
                }
            return n;
        }

        var inBox = CountIn(t.X, t.Y, t.X + t.LocalWidth, t.Y + t.LocalHeight);
        var farAway = CountIn(900, 600, 1200, 780);

        Check.True(inBox > 500, $"文本框内应有文字像素，实际 {inBox}");
        Check.Equal(0, farAway, "远处不应有内容");
    }

    /// <summary>文本也要能跟着视口缩放（世界单位字号的意义）。</summary>
    public static void Test_Text_ScalesWithZoom()
    {
        var h = new Harness();
        h.AddText("缩放", x: 100, y: 100, size: 60);
        var renderer = new PageRenderer(h.Geometry);

        int PixelsAtZoom(double zoom)
        {
            var vp = new ViewportState { Zoom = zoom, PanX = 0, PanY = 0 };
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) renderer.Render(dc, h.Page, vp, 800, 600);

            var bmp = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);

            var stride = 800 * 4;
            var px = new byte[stride * 600];
            bmp.CopyPixels(px, stride, 0);

            var n = 0;
            for (var i = 0; i + 3 < px.Length; i += 4)
                if (Math.Abs(px[i] - 0x3A) > 14 || Math.Abs(px[i + 1] - 0x4F) > 14 || Math.Abs(px[i + 2] - 0x2F) > 14)
                    n++;
            return n;
        }

        var at1 = PixelsAtZoom(1);
        var at3 = PixelsAtZoom(3);

        Check.True(at1 > 100, $"1 倍下应能看到文字，实际 {at1}");
        Check.True(at3 > at1 * 3, $"放大 3 倍后文字像素应显著增多（{at1} → {at3}）");
    }

    // ── 与选择工具/命令的配合 ─────────────────────────────────────────────

    public static void Test_Text_CanBeSelectedMovedAndDeleted()
    {
        var h = new Harness();
        var t = h.AddText("搬我", size: 96);
        var x0 = t.X;
        var y0 = t.Y;

        // 框选（用文本框范围，稳定命中）
        var select = new SelectTool(h.Geometry);
        var ctx = h.Ctx();
        var a = new PointD(t.X - 20, t.Y - 20);
        var b = new PointD(t.X + t.LocalWidth + 20, t.Y + t.LocalHeight + 20);

        h.Dispatcher.Dispatch(PointerAction.Down, At(a.X, a.Y), select, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, At((a.X + b.X) / 2, (a.Y + b.Y) / 2), select, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, At(b.X, b.Y), select, ctx, false);

        Check.True(h.Selection.Contains(t), "文本应被框选选中");

        // 抓住文字中心拖动
        var grab = new PointD(t.X + t.LocalWidth / 2, t.Y + t.LocalHeight / 2);
        h.Dispatcher.Dispatch(PointerAction.Down, At(grab.X, grab.Y), select, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, At(grab.X + 60, grab.Y + 40), select, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, At(grab.X + 100, grab.Y + 80), select, ctx, false);

        Check.True(Math.Abs(t.X - (x0 + 100)) < 2, $"文本应被移动（{x0:0.#} → {t.X:0.#}）");
        Check.True(Math.Abs(t.Y - (y0 + 80)) < 2, $"文本应被移动（{y0:0.#} → {t.Y:0.#}）");
        Check.Equal("搬我", t.Text, "移动不应改变文字内容");

        // 删除并撤销
        h.Commands.Execute(new RemoveObjectsCommand(h.Page, [t]));
        Check.Equal(0, h.Page.Count, "文本应被删除");
        Check.True(h.Commands.Undo(), "应可撤销");
        Check.Equal(1, h.Page.Count, "撤销后文本回来");
        Check.Equal("搬我", ((TextObject)h.Page.Objects[0]).Text, "文字内容应完整回来");
    }
}
