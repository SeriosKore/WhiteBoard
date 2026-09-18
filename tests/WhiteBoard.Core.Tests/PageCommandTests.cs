using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Core.Tests;

/// <summary>
/// 页面级命令：新建 / 复制 / 删除 / 换序 / 跨页搬运。
///
/// 这些用例集中盯两件事：
/// <list type="number">
/// <item>**撤销是否真能还原**（页数、页序、当前页、内容、Z 序）；</item>
/// <item>**文档"至少 1 页"的约束**不能被这些操作破坏。</item>
/// </list>
/// </summary>
public static class PageCommandTests
{
    private static WhiteboardDocument NewDoc(int pages = 1)
    {
        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();
        for (var i = 1; i < pages; i++) doc.AddPage();
        doc.CurrentPageIndex = 0;
        return doc;
    }

    private static FreehandObject Stroke(WhiteboardDocument doc, double x = 100, double y = 100)
        => FreehandObject.FromWorldPoints(doc.AllocateObjectId(),
            [new InkPoint(x, y, 0.5f), new InkPoint(x + 50, y + 20, 0.5f)], "#F5F5F0", 6);

    private static string PageIds(WhiteboardDocument doc) => string.Join(",", doc.Pages.Select(p => p.Id));

    // ── 新建页 ────────────────────────────────────────────────────────────

    public static void Test_AddPage_AppendsAndSwitchesToIt()
    {
        var doc = NewDoc();
        var cmd = new AddPageCommand(doc);
        cmd.Do();

        Check.Equal(2, doc.Pages.Count, "应有两页");
        Check.Equal(1, doc.CurrentPageIndex, "应切到新页");
        Check.True(cmd.Page is not null, "命令应暴露新建的页");
        Check.Equal(0, doc.Pages[1].Count, "新页应为空");
    }

    /// <summary>
    /// 关键行为："新建页 → 在上面画 → 撤销 → 重做"，画的内容必须一起回来。
    /// 如果命令每次 Apply 都新建一个 Page 对象，重做得到的就是空页——用户的画"凭空消失"。
    /// </summary>
    public static void Test_AddPage_RedoRestoresContentDrawnOnThatPage()
    {
        var doc = NewDoc();
        var cmd = new AddPageCommand(doc);
        cmd.Do();

        var page = cmd.Page!;
        page.Add(Stroke(doc));
        Check.Equal(1, page.Count, "在新页上画一笔");

        cmd.Undo();
        Check.Equal(1, doc.Pages.Count, "撤销后回到 1 页");

        cmd.Do();
        Check.Equal(2, doc.Pages.Count, "重做后又是 2 页");
        Check.True(ReferenceEquals(page, doc.Pages[1]), "重做应复用同一页对象");
        Check.Equal(1, doc.Pages[1].Count, "画的内容应还在");
    }

    public static void Test_AddPage_InsertsAtRequestedIndex()
    {
        var doc = NewDoc(3);
        var first = doc.Pages[0];
        var last = doc.Pages[2];

        new AddPageCommand(doc, index: 1).Do();

        Check.Equal(4, doc.Pages.Count, "应插入一页");
        Check.True(ReferenceEquals(first, doc.Pages[0]), "原第 1 页仍在最前");
        Check.True(ReferenceEquals(last, doc.Pages[3]), "原第 3 页被推到第 4 位");
        Check.Equal(1, doc.CurrentPageIndex, "应切到新插入的页");
    }

    // ── 复制页 ────────────────────────────────────────────────────────────

    public static void Test_DuplicatePage_DeepCopiesContentAndViewport()
    {
        var doc = NewDoc();
        var src = doc.Pages[0];
        src.Add(Stroke(doc, 10, 20));
        src.Viewport.Zoom = 2.5;
        src.Viewport.PanX = -7;
        src.AddErasureToLastStroke();

        var cmd = new DuplicatePageCommand(doc, src);
        cmd.Do();

        Check.Equal(2, doc.Pages.Count, "应有两页");
        Check.Equal(1, doc.CurrentPageIndex, "应切到副本");
        var copy = doc.Pages[1];

        Check.Equal(src.Count, copy.Count, "对象数应一致");
        Check.Near(src.Viewport.Zoom, copy.Viewport.Zoom, 1e-9, "视口缩放应复制");
        Check.Near(src.Viewport.PanX, copy.Viewport.PanX, 1e-9, "视口平移应复制");

        Check.True(!ReferenceEquals(src.Objects[0], copy.Objects[0]), "必须是深拷贝");
        Check.True(src.Objects[0].Id != copy.Objects[0].Id, "副本应有新的对象 Id");
        Check.Equal(((FreehandObject)src.Objects[0]).Erasures.Count,
            ((FreehandObject)copy.Objects[0]).Erasures.Count, "擦除遮罩应一起复制");

        // 改副本不应影响源页
        copy.Objects[0].X += 100;
        Check.True(Math.Abs(src.Objects[0].X - copy.Objects[0].X) > 1, "改副本不应影响源页");
    }

    public static void Test_DuplicatePage_UndoRemovesCopyAndRestoresIndex()
    {
        var doc = NewDoc(2);
        doc.CurrentPageIndex = 0;
        var cmd = new DuplicatePageCommand(doc, doc.Pages[0]);
        cmd.Do();
        Check.Equal(3, doc.Pages.Count, "应有 3 页");

        cmd.Undo();
        Check.Equal(2, doc.Pages.Count, "撤销后回到 2 页");
        Check.Equal(0, doc.CurrentPageIndex, "当前页索引应还原");
    }

    // ── 删除页 ────────────────────────────────────────────────────────────

    public static void Test_RemovePage_UndoPutsPageBackWithContent()
    {
        var doc = NewDoc(4);
        var victim = doc.Pages[1];
        victim.Add(Stroke(doc));
        victim.Add(Stroke(doc, 200, 200));

        var idsBefore = PageIds(doc);
        var cmd = new RemovePageCommand(doc, victim);
        cmd.Do();

        Check.Equal(3, doc.Pages.Count, "应少一页");
        Check.True(!doc.Pages.Contains(victim), "被删页不应还在文档里");

        cmd.Undo();
        Check.Equal(4, doc.Pages.Count, "撤销后回来");
        Check.Equal(idsBefore, PageIds(doc), "页序应完全还原");
        Check.Equal(2, doc.Pages[1].Count, "整页内容应一起回来");
    }

    /// <summary>删掉最后一页时，当前页索引要往前挪，不能指向越界位置。</summary>
    public static void Test_RemovePage_LastPageAdjustsCurrentIndex()
    {
        var doc = NewDoc(3);
        doc.CurrentPageIndex = 2;

        new RemovePageCommand(doc, doc.Pages[2]).Do();

        Check.Equal(2, doc.Pages.Count, "应剩 2 页");
        Check.Equal(1, doc.CurrentPageIndex, "当前页应往前挪到最后一页");
    }

    /// <summary>**文档约束**：删除最后一页必须被拒绝，而不是悄悄补一个空白页。</summary>
    public static void Test_RemovePage_RefusesToDeleteTheOnlyPage()
    {
        var doc = NewDoc();
        var cmd = new RemovePageCommand(doc, doc.Pages[0]);
        cmd.Do();

        Check.Equal(1, doc.Pages.Count, "仍应保留 1 页");
        Check.True(cmd.Refused, "命令应报告被拒绝");
        Check.True(ReferenceEquals(cmd.Page, doc.Pages[0]), "保留的应是原来那一页");
    }

    /// <summary>删除中间页之后撤销，当前页索引不应乱跳（用户会"不知道自己在第几页"）。</summary>
    public static void Test_RemovePage_KeepsSamePageSelectedWhenPossible()
    {
        var doc = NewDoc(4);
        var third = doc.Pages[2];
        doc.CurrentPageIndex = 2;   // 停在第 3 页

        var cmd = new RemovePageCommand(doc, doc.Pages[0]);
        cmd.Do();

        // 删掉第 1 页后，原来的第 3 页变成第 2 页
        Check.True(ReferenceEquals(third, doc.CurrentPage), "应仍然停在同一页对象上");

        cmd.Undo();
        Check.True(ReferenceEquals(third, doc.CurrentPage), "撤销后仍应停在同一页");
    }

    // ── 换序 ──────────────────────────────────────────────────────────────

    public static void Test_MovePage_ReordersAndUndoes()
    {
        var doc = NewDoc(4);
        var before = PageIds(doc);
        var moved = doc.Pages[0];

        var cmd = new MovePageCommand(doc, moved, 2);
        cmd.Do();

        Check.True(ReferenceEquals(moved, doc.Pages[2]), "应被移到索引 2");
        Check.True(PageIds(doc) != before, "顺序应真的变了");

        cmd.Undo();
        Check.Equal(before, PageIds(doc), "撤销后顺序应完全还原");
    }

    // ── 跨页搬运 ──────────────────────────────────────────────────────────

    public static void Test_MoveObjectsToPage_KeepsWorldPositionAndUndoes()
    {
        var doc = NewDoc(2);
        var a = doc.Pages[0];
        var b = doc.Pages[1];

        var moved = Stroke(doc, 300, 400);
        a.Add(moved);
        var zBefore = moved.ZIndex;
        var xBefore = moved.X;
        var yBefore = moved.Y;

        var cmd = new MoveObjectsToPageCommand(doc, a, b, [moved]);
        cmd.Do();

        Check.Equal(0, a.Count, "源页应空了");
        Check.Equal(1, b.Count, "目标页应有 1 个对象");
        Check.Near(xBefore, moved.X, 1e-9, "世界坐标 X 应原样保留");
        Check.Near(yBefore, moved.Y, 1e-9, "世界坐标 Y 应原样保留");
        Check.True(moved.ZIndex > zBefore || b.Count == 1, "目标页里的 Z 序应重新排到上层");

        cmd.Undo();
        Check.Equal(1, a.Count, "撤销后应回到源页");
        Check.Equal(0, b.Count, "目标页应恢复为空");
        Check.Equal(zBefore, moved.ZIndex, "Z 序应还原");
    }

    /// <summary>搬到目标页后必须**看得见**：不能被目标页已有内容盖住（所以 Z 排到最上层）。</summary>
    public static void Test_MoveObjectsToPage_PlacesOnTopOfTargetPage()
    {
        var doc = NewDoc(2);
        var a = doc.Pages[0];
        var b = doc.Pages[1];

        for (var i = 0; i < 3; i++) b.Add(Stroke(doc, i * 10, i * 10));

        var moved = Stroke(doc, 500, 500);
        a.Add(moved);

        new MoveObjectsToPageCommand(doc, a, b, [moved]).Do();

        Check.Equal(4, b.Count, "目标页应有 4 个对象");
        Check.True(ReferenceEquals(b.InRenderOrder().Last(), moved),
            "搬过来的对象应排在渲染顺序的最后（最上层）");
    }

    public static void Test_MoveObjectsToPage_MultipleObjects()
    {
        var doc = NewDoc(2);
        var a = doc.Pages[0];
        var b = doc.Pages[1];

        var s1 = Stroke(doc, 10, 10);
        var s2 = Stroke(doc, 20, 20);
        var stay = Stroke(doc, 30, 30);
        a.Add(s1);
        a.Add(s2);
        a.Add(stay);

        var cmd = new MoveObjectsToPageCommand(doc, a, b, [s1, s2]);
        cmd.Do();

        Check.Equal(1, a.Count, "源页应只剩没被选中的那个");
        Check.Equal(2, b.Count, "目标页应有 2 个");
        Check.True(ReferenceEquals(a.Objects[0], stay), "留下的应是没被选中的那个");

        cmd.Undo();
        Check.Equal(3, a.Count, "撤销后 3 个都在源页");
        Check.Equal(0, b.Count, "目标页应为空");
    }

    /// <summary>搬运之后原来的页 Id 关系不变——页 Id 不因搬运而重新分配。</summary>
    public static void Test_MoveObjectsToPage_DoesNotDisturbPageIds()
    {
        var doc = NewDoc(3);
        var idsBefore = PageIds(doc);

        var obj = Stroke(doc);
        doc.Pages[0].Add(obj);
        new MoveObjectsToPageCommand(doc, doc.Pages[0], doc.Pages[2], [obj]).Do();

        Check.Equal(idsBefore, PageIds(doc), "页 Id 与页序都不应变");
        Check.Near(1.0, doc.Pages[0].Viewport.Zoom, 1e-9, "视口不应被改动");
    }
}

/// <summary>测试辅助：给页面上最后一个笔迹加一个擦除遮罩（保持用例简短）。</summary>
internal static class PageCommandTestHelpers
{
    public static void AddErasureToLastStroke(this Page page)
    {
        if (page.Objects.Count == 0) return;
        if (page.Objects[^1] is FreehandObject f && f.Points.Count > 0)
            f.AddErasure(new EraserCircle(f.Points[0].X, f.Points[0].Y, 2));
    }
}
