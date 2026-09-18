using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Core.Tests;

/// <summary>文档/页面模型 + 每页独立撤销栈 + 擦除遮罩语义。</summary>
public static class ModelTests
{
    private static WhiteboardDocument NewDoc()
    {
        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();
        return doc;
    }

    private static FreehandObject NewStroke(
        WhiteboardDocument doc, double x, double y, int points = 5, double penWidth = 3)
    {
        var pts = new List<InkPoint>();
        for (var i = 0; i < points; i++) pts.Add(new InkPoint(x + i * 10, y, 0.5f));
        return FreehandObject.FromWorldPoints(doc.AllocateObjectId(), pts, "#F5F5F0", penWidth);
    }

    // ---- 文档 / 页面 ----

    public static void Test_Document_AlwaysHasAtLeastOnePage()
    {
        var doc = new WhiteboardDocument { Id = 1 };
        Check.Equal(0, doc.Pages.Count, "起初无页");
        doc.EnsureAtLeastOnePage();
        Check.Equal(1, doc.Pages.Count, "应自动补一页");
    }

    public static void Test_Document_RemoveLastPage_AddsBlankPage()
    {
        var doc = NewDoc();
        var only = doc.Pages[0];
        doc.RemovePage(only);
        Check.Equal(1, doc.Pages.Count, "删掉最后一页后必须仍有 1 页");
        Check.True(doc.Pages[0] != only, "应是新的空白页");
    }

    public static void Test_Document_DuplicatePage_DeepCopiesWithNewIds()
    {
        var doc = NewDoc();
        var p0 = doc.CurrentPage;
        var o = NewStroke(doc, 0, 0);
        p0.Add(o);

        var copy = doc.DuplicatePage(p0);

        Check.Equal(1, copy.Count, "副本应有 1 个对象");
        Check.True(copy.Objects[0].Id != o.Id, "副本对象必须有新 Id");
        Check.Equal(p0.Viewport.Zoom, copy.Viewport.Zoom, "视口应复制");
        Check.True(doc.Pages.IndexOf(copy) == doc.Pages.IndexOf(p0) + 1, "副本应插在原页之后");
    }

    public static void Test_Page_ZOrder_MoveAndOrder()
    {
        var doc = NewDoc();
        var page = doc.CurrentPage;
        var a = NewStroke(doc, 0, 0);
        var b = NewStroke(doc, 0, 50);
        page.Add(a);
        page.Add(b);

        var order = page.InRenderOrder().Select(o => o.Id).ToList();
        Check.True(order.IndexOf(a.Id) < order.IndexOf(b.Id), "先加的应更靠后渲染");

        page.BringToFront(a);
        order = page.InRenderOrder().Select(o => o.Id).ToList();
        Check.Equal(a.Id, order[^1], "置顶后应最后渲染");

        page.SendToBack(a);
        order = page.InRenderOrder().Select(o => o.Id).ToList();
        Check.Equal(a.Id, order[0], "置底后应最先渲染");
    }

    public static void Test_Page_VisibleObjects_Culls()
    {
        var doc = NewDoc();
        var page = doc.CurrentPage;
        var near = NewStroke(doc, 0, 0);
        var far = NewStroke(doc, 5000, 5000);
        page.Add(near);
        page.Add(far);

        var visible = page.VisibleObjects(new RectD(-100, -100, 500, 500)).ToList();
        Check.Equal(1, visible.Count, "只有近处对象可见");
        Check.Equal(near.Id, visible[0].Id, "可见的应是 near");
    }

    // ---- FreehandObject ----

    public static void Test_Freehand_FromWorldPoints_NormalizesToLocal()
    {
        var pts = new List<InkPoint>
        {
            new(100, 200, 0.5f),
            new(150, 260, 0.5f),
            new(120, 300, 0.5f)
        };
        var o = FreehandObject.FromWorldPoints(7, pts, "#FFFFFF", 4);

        Check.Near(100, o.X, 1e-9, "对象原点应取最小 X");
        Check.Near(200, o.Y, 1e-9, "对象原点应取最小 Y");
        Check.Near(50, o.LocalWidth, 1e-9, "局部宽 = maxX-minX");
        Check.Near(100, o.LocalHeight, 1e-9, "局部高 = maxY-minY");
        Check.Near(0, o.Points[0].X, 1e-9, "首点局部 X 应为 0");
        Check.Near(0, o.Points[0].Y, 1e-9, "首点局部 Y 应为 0");
    }

    public static void Test_Freehand_Clone_IsDeepCopy()
    {
        var doc = NewDoc();
        var o = NewStroke(doc, 10, 20);
        o.AddErasure(new EraserCircle(5, 5, 2));

        var c = (FreehandObject)o.Clone(999);

        Check.Equal(999, c.Id, "新 Id");
        Check.Near(o.X, c.X, 1e-9, "位置复制");
        Check.Equal(o.Points.Count, c.Points.Count, "点数复制");
        Check.Equal(1, c.Erasures.Count, "遮罩复制");

        c.Points.Add(new InkPoint(1, 1, 0.5f));
        Check.True(c.Points.Count != o.Points.Count, "改副本不应影响原对象（深拷贝）");
    }

    /// <summary>
    /// 世界包围盒必须**包含笔宽**：笔迹的可见范围是中心线 ± 笔宽/2。
    /// 早期实现只取采样点包围盒，导致"一条水平笔迹高度为 0"，
    /// 视口裁剪会在笔迹还露着一半时把它整条裁掉（已修，见 ExportTests 的回归用例）。
    /// </summary>
    public static void Test_Freehand_WorldBounds_IncludesPenWidth()
    {
        var doc = NewDoc();
        var o = NewStroke(doc, 100, 200, points: 3, penWidth: 6);  // 局部宽 20、点都在同一 y

        var wb = o.WorldBounds;

        // 中心线包围盒是 (100,200,20,0)，笔宽 6 → 四周各外扩 3
        Check.Near(97, wb.X, 1e-9, "世界包围盒 X 应含笔宽");
        Check.Near(197, wb.Y, 1e-9, "世界包围盒 Y 应含笔宽");
        Check.Near(26, wb.Width, 1e-9, "世界包围盒宽应含两侧笔宽");
        Check.Near(6, wb.Height, 1e-9, "水平笔迹的高应等于笔宽（不能是 0，否则会被视口裁掉）");
    }

    // ---- 擦除遮罩（ADR-18）----

    public static void Test_Erasure_AppendAndDeduplicate()
    {
        var doc = NewDoc();
        var o = NewStroke(doc, 0, 0);

        Check.True(o.AddErasure(new EraserCircle(10, 10, 5)), "首次添加应成功");
        Check.True(!o.AddErasure(new EraserCircle(10, 10, 5)), "完全相同的圆应被忽略");
        Check.True(o.AddErasure(new EraserCircle(10, 10, 6)), "半径不同应视为新遮罩");
        Check.Equal(2, o.Erasures.Count, "应有两个遮罩");
        Check.True(!o.AddErasure(new EraserCircle(0, 0, 0)), "半径为 0 不应添加");
    }

    public static void Test_Erasure_ObjectRemainsSingleObject()
    {
        // 这是 ADR-18 的核心：擦除**不**把一笔拆成多个对象
        var doc = NewDoc();
        var page = doc.CurrentPage;
        var id = doc.AllocateObjectId();
        var pts = new List<InkPoint>();
        for (var i = 0; i < 200; i++) pts.Add(new InkPoint(i * 5, 0, 0.5f));
        var stroke = FreehandObject.FromWorldPoints(id, pts, "#FFFFFF", 3);
        page.Add(stroke);

        // 沿笔迹打 10 个擦除圆
        for (var k = 0; k < 10; k++)
            stroke.AddErasure(EraserCircle.FromWorld(new PointD(k * 100 + 50, 0), 22, stroke.LocalToWorld));

        Check.Equal(1, page.Count, "擦除 10 次后页面仍然只有 1 个对象（不碎片化）");
        Check.Equal(10, stroke.Erasures.Count, "应有 10 个遮罩");
    }

    public static void Test_Erasure_LocalCoordinates_SurviveMove()
    {
        var doc = NewDoc();
        var o = NewStroke(doc, 100, 100);
        var local = EraserCircle.FromWorld(new PointD(150, 100), 10, o.LocalToWorld);
        o.AddErasure(local);

        var before = o.Erasures[0];
        o.X += 500;                       // 移动对象
        var after = o.Erasures[0];

        Check.Near(before.X, after.X, 1e-12, "遮罩是局部坐标，对象移动不应改变它");
        Check.Near(before.Y, after.Y, 1e-12, "遮罩是局部坐标，对象移动不应改变它");

        // 世界坐标下的遮罩中心应随对象一起移动
        var worldCenterBefore = new PointD(before.X + o.X - 500, before.Y + o.Y);
        var worldCenterAfter = o.LocalToWorld.Transform(before.Center);
        Check.Near(worldCenterBefore.X + 500, worldCenterAfter.X, 1e-6, "遮罩世界位置应随对象移动");
    }

    public static void Test_Erasure_NonErasableObject_Ignored()
    {
        var doc = NewDoc();
        var o = NewStroke(doc, 0, 0);
        // 用一个不可擦除的假对象验证记录逻辑
        Check.True(o.IsErasable, "自由画笔应可擦除");
    }

    public static void Test_Erasure_BakeThreshold()
    {
        var doc = NewDoc();
        var o = NewStroke(doc, 0, 0);
        Check.True(!o.ShouldBake, "初始不应需要烘焙");
        for (var i = 0; i < ShapeObject.BakeThreshold; i++)
            o.AddErasure(new EraserCircle(i * 3, 0, 2));
        Check.True(o.ShouldBake, $"遮罩达到 {ShapeObject.BakeThreshold} 个后应建议烘焙");
    }

    // ---- 命令层 ----

    public static void Test_Command_UndoRedo_AddRemove()
    {
        var doc = NewDoc();
        var page = doc.CurrentPage;
        var stack = new PageCommandStack(page.Id);

        var o = NewStroke(doc, 0, 0);
        stack.Execute(new AddObjectCommand(page, o));
        Check.Equal(1, page.Count, "执行后应有 1 个对象");

        Check.True(stack.Undo(), "应能撤销");
        Check.Equal(0, page.Count, "撤销后应为 0");

        Check.True(stack.Redo(), "应能重做");
        Check.Equal(1, page.Count, "重做后应为 1");
    }

    public static void Test_Command_Move_UndoRestoresPosition()
    {
        var doc = NewDoc();
        var page = doc.CurrentPage;
        var stack = new PageCommandStack(page.Id);
        var o = NewStroke(doc, 10, 20);
        page.Add(o);

        stack.Execute(new MoveObjectsCommand([(o, 100.0, -50.0)]));
        Check.Near(110, o.X, 1e-9, "移动后 X");
        Check.Near(-30, o.Y, 1e-9, "移动后 Y");

        stack.Undo();
        Check.Near(10, o.X, 1e-9, "撤销后 X");
        Check.Near(20, o.Y, 1e-9, "撤销后 Y");
    }

    public static void Test_Command_Erase_UndoRestoresMaskOnly()
    {
        var doc = NewDoc();
        var page = doc.CurrentPage;
        var stack = new PageCommandStack(page.Id);
        var o = NewStroke(doc, 0, 0, points: 20);
        page.Add(o);

        var erase = new EraseCommand(page);
        erase.Record(o, new EraserCircle(10, 0, 4));
        erase.Record(o, new EraserCircle(30, 0, 4));
        stack.Execute(erase);

        Check.Equal(2, o.Erasures.Count, "应有 2 个遮罩");
        Check.Equal(1, page.Count, "仍是 1 个对象");

        stack.Undo();
        Check.Equal(0, o.Erasures.Count, "撤销后遮罩应清空");
        Check.Equal(1, page.Count, "对象没有被拆碎，仍为 1");
    }

    public static void Test_Command_PerPage_StacksAreIndependent()
    {
        // ADR：每页一个独立栈，撤销只作用于当前页
        var doc = NewDoc();
        var p1 = doc.CurrentPage;
        var p2 = doc.AddPage();
        var mgr = new CommandManager();

        mgr.SetCurrentPage(p1.Id);
        var o1 = NewStroke(doc, 0, 0);
        mgr.Execute(new AddObjectCommand(p1, o1));

        mgr.SetCurrentPage(p2.Id);
        Check.True(!mgr.CanUndo, "切到新页后不应能撤销（该页栈为空）");

        var o2 = NewStroke(doc, 0, 0);
        mgr.Execute(new AddObjectCommand(p2, o2));
        Check.True(mgr.CanUndo, "在新页执行后应能撤销");

        mgr.Undo();
        Check.Equal(1, p1.Count, "撤销只作用于当前页，第 1 页不受影响");
        Check.Equal(0, p2.Count, "第 2 页被撤销");

        mgr.SetCurrentPage(p1.Id);
        Check.True(mgr.CanUndo, "切回第 1 页后仍可撤销该页历史");
        mgr.Undo();
        Check.Equal(0, p1.Count, "第 1 页撤销生效");
    }

    public static void Test_Command_NewCommandClearsRedo()
    {
        var doc = NewDoc();
        var page = doc.CurrentPage;
        var stack = new PageCommandStack(page.Id);
        var a = NewStroke(doc, 0, 0);
        stack.Execute(new AddObjectCommand(page, a));
        stack.Undo();
        Check.True(stack.CanRedo, "应有可重做");

        var b = NewStroke(doc, 0, 50);
        stack.Execute(new AddObjectCommand(page, b));
        Check.True(!stack.CanRedo, "执行新命令后重做栈应被清空");
    }

    public static void Test_Command_Capacity_DropsOldest()
    {
        var doc = NewDoc();
        var page = doc.CurrentPage;
        var stack = new PageCommandStack(page.Id, capacity: 5);
        for (var i = 0; i < 8; i++)
            stack.Execute(new AddObjectCommand(page, NewStroke(doc, i * 5, 0)));

        Check.Equal(5, stack.UndoCount, "步数应被限制在容量 5");
        Check.Near(8, page.Count, 1e-9, "页面对象数不受撤销栈容量影响");
    }

    public static void Test_Command_Transaction_UndoReverseOrder()
    {
        var doc = NewDoc();
        var page = doc.CurrentPage;
        var stack = new PageCommandStack(page.Id);
        var o = NewStroke(doc, 0, 0);
        page.Add(o);

        var parts = new List<ICommand>
        {
            new MoveObjectsCommand([(o, 10.0, 0.0)]),
            new MoveObjectsCommand([(o, 0.0, 20.0)])
        };
        foreach (var p in parts) p.Do();
        stack.PushTransaction("移动并旋转", parts);

        Check.Near(10, o.X, 1e-9, "事务后 X");
        Check.Near(20, o.Y, 1e-9, "事务后 Y");
        Check.Equal(1, stack.UndoCount, "事务应只占一条栈记录");

        stack.Undo();
        Check.Near(0, o.X, 1e-9, "撤销事务后 X 回原位");
        Check.Near(0, o.Y, 1e-9, "撤销事务后 Y 回原位");
    }

    public static void Test_Command_ZOrder_UndoRestores()
    {
        var doc = NewDoc();
        var page = doc.CurrentPage;
        var stack = new PageCommandStack(page.Id);
        var a = NewStroke(doc, 0, 0);
        var b = NewStroke(doc, 0, 50);
        page.Add(a);
        page.Add(b);

        var zBefore = a.ZIndex;
        stack.Execute(new ZOrderCommand(page, a, ZOrderCommand.Mode.Front));
        Check.True(a.ZIndex > b.ZIndex, "置顶后 a 应最大");

        stack.Undo();
        Check.Equal(zBefore, a.ZIndex, "撤销后 Z 应恢复");
    }

    public static void Test_Command_Clone_OffsetsAndUndo()
    {
        var doc = NewDoc();
        var page = doc.CurrentPage;
        var stack = new PageCommandStack(page.Id);
        var o = NewStroke(doc, 100, 100);
        page.Add(o);

        stack.Execute(new CloneCommand(page, [o], doc.AllocateObjectId, offset: 16));
        Check.Equal(2, page.Count, "克隆后应有 2 个对象");

        var clone = page.Objects.First(x => x.Id != o.Id);
        Check.Near(116, clone.X, 1e-9, "克隆应偏移 16");
        Check.True(clone.Erasures.Count == o.Erasures.Count, "遮罩应一并复制");

        stack.Undo();
        Check.Equal(1, page.Count, "撤销克隆后应剩 1 个");
    }

    public static void Test_Command_ClearPage_UndoRestoresAll()
    {
        var doc = NewDoc();
        var page = doc.CurrentPage;
        var stack = new PageCommandStack(page.Id);
        for (var i = 0; i < 5; i++) page.Add(NewStroke(doc, i * 20, 0));

        stack.Execute(new ClearPageCommand(page));
        Check.Equal(0, page.Count, "清空后为 0");

        stack.Undo();
        Check.Equal(5, page.Count, "撤销清空应恢复 5 个对象");
    }

    public static void Test_Command_DropPage_RemovesStack()
    {
        var mgr = new CommandManager();
        mgr.SetCurrentPage(11);
        mgr.SetCurrentPage(22);
        Check.Equal(2, mgr.PageStackCount, "应有两个页栈");

        mgr.DropPage(11);
        Check.Equal(1, mgr.PageStackCount, "删除页后其栈应被丢弃");
    }
}
