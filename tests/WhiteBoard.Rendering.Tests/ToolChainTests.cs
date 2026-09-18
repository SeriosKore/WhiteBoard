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
/// 「画一笔」端到端链路测试（M0 里程碑）。
///
/// 走的是**生产代码**：合成 <see cref="PointerSample"/> → <see cref="ToolDispatcher"/>
/// （掌拒 → 路由 → 工具）→ 命令入栈 → <see cref="PageRenderer"/> 出像素。
/// 与 <c>CanvasHost</c> 在真机上跑的是同一条链路，因此这些断言能代表真实行为。
/// </summary>
public static class ToolChainTests
{
    private const string Pen = "#F5F5F0";

    /// <summary>测试用的"画布"：文档 + 页 + 撤销栈 + 渲染器。</summary>
    private sealed class Harness
    {
        public readonly WhiteboardDocument Doc = new() { Id = 1 };
        public readonly SmoothGeometryBuilder Geometry = new();
        public readonly PageRenderer Renderer;
        public readonly CommandManager Commands = new();
        public readonly ToolDispatcher Dispatcher = new();

        public Harness()
        {
            Doc.EnsureAtLeastOnePage();
            Renderer = new PageRenderer(Geometry);
            Commands.SetCurrentPage(Doc.CurrentPage.Id);
        }

        public Page Page => Doc.CurrentPage;

        public ToolContext Ctx() => new()
        {
            Page = Page,
            Viewport = Page.Viewport,
            Commands = Commands,
            Geometry = Geometry,
            AllocateObjectId = () => Doc.AllocateObjectId(),
            PenColor = Pen,
            PenWidth = 6
        };

        public int ObjectCount => Page.Count;
        public int NextId => Doc.NextObjectId;
    }

    private static long _ticks = 1_000_000;

    private static PointerSample Mouse(double x, double y)
        => new(0, PointerKind.Mouse, x, y, 0.5f, 0, _ticks += 8 * TimeSpan.TicksPerMillisecond / 10);

    private static PointerSample Stylus(double x, double y)
        => new(1, PointerKind.Stylus, x, y, 0.7f, 0, _ticks += 8 * TimeSpan.TicksPerMillisecond / 10);

    private static PointerSample Touch(int id, double x, double y, double contactWidth = 20)
        => new(id, PointerKind.Touch, x, y, 0.4f, contactWidth, _ticks += 8 * TimeSpan.TicksPerMillisecond / 10);

    /// <summary>合成一次完整的拖拽（落笔 → 若干移动 → 抬笔）。</summary>
    private static void Drag(Harness h, ITool tool, PointerSample down, PointerSample move, PointerSample up)
    {
        var ctx = h.Ctx();
        h.Dispatcher.Dispatch(PointerAction.Down, down, tool, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, move, tool, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, up, tool, ctx, false);
    }

    // ── ① 落笔 → 抬笔 → 对象落进文档 ─────────────────────────────────────

    public static void Test_Chain_MouseStrokeCommitsOneObject()
    {
        var h = new Harness();
        var pen = new PenTool();

        Drag(h, pen, Mouse(100, 100), Mouse(200, 140), Mouse(300, 100));

        Check.Equal(1, h.ObjectCount, "抬笔后本页应有 1 个对象");

        var obj = (FreehandObject)h.Page.Objects[0];
        Check.True(obj is FreehandObject, $"应为自由笔迹，实际 {obj.GetType().Name}");
        Check.Equal(3, obj.Points.Count, "应有 3 个采样点");
        Check.Equal(Pen, obj.Color, "颜色应取当前画笔色");
        Check.Near(6, obj.PenWidth, 1e-9, "笔宽应取当前笔宽");
    }

    public static void Test_Chain_StrokeIsUndoableAndRedoable()
    {
        var h = new Harness();
        var pen = new PenTool();
        Drag(h, pen, Mouse(100, 100), Mouse(200, 140), Mouse(300, 100));

        Check.True(h.Commands.CanUndo, "抬笔后应有可撤销操作");
        Check.Equal("添加", h.Commands.Current.NextUndoName ?? "", "撤销项名称应可读");

        Check.True(h.Commands.Undo(), "撤销应成功");
        Check.Equal(0, h.ObjectCount, "撤销后对象应被移除");

        Check.True(h.Commands.Redo(), "重做应成功");
        Check.Equal(1, h.ObjectCount, "重做后对象应回来");
    }

    /// <summary>
    /// **湿/干同源不变量**（M0 §六-D）：书写中的预览几何与抬笔后的干笔迹几何必须**完全一致**，
    /// 否则用户会看到抬笔瞬间的"跳变"——这正是旧项目失败的观感问题之一。
    /// </summary>
    public static void Test_Chain_WetPreviewEqualsDryGeometry()
    {
        var h = new Harness();
        var pen = new PenTool();
        var ctx = h.Ctx();

        h.Dispatcher.Dispatch(PointerAction.Down, Mouse(100, 300), pen, ctx, false);

        // 注意：抬笔点必须与最后一个移动点重合，否则比的是"两个不同的点集"，
        // 而这里要验证的是"同一批点的湿态与干态是否同源"。
        var last = Mouse(0, 0);
        for (var i = 1; i <= 40; i++)
        {
            last = Mouse(100 + i * 8, 300 + Math.Sin(i / 4.0) * 60);
            h.Dispatcher.Dispatch(PointerAction.Move, last, pen, ctx, false);
        }

        var wet = pen.BuildPreview(ctx);
        Check.True(wet is not null, "书写中应有预览几何");
        var wetBounds = wet!.Bounds;

        h.Dispatcher.Dispatch(PointerAction.Up, last, pen, ctx, false);

        Check.Equal(1, h.ObjectCount, "抬笔后应有 1 个对象");
        var dry = h.Geometry.GetWorldGeometry(h.Page.Objects[0]);

        // 容差说明：干笔迹的几何在**局部坐标**构建后再经 LocalToWorld 矩阵变换到世界，
        // 而预览几何直接在**世界坐标**构建，两条路径的浮点舍入不同。
        // 实测差异 ~0.0008 px（远小于 1 px），因此这里按 0.01 px 断言：
        // 只要不是"跳变"级的差异（旧项目的问题是几像素甚至更多），就应通过。
        const double Tolerance = 0.01;

        Check.Near(wetBounds.X, dry.Bounds.X, Tolerance, "预览与干笔迹左边界应一致");
        Check.Near(wetBounds.Y, dry.Bounds.Y, Tolerance, "预览与干笔迹上边界应一致");
        Check.Near(wetBounds.Width, dry.Bounds.Width, Tolerance, "预览与干笔迹宽度应一致");
        Check.Near(wetBounds.Height, dry.Bounds.Height, Tolerance, "预览与干笔迹高度应一致");
        Check.Near(wet.GetArea(), dry.GetArea(), 0.5, "预览与干笔迹面积应一致（同源渲染）");
    }

    public static void Test_Chain_PreviewClearedAfterCommit()
    {
        var h = new Harness();
        var pen = new PenTool();
        var ctx = h.Ctx();

        h.Dispatcher.Dispatch(PointerAction.Down, Mouse(100, 100), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, Mouse(180, 160), pen, ctx, false);
        Check.True(pen.BuildPreview(ctx) is not null, "书写中应有预览");

        h.Dispatcher.Dispatch(PointerAction.Up, Mouse(260, 100), pen, ctx, false);
        Check.True(pen.BuildPreview(ctx) is null, "抬笔后不应再有预览（已变成干笔迹）");
    }

    // ── ② 形状工具 ────────────────────────────────────────────────────────

    public static void Test_Chain_ShapeToolsCommitExpectedKinds()
    {
        var cases = new (ITool Tool, string Kind)[]
        {
            (new LineTool(), "LineObject"),
            (new RectTool(), "RectObject"),
            (new EllipseTool(), "EllipseObject")
        };

        foreach (var (tool, kind) in cases)
        {
            var h = new Harness();
            Drag(h, tool, Mouse(100, 100), Mouse(200, 180), Mouse(300, 260));

            Check.Equal(1, h.ObjectCount, $"{tool.Name}：抬笔后应有 1 个对象");
            Check.Equal(kind, h.Page.Objects[0].GetType().Name, $"{tool.Name}：对象类型");
        }
    }

    public static void Test_Chain_TinyDragProducesNothing()
    {
        var h = new Harness();
        var rect = new RectTool();

        // 位移 1 世界单位 < MinDragWorld(2) → 视为误触，不生成对象
        Drag(h, rect, Mouse(100, 100), Mouse(100.5, 100.5), Mouse(101, 101));

        Check.Equal(0, h.ObjectCount, "过小的拖动不应生成对象");
        Check.True(!h.Commands.CanUndo, "未生成对象时不应留下撤销项");
    }

    // ── ③ 指针路由：合成鼠标、双指、手掌 ──────────────────────────────────

    /// <summary>
    /// 触屏上 WPF 会先抛 Touch 再合成 Mouse；不抑制就会"画两遍"。
    /// 这里验证触摸抬笔后紧跟着的鼠标事件被丢弃 → 仍然只有 1 个对象。
    /// </summary>
    public static void Test_Chain_SyntheticMouseIsSuppressed()
    {
        var h = new Harness();
        var pen = new PenTool();
        var ctx = h.Ctx();

        h.Dispatcher.Dispatch(PointerAction.Down, Touch(7, 100, 100), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, Touch(7, 200, 140), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Touch(7, 300, 100), pen, ctx, false);

        // 立即到来的合成鼠标序列
        h.Dispatcher.Dispatch(PointerAction.Down, Mouse(300, 100), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, Mouse(400, 200), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Mouse(500, 300), pen, ctx, false);

        Check.Equal(1, h.ObjectCount, "合成鼠标事件不应产生第二个对象");
    }

    /// <summary>触屏：第二指在判定窗口内及时到来 → 丢弃这点头笔画并进入手势（ADR-19）。</summary>
    public static void Test_Chain_TwoFingerCancelsEarlyStroke()
    {
        var h = new Harness();
        var pen = new PenTool();
        var ctx = h.Ctx();

        h.Dispatcher.Dispatch(PointerAction.Down, Touch(1, 100, 100), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, Touch(1, 108, 104), pen, ctx, false);

        var r = h.Dispatcher.Dispatch(PointerAction.Down, Touch(2, 300, 300), pen, ctx, false);

        Check.True(h.Dispatcher.IsGestureActive, "两指应进入手势");
        Check.Equal(DispatchOutcome.Gesture, r.Outcome, "第二指应路由为手势");
        Check.True(r.Message is not null && r.Message.Contains("丢弃"), $"应提示丢弃起笔，实际 {r.Message}");
        Check.Equal(1, h.Dispatcher.CancelledStrokeCount, "应记录一次被丢弃的起笔");

        // 抬手结束整次交互后，不该有任何对象落进文档
        h.Dispatcher.Dispatch(PointerAction.Up, Touch(1, 110, 106), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Touch(2, 320, 320), pen, ctx, false);
        Check.Equal(0, h.ObjectCount, "被手势接管的起笔不应留下对象");
    }

    /// <summary>触屏：笔画已经写了一会儿，第二指视为误触 → 保护书写（Q4 决策）。</summary>
    public static void Test_Chain_SecondFingerAfterWindowIsIgnored()
    {
        var h = new Harness();
        var pen = new PenTool();
        var ctx = h.Ctx();

        h.Dispatcher.Dispatch(PointerAction.Down, Touch(1, 100, 100), pen, ctx, false);
        // 超过 GestureMaxPoints（6 点）之后再落第二指
        for (var i = 0; i < 10; i++)
            h.Dispatcher.Dispatch(PointerAction.Move, Touch(1, 100 + i * 10, 100 + i * 4), pen, ctx, false);

        var r = h.Dispatcher.Dispatch(PointerAction.Down, Touch(2, 400, 400), pen, ctx, false);

        Check.Equal(DispatchOutcome.Ignored, r.Outcome, "书写中的第二指应被忽略");
        Check.True(!h.Dispatcher.IsGestureActive, "不应因此进入手势");

        h.Dispatcher.Dispatch(PointerAction.Up, Touch(1, 220, 140), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Touch(2, 400, 400), pen, ctx, false);
        Check.Equal(1, h.ObjectCount, "书写应被完整保留");
    }

    /// <summary>两指手势应真的改变视口（缩放+平移），且起点下的世界坐标跟随中点。</summary>
    public static void Test_Chain_PinchGestureZoomsViewport()
    {
        var h = new Harness();
        var pen = new PenTool();
        var ctx = h.Ctx();
        var vp = h.Page.Viewport;

        var before = vp.Zoom;
        h.Dispatcher.Dispatch(PointerAction.Down, Touch(1, 300, 400), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Down, Touch(2, 500, 400), pen, ctx, false);
        Check.True(h.Dispatcher.IsGestureActive, "两指应进入手势");

        // 两指距离由 200 拉到 400 → 约 2 倍
        h.Dispatcher.Dispatch(PointerAction.Move, Touch(1, 200, 400), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, Touch(2, 600, 400), pen, ctx, false);

        Check.True(vp.Zoom > before * 1.5, $"缩放应显著变大（前 {before:0.##} 后 {vp.Zoom:0.##}）");

        h.Dispatcher.Dispatch(PointerAction.Up, Touch(1, 200, 400), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Touch(2, 600, 400), pen, ctx, false);
        Check.True(!h.Dispatcher.IsGestureActive, "抬手后手势应结束");
        Check.Equal(0, h.ObjectCount, "手势不应产生任何对象");
    }

    /// <summary>掌拒：宽接触（手掌）在画笔工具下必须被拒绝（决策 D3）。</summary>
    public static void Test_Chain_PalmIsRejectedOnPenTool()
    {
        var h = new Harness();
        var pen = new PenTool();
        var ctx = h.Ctx();

        var r = h.Dispatcher.Dispatch(PointerAction.Down, Touch(3, 200, 200, contactWidth: 60), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, Touch(3, 260, 240, 60), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Touch(3, 300, 260, 60), pen, ctx, false);

        Check.Equal(DispatchOutcome.PalmRejected, r.Outcome, "手掌接触应被拒绝");
        Check.Equal(1, h.Dispatcher.PalmRejectCount, "掌拒计数应为 1");
        Check.Equal(0, h.ObjectCount, "掌拒不应产生笔迹");
    }

    /// <summary>笔在感应范围内时，手指触摸一律拒绝（避免边写边被手碰乱）。</summary>
    public static void Test_Chain_TouchRejectedWhileStylusActive()
    {
        var h = new Harness();
        var pen = new PenTool();
        var ctx = h.Ctx();

        var r = h.Dispatcher.Dispatch(PointerAction.Down, Touch(1, 100, 100), pen, ctx, stylusActive: true);
        Check.Equal(DispatchOutcome.PalmRejected, r.Outcome, "笔活跃时触摸应被拒绝");
        Check.Equal(0, h.ObjectCount, "不应产生对象");
    }

    // ── ④ 橡皮（ADR-18：遮罩保留 + 延迟烘焙）──────────────────────────────

    public static void Test_Chain_EraserMasksInsteadOfFragmenting()
    {
        var h = new Harness();
        var pen = new PenTool();
        var ctx = h.Ctx();

        // 画一条水平笔迹
        h.Dispatcher.Dispatch(PointerAction.Down, Mouse(100, 300), pen, ctx, false);
        for (var x = 110; x <= 600; x += 10)
            h.Dispatcher.Dispatch(PointerAction.Move, Mouse(x, 300), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Mouse(600, 300), pen, ctx, false);

        Check.Equal(1, h.ObjectCount, "先画一笔");
        var stroke = (FreehandObject)h.Page.Objects[0];
        var areaBefore = h.Geometry.GetWorldGeometry(stroke).GetArea();
        var idBefore = stroke.Id;

        // 用橡皮在中间划一段
        var eraser = new EraserTool(h.Geometry);
        var ectx = h.Ctx();
        h.Dispatcher.Dispatch(PointerAction.Down, Mouse(300, 300), eraser, ectx, false);
        for (var x = 310; x <= 400; x += 10)
            h.Dispatcher.Dispatch(PointerAction.Move, Mouse(x, 300), eraser, ectx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Mouse(400, 300), eraser, ectx, false);

        // ADR-18 的核心断言：仍是 1 个对象、Id 不变、面积变小
        Check.Equal(1, h.ObjectCount, "擦除不应把笔迹切碎成多个对象");
        Check.Equal(idBefore, h.Page.Objects[0].Id, "对象 Id 应保持不变");
        Check.True(stroke.Erasures.Count > 0, "应追加了擦除遮罩");
        var areaAfter = h.Geometry.GetWorldGeometry(stroke).GetArea();
        Check.True(areaAfter < areaBefore, $"擦除后面积应减少（前 {areaBefore:0} 后 {areaAfter:0}）");
        Check.True(areaAfter > 0, "只擦中间不应把整条擦没");
    }

    public static void Test_Chain_EraserIsUndoable()
    {
        var h = new Harness();
        var pen = new PenTool();
        var ctx = h.Ctx();
        h.Dispatcher.Dispatch(PointerAction.Down, Mouse(100, 300), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, Mouse(400, 300), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Mouse(600, 300), pen, ctx, false);

        var stroke = (FreehandObject)h.Page.Objects[0];
        var area0 = h.Geometry.GetWorldGeometry(stroke).GetArea();

        var eraser = new EraserTool(h.Geometry);
        var ectx = h.Ctx();
        h.Dispatcher.Dispatch(PointerAction.Down, Mouse(300, 300), eraser, ectx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Mouse(300, 300), eraser, ectx, false);
        var area1 = h.Geometry.GetWorldGeometry(stroke).GetArea();
        Check.True(area1 < area0, "擦除应立即生效");

        Check.True(h.Commands.Undo(), "擦除应可撤销");
        var area2 = h.Geometry.GetWorldGeometry(stroke).GetArea();
        Check.Near(area0, area2, 0.5, "撤销擦除后面积应恢复");
    }

    /// <summary>完全擦掉的对象应被删除，且能随同一条命令一起撤销回来（"结果为空则删除"）。</summary>
    public static void Test_Chain_EraserRemovesFullyErasedObjectAndUndoRestoresIt()
    {
        var h = new Harness();
        var pen = new PenTool();
        var ctx = h.Ctx();
        h.Dispatcher.Dispatch(PointerAction.Down, Mouse(200, 300), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, Mouse(260, 300), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Mouse(300, 300), pen, ctx, false);
        Check.Equal(1, h.ObjectCount, "先画一小笔");

        var eraser = new EraserTool(h.Geometry) { DiameterPx = 420 };
        var ectx = h.Ctx();
        h.Dispatcher.Dispatch(PointerAction.Down, Mouse(250, 300), eraser, ectx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, Mouse(300, 300), eraser, ectx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Mouse(260, 300), eraser, ectx, false);

        Check.Equal(0, h.ObjectCount, "被完全擦除的对象应被删除");

        Check.True(h.Commands.Undo(), "应可撤销");
        Check.Equal(1, h.ObjectCount, "撤销后对象应恢复");
    }

    /// <summary>橡皮工具下，手掌接触转为大号橡皮（ADR-11），不会被掌拒丢掉。</summary>
    public static void Test_Chain_PalmActsAsEraserOnEraserTool()
    {
        var h = new Harness();
        var pen = new PenTool();
        var ctx = h.Ctx();
        h.Dispatcher.Dispatch(PointerAction.Down, Mouse(200, 300), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, Mouse(400, 300), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Mouse(500, 300), pen, ctx, false);

        var stroke = (FreehandObject)h.Page.Objects[0];
        var area0 = h.Geometry.GetWorldGeometry(stroke).GetArea();

        var eraser = new EraserTool(h.Geometry);
        // 关键：先经 SetTool 同步掌拒策略，否则 PalmActsAsEraser 仍是 false
        h.Dispatcher.SetTool(null, eraser, h.Ctx());
        Check.True(h.Dispatcher.Palm.PalmActsAsEraser, "橡皮工具下应开启手掌擦除");

        var ectx = h.Ctx();
        var r = h.Dispatcher.Dispatch(PointerAction.Down, Touch(9, 300, 300, contactWidth: 90), eraser, ectx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Touch(9, 300, 300, 90), eraser, ectx, false);

        Check.Equal(DispatchOutcome.Stroke, r.Outcome, "手掌在橡皮工具下应被当作擦除而非拒绝");
        Check.Equal(0, h.Dispatcher.PalmRejectCount, "不应触发掌拒");
        var area1 = h.Geometry.GetWorldGeometry(stroke).GetArea();
        Check.True(area1 < area0, $"手掌应擦掉一部分（前 {area0:0} 后 {area1:0}）");
    }

    // ── ⑤ 出像素：真渲染到位图 ────────────────────────────────────────────

    public static void Test_Chain_RenderedStrokePixelsAppearWhereExpected()
    {
        var h = new Harness();
        h.Renderer.BackgroundColor = Color.FromRgb(0x2F, 0x4F, 0x3A);
        var pen = new PenTool();

        const int w = 800;
        const int hgt = 600;

        // 从左到右一条水平线（世界=屏幕，缩放 1、平移 0）
        var ctx = h.Ctx();
        h.Dispatcher.Dispatch(PointerAction.Down, Mouse(100, 300), pen, ctx, false);
        for (var x = 110; x <= 700; x += 10)
            h.Dispatcher.Dispatch(PointerAction.Move, Mouse(x, 300), pen, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, Mouse(700, 300), pen, ctx, false);

        Check.Equal(1, h.ObjectCount, "应有 1 个对象");

        var bmp = Render(h, w, hgt);

        // 笔迹带内必须有足够的亮像素……
        var inBand = CountNonBackground(bmp, 0, 285, w, 316);
        Check.True(inBand > 300, $"笔迹带内应有足够亮像素，实际 {inBand}");

        // ……带外（上下两块）必须完全是背景
        Check.Equal(0, CountNonBackground(bmp, 0, 0, w, 284), "笔迹上方不应有任何绘制");
        Check.Equal(0, CountNonBackground(bmp, 0, 317, w, hgt), "笔迹下方不应有任何绘制");

        // 右端（x>720）不应有内容
        Check.Equal(0, CountNonBackground(bmp, 721, 0, w, hgt), "笔迹右端之外不应有内容");
    }

    public static void Test_Chain_RenderedShapePixelsAreClosed()
    {
        var h = new Harness();
        h.Renderer.BackgroundColor = Color.FromRgb(0x2F, 0x4F, 0x3A);
        var rect = new RectTool();

        Drag(h, rect, Mouse(200, 200), Mouse(400, 320), Mouse(600, 400));
        Check.Equal(1, h.ObjectCount, "应生成 1 个矩形");

        var bmp = Render(h, 800, 600);

        // 矩形中心必须是背景（描边不填充），边框上必须有像素
        Check.True(IsBackground(bmp, 400, 300), "矩形内部应为背景色（描边不填充）");
        Check.True(!IsBackground(bmp, 400, 202), "矩形上边框应有像素");
        Check.True(!IsBackground(bmp, 202, 300), "矩形左边框应有像素");
        Check.True(IsBackground(bmp, 100, 500), "远离形状的区域应为背景色");
    }

    private static RenderTargetBitmap Render(Harness h, int w, int hgt)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            h.Renderer.Render(dc, h.Page, h.Page.Viewport, w, hgt);

        var bmp = new RenderTargetBitmap(w, hgt, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        return bmp;
    }

    /// <summary>统计矩形区域内的非背景像素数（半开区间 [x0,x1) × [y0,y1)）。</summary>
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
        bmp.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), buf, 4, 0);
        return Math.Abs(buf[0] - 0x3A) <= 12
               && Math.Abs(buf[1] - 0x4F) <= 12
               && Math.Abs(buf[2] - 0x2F) <= 12;
    }
}
