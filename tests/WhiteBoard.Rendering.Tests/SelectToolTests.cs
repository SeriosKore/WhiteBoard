using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Input;
using WhiteBoard.Core.Model;
using WhiteBoard.Core.Tests;
using WhiteBoard.Rendering;
using WhiteBoard.Rendering.Tools;

namespace WhiteBoard.Rendering.Tests;

/// <summary>
/// 选择工具：点选（取最上层）/ 框选 / 圈选 / 拖动移动 / 误触阈值，
/// 以及最关键的"**拖动移动之后撤销必须回到原位**"。
/// </summary>
public static class SelectToolTests
{
    private const string Pen = "#F5F5F0";

    private sealed class Harness
    {
        public readonly WhiteboardDocument Doc = new() { Id = 1 };
        public readonly SmoothGeometryBuilder Geometry = new();
        public readonly CommandManager Commands = new();
        public readonly ToolDispatcher Dispatcher = new();
        public readonly SelectionState Selection = new();

        public Harness()
        {
            Doc.EnsureAtLeastOnePage();
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
            PenWidth = 6,
            Selection = Selection
        };

        /// <summary>放一个矩形对象在指定世界位置（模拟"已经画好的东西"）。</summary>
        public RectObject AddRect(double x, double y, double w = 100, double h = 60)
        {
            var r = RectObject.FromWorldCorners(Doc.AllocateObjectId(), new PointD(x, y), new PointD(x + w, y + h), Pen, 6);
            Page.Add(r);
            return r;
        }
    }

    private static long _ticks = 5_000_000;

    private static PointerSample At(double x, double y)
        => new(0, PointerKind.Mouse, x, y, 0.5f, 0, _ticks += 8 * TimeSpan.TicksPerMillisecond / 10);

    /// <summary>合成一次完整的拖拽（落笔 → 若干移动 → 抬笔）。</summary>
    private static void Drag(Harness h, SelectTool tool, IReadOnlyList<PointD> path, bool additive = false)
    {
        tool.Additive = additive;
        var ctx = h.Ctx();
        h.Dispatcher.Dispatch(PointerAction.Down, At(path[0].X, path[0].Y), tool, ctx, false);
        for (var i = 1; i < path.Count - 1; i++)
            h.Dispatcher.Dispatch(PointerAction.Move, At(path[i].X, path[i].Y), tool, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Up, At(path[^1].X, path[^1].Y), tool, ctx, false);
    }

    // ── 点选 ──────────────────────────────────────────────────────────────

    public static void Test_Select_ClickPicksObjectUnderPointer()
    {
        var h = new Harness();
        var rect = h.AddRect(200, 200);
        var tool = new SelectTool(h.Geometry);

        // 点边框上（矩形是空心的，中心点不中）
        Drag(h, tool, [new PointD(250, 200), new PointD(250, 200), new PointD(250, 200)]);

        Check.Equal(1, h.Selection.Count, "应选中 1 个对象");
        Check.True(h.Selection.Contains(rect), "选中的应是那个矩形");
    }

    public static void Test_Select_ClickOnEmptyClearsSelection()
    {
        var h = new Harness();
        var rect = h.AddRect(200, 200);
        var tool = new SelectTool(h.Geometry);

        Drag(h, tool, [new PointD(250, 200), new PointD(250, 200), new PointD(250, 200)]);
        Check.Equal(1, h.Selection.Count, "先选中");

        Drag(h, tool, [new PointD(700, 600), new PointD(700, 600), new PointD(700, 600)]);
        Check.Equal(0, h.Selection.Count, "点空白处应取消选择");
        Check.True(!h.Selection.Contains(rect), "不应还选中着");
    }

    /// <summary>点选必须取**最上面**的那个：用户看到的是最上层，点到的也必须是它。</summary>
    public static void Test_Select_ClickPicksTopmostObject()
    {
        var h = new Harness();
        var bottom = h.AddRect(200, 200);
        var top = h.AddRect(200, 200);   // 完全重合，Z 更大 → 在上面

        Check.True(top.ZIndex > bottom.ZIndex, "后加的应在上面");

        var tool = new SelectTool(h.Geometry);
        Drag(h, tool, [new PointD(250, 200), new PointD(250, 200), new PointD(250, 200)]);

        Check.True(h.Selection.Contains(top), "应选中上层对象");
        Check.True(!h.Selection.Contains(bottom), "不应选中被盖住的那个");
    }

    /// <summary>被橡皮擦掉的部分不该能点中（与用户看到的一致）。</summary>
    public static void Test_Select_ErasedAreaIsNotClickable()
    {
        var h = new Harness();
        var rect = h.AddRect(200, 200, 200, 100);

        // 把左边那条边整条擦掉（对象局部坐标：左边 x=3 附近）
        for (var y = -20.0; y <= 120; y += 8)
            rect.AddErasure(new EraserCircle(3, y, 14));

        var tool = new SelectTool(h.Geometry);
        Drag(h, tool, [new PointD(203, 250), new PointD(203, 250), new PointD(203, 250)]);
        Check.True(!h.Selection.Contains(rect), "被擦掉的左边不应点中");

        Drag(h, tool, [new PointD(400, 250), new PointD(400, 250), new PointD(400, 250)]);
        Check.True(h.Selection.Contains(rect), "没被擦掉的右边应能点中");
    }

    // ── 多选 ──────────────────────────────────────────────────────────────

    public static void Test_Select_AdditiveToggle()
    {
        var h = new Harness();
        var a = h.AddRect(100, 100);
        var b = h.AddRect(400, 400);
        var tool = new SelectTool(h.Geometry);

        Drag(h, tool, [new PointD(150, 100), new PointD(150, 100), new PointD(150, 100)]);
        Check.Equal(1, h.Selection.Count, "先选中第一个");

        Drag(h, tool, [new PointD(450, 400), new PointD(450, 400), new PointD(450, 400)], additive: true);
        Check.Equal(2, h.Selection.Count, "多选后应有 2 个");
        Check.True(h.Selection.Contains(a) && h.Selection.Contains(b), "两个都应在选中集合里");

        // 再 Ctrl 点一次 → 取消选中
        Drag(h, tool, [new PointD(450, 400), new PointD(450, 400), new PointD(450, 400)], additive: true);
        Check.Equal(1, h.Selection.Count, "再点一次应取消选中第二个");
        Check.True(h.Selection.Contains(a), "第一个仍在选中");
    }

    // ── 框选 / 圈选 ───────────────────────────────────────────────────────

    public static void Test_Select_RectangleMarqueeSelectsIntersecting()
    {
        var h = new Harness();
        var inside = h.AddRect(200, 200, 80, 60);
        var outside = h.AddRect(600, 500, 80, 60);

        var tool = new SelectTool(h.Geometry) { Mode = SelectionMode.Rectangle };
        Drag(h, tool, [new PointD(100, 100), new PointD(300, 300), new PointD(500, 400)]);

        Check.True(h.Selection.Contains(inside), "框内的应被选中");
        Check.True(!h.Selection.Contains(outside), "框外的不应被选中");
    }

    public static void Test_Select_LassoSelectsEnclosed()
    {
        var h = new Harness();
        var inside = h.AddRect(300, 300, 60, 40);
        var outside = h.AddRect(100, 100, 60, 40);

        var tool = new SelectTool(h.Geometry) { Mode = SelectionMode.Lasso };

        // 用一圈点围住 (300,300)-(360,340)
        var ring = new List<PointD>();
        for (var i = 0; i <= 24; i++)
        {
            var a = i * 2 * Math.PI / 24;
            ring.Add(new PointD(330 + 120 * Math.Cos(a), 320 + 120 * Math.Sin(a)));
        }
        ring.Add(ring[0]);

        Drag(h, tool, ring);

        Check.True(h.Selection.Contains(inside), "圈内的应被选中");
        Check.True(!h.Selection.Contains(outside), "圈外的不应被选中");
    }

    /// <summary>框太小（误触）不应把选择清空成"框选结果为空"。</summary>
    public static void Test_Select_TinyMarqueeKeepsClickSemantics()
    {
        var h = new Harness();
        var rect = h.AddRect(200, 200);
        var tool = new SelectTool(h.Geometry);

        Drag(h, tool, [new PointD(250, 200), new PointD(250, 200), new PointD(250, 200)]);
        Check.Equal(1, h.Selection.Count, "先选中矩形");

        // 在对象旁边空白处"点"一下（位移 2px，属于手抖，不构成框选）
        Drag(h, tool, [new PointD(700, 400), new PointD(700, 401), new PointD(701, 400)]);
        Check.Equal(0, h.Selection.Count, "点空白应清空选择，而不是留下一个空框选");
    }

    // ── 拖动移动 ──────────────────────────────────────────────────────────

    public static void Test_Select_DragMovesSelectedObject()
    {
        var h = new Harness();
        var rect = h.AddRect(200, 200);
        var x0 = rect.X;
        var y0 = rect.Y;

        var tool = new SelectTool(h.Geometry);
        Drag(h, tool, [new PointD(250, 200), new PointD(300, 240), new PointD(350, 260)]);

        Check.True(h.Selection.Contains(rect), "拖动后仍应选中");
        Check.Near(x0 + 100, rect.X, 0.5, "应向右移动 100 世界单位");
        Check.Near(y0 + 60, rect.Y, 0.5, "应向下移动 60 世界单位");
        Check.True(h.Commands.CanUndo, "移动应是一条可撤销命令");
    }

    /// <summary>
    /// **最关键的一条**：拖动是"实时改对象 + 抬手补命令"，
    /// 而 <see cref="MoveObjectsCommand"/> 在首次执行时才捕获起点——
    /// 如果不在抬手前先还原，撤销就会"回到拖动后的位置"，等于撤销失效。
    /// </summary>
    public static void Test_Select_DragMoveIsUndoableBackToOriginalPosition()
    {
        var h = new Harness();
        var rect = h.AddRect(200, 200);
        var x0 = rect.X;
        var y0 = rect.Y;

        var tool = new SelectTool(h.Geometry);
        Drag(h, tool, [new PointD(250, 200), new PointD(320, 260), new PointD(400, 320)]);

        Check.True(Math.Abs(rect.X - x0) > 10, "先确认真的移动了");

        Check.True(h.Commands.Undo(), "应可撤销");
        Check.Near(x0, rect.X, 1e-6, "撤销后 X 应回到原位");
        Check.Near(y0, rect.Y, 1e-6, "撤销后 Y 应回到原位");

        Check.True(h.Commands.Redo(), "应可重做");
        Check.True(Math.Abs(rect.X - x0) > 10, "重做后应再次移开");
        Check.Near(x0 + 150, rect.X, 0.5, "重做后的位置应与拖动结束时一致");
    }

    public static void Test_Select_MovingMultipleObjectsTogether()
    {
        var h = new Harness();
        var a = h.AddRect(100, 100);
        var b = h.AddRect(400, 400);
        var ax = a.X;
        var bx = b.X;

        var tool = new SelectTool(h.Geometry);

        // 框选两个
        Drag(h, tool, [new PointD(50, 50), new PointD(300, 300), new PointD(600, 600)]);
        Check.Equal(2, h.Selection.Count, "应选中 2 个");

        // 抓住其中一个拖动
        Drag(h, tool, [new PointD(150, 100), new PointD(200, 150), new PointD(250, 200)]);
        Check.Near(ax + 100, a.X, 0.5, "第一个应跟着移动");
        Check.Near(bx + 100, b.X, 0.5, "第二个也应跟着移动（多选整体移动）");

        Check.True(h.Commands.Undo(), "应可撤销");
        Check.Near(ax, a.X, 1e-6, "撤销后两者都应回原位");
        Check.Near(bx, b.X, 1e-6, "撤销后两者都应回原位");
    }

    /// <summary>轻微手抖（小于 4px 阈值）不应产生移动，也不应留下撤销项。</summary>
    public static void Test_Select_TinyDragDoesNotMoveOrPushCommand()
    {
        var h = new Harness();
        var rect = h.AddRect(200, 200);
        var x0 = rect.X;
        var y0 = rect.Y;

        var tool = new SelectTool(h.Geometry);
        Drag(h, tool, [new PointD(250, 200), new PointD(250.5, 200.5), new PointD(251, 201)]);

        Check.Near(x0, rect.X, 1e-9, "位移小于阈值不应移动");
        Check.Near(y0, rect.Y, 1e-9, "位移小于阈值不应移动");
        Check.True(!h.Commands.CanUndo, "不应留下撤销项");
        Check.True(h.Selection.Contains(rect), "但仍应选中它（点选有效）");
    }

    /// <summary>取消（切工具、切页）时必须把拖动中的对象还原，不能留下半移动状态。</summary>
    public static void Test_Select_CancelRestoresObjectsMidDrag()
    {
        var h = new Harness();
        var rect = h.AddRect(200, 200);
        var x0 = rect.X;
        var y0 = rect.Y;

        var tool = new SelectTool(h.Geometry);
        var ctx = h.Ctx();

        h.Dispatcher.Dispatch(PointerAction.Down, At(250, 200), tool, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, At(280, 230), tool, ctx, false);
        h.Dispatcher.Dispatch(PointerAction.Move, At(320, 260), tool, ctx, false);
        Check.True(Math.Abs(rect.X - x0) > 1, "拖动中对象应跟随");

        tool.Cancel();   // 例如用户切到了画笔

        Check.Near(x0, rect.X, 1e-9, "取消后应回到原位");
        Check.Near(y0, rect.Y, 1e-9, "取消后应回到原位");
        Check.True(!h.Commands.CanUndo, "取消不应留下撤销项");
    }

    // ── 选中集合的健壮性 ──────────────────────────────────────────────────

    /// <summary>撤销"添加"之后，选中集合里那个对象已不在页面里，必须被剔除。</summary>
    public static void Test_Selection_RemoveMissingDropsDeletedObjects()
    {
        var h = new Harness();
        var rect = h.AddRect(200, 200);
        h.Selection.Set(rect);
        Check.Equal(1, h.Selection.Count, "先选中");

        h.Commands.Execute(new RemoveObjectsCommand(h.Page, [rect]));
        Check.True(h.Selection.RemoveMissing(h.Page), "应报告剔除了对象");
        Check.Equal(0, h.Selection.Count, "选中集合应清空");
    }

    public static void Test_Selection_ChangedEventFires()
    {
        var s = new SelectionState();
        var fires = 0;
        s.Changed += () => fires++;

        var page = new Page { Id = 1 };
        var rect = RectObject.FromWorldCorners(1, new PointD(0, 0), new PointD(10, 10), Pen, 3);
        page.Add(rect);

        s.Set(rect);
        Check.Equal(1, fires, "第一次选中应触发一次");

        s.Set(rect);
        Check.Equal(1, fires, "重复选中同一个不应重复触发");

        s.Clear();
        Check.Equal(2, fires, "清空应触发");

        s.Clear();
        Check.Equal(2, fires, "已经空了再清空不应触发");
    }

    public static void Test_Select_SelectAllAndDeleteAll()
    {
        var h = new Harness();
        h.AddRect(100, 100);
        h.AddRect(400, 400);
        h.AddRect(700, 200);

        h.Selection.SetMany(h.Page.Objects);
        Check.Equal(3, h.Selection.Count, "应全选 3 个");

        var objs = h.Selection.Objects.ToList();
        h.Commands.Execute(new RemoveObjectsCommand(h.Page, objs));
        Check.Equal(0, h.Page.Count, "应全部删除");

        Check.True(h.Commands.Undo(), "应可撤销");
        Check.Equal(3, h.Page.Count, "撤销后 3 个都回来");
    }

    /// <summary>删除后撤销，对象回来时 Z 序也应回来（否则图层顺序会被静默改掉）。</summary>
    public static void Test_Select_UndoDeleteRestoresZOrder()
    {
        var h = new Harness();
        var a = h.AddRect(100, 100);
        var b = h.AddRect(120, 120);
        var c = h.AddRect(140, 140);

        var za = a.ZIndex;
        h.Commands.Execute(new RemoveObjectsCommand(h.Page, [b]));
        Check.Equal(2, h.Page.Count, "删掉一个");

        h.Commands.Undo();
        Check.Equal(3, h.Page.Count, "撤销后回来");
        Check.Equal(za, a.ZIndex, "其他对象的 Z 序不应变化");
        Check.Equal(1, h.Page.InRenderOrder().ToList().IndexOf(b), "被删对象应回到原来的层位置（a=0, b=1, c=2）");
        Check.True(c.ZIndex > b.ZIndex, "层序关系应保持");
    }
}
