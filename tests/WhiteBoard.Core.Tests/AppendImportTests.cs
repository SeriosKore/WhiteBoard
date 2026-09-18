using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;
using WhiteBoard.Core.Storage;

namespace WhiteBoard.Core.Tests;

/// <summary>
/// 追加导入：把另一份画板的页并进当前文档。
///
/// 重点盯三件事：**Id 不能撞号**、**必须是深拷贝**（不许改到源文档）、**一次可撤销**。
/// </summary>
public static class AppendImportTests
{
    private static FreehandObject Stroke(WhiteboardDocument doc, double x, double y)
        => FreehandObject.FromWorldPoints(doc.AllocateObjectId(),
            [new InkPoint(x, y, 0.5f), new InkPoint(x + 40, y + 10, 0.5f)], "#F5F5F0", 6);

    private static WhiteboardDocument SourceDoc(int pages, int objectsPerPage)
    {
        var doc = new WhiteboardDocument { Id = 99 };
        doc.EnsureAtLeastOnePage();
        for (var i = 1; i < pages; i++) doc.AddPage();

        for (var i = 0; i < doc.Pages.Count; i++)
        {
            doc.Pages[i].Viewport.Zoom = 1 + i * 0.5;
            for (var k = 0; k < objectsPerPage; k++)
                doc.Pages[i].Add(Stroke(doc, i * 100 + k * 10, i * 50 + k * 10));
        }

        doc.CurrentPageIndex = 0;
        doc.RebuildIdCounters();
        return doc;
    }

    public static void Test_Append_AddsPagesAtEndAndSwitchesToFirstImported()
    {
        var target = new WhiteboardDocument { Id = 1 };
        target.EnsureAtLeastOnePage();
        target.Pages[0].Add(Stroke(target, 0, 0));

        var source = SourceDoc(pages: 2, objectsPerPage: 3);

        var cmd = new AppendDocumentCommand(target, source);
        cmd.Do();

        Check.Equal(3, target.Pages.Count, "应变成 3 页（原 1 + 导入 2）");
        Check.Equal(1, target.CurrentPageIndex, "应切到第一张导入的页");
        Check.Equal(3, target.Pages[1].Count, "导入的页应带着内容");
        Check.Equal(3, target.Pages[2].Count, "第二张导入页也应有内容");
        Check.Equal(2, cmd.AddedPages.Count, "命令应报告追加了 2 页");
    }

    /// <summary>页 Id 与对象 Id 必须**全部重分配**，不能与目标文档撞号。</summary>
    public static void Test_Append_ReassignsAllIdsWithoutCollisions()
    {
        var target = new WhiteboardDocument { Id = 1 };
        target.EnsureAtLeastOnePage();
        for (var i = 0; i < 3; i++) target.Pages[0].Add(Stroke(target, i * 10, 0));
        target.RebuildIdCounters();

        var source = SourceDoc(pages: 2, objectsPerPage: 4);
        new AppendDocumentCommand(target, source).Do();

        var pageIds = target.Pages.Select(p => p.Id).ToList();
        Check.Equal(pageIds.Count, pageIds.Distinct().Count(), "页 Id 不应重复");

        var objIds = target.Pages.SelectMany(p => p.Objects).Select(o => o.Id).ToList();
        Check.Equal(objIds.Count, objIds.Distinct().Count(), $"对象 Id 不应重复，实际 {objIds.Count} 个");

        // 新分配的对象 Id 必须大于原有的最大值，避免下一次分配又撞上
        Check.True(objIds.Max() < target.NextObjectId, "Id 分配器应已推进到最大值之后");
    }

    /// <summary>必须是深拷贝：改目标文档不能影响源文档（源是"别的文件"）。</summary>
    public static void Test_Append_DeepCopiesAndDoesNotTouchSource()
    {
        var target = new WhiteboardDocument { Id = 1 };
        target.EnsureAtLeastOnePage();

        var source = SourceDoc(pages: 1, objectsPerPage: 2);
        var srcObj = source.Pages[0].Objects[0];
        var srcX = srcObj.X;
        var srcCount = source.Pages[0].Count;

        new AppendDocumentCommand(target, source).Do();

        var imported = target.Pages[1].Objects[0];
        Check.True(!ReferenceEquals(srcObj, imported), "导入的应是副本，不是同一个对象");

        imported.X += 500;
        Check.Near(srcX, srcObj.X, 1e-9, "改副本不应影响源文档");
        Check.Equal(srcCount, source.Pages[0].Count, "源文档页内对象数不应变化");
        Check.Equal(1, source.Pages.Count, "源文档页数不应变化");
    }

    public static void Test_Append_UndoRemovesEverythingAndRedoBringsItBack()
    {
        var target = new WhiteboardDocument { Id = 1 };
        target.EnsureAtLeastOnePage();
        var source = SourceDoc(pages: 3, objectsPerPage: 2);

        var cmd = new AppendDocumentCommand(target, source);
        cmd.Do();
        Check.Equal(4, target.Pages.Count, "应变成 4 页");
        Check.Equal(0, target.CurrentPageIndex - 1, "应停在第 2 页（第一张导入页）");

        cmd.Undo();
        Check.Equal(1, target.Pages.Count, "撤销后回到 1 页");
        Check.Equal(0, target.CurrentPageIndex, "当前页应还原");

        cmd.Do();
        Check.Equal(4, target.Pages.Count, "重做后又是 4 页");
        Check.Equal(2, target.Pages[1].Count, "重做后内容应还在（复用同一批页对象）");
    }

    public static void Test_Append_EmptySourceDoesNothing()
    {
        var target = new WhiteboardDocument { Id = 1 };
        target.EnsureAtLeastOnePage();
        var source = new WhiteboardDocument { Id = 2 };   // 一页都没有

        var cmd = new AppendDocumentCommand(target, source);
        Check.True(cmd.NothingToDo, "空文档应报告无事可做");

        cmd.Do();
        Check.Equal(1, target.Pages.Count, "页数不应变化");
        Check.Equal(0, cmd.AddedPages.Count, "不应有追加页");
    }

    /// <summary>追加导入与视口：导入页各自的缩放/平移应一并带过来（老师导入的页保持原样）。</summary>
    public static void Test_Append_PreservesPerPageViewport()
    {
        var target = new WhiteboardDocument { Id = 1 };
        target.EnsureAtLeastOnePage();

        var source = SourceDoc(pages: 3, objectsPerPage: 1);
        var expected = source.Pages.Select(p => p.Viewport.Zoom).ToList();

        new AppendDocumentCommand(target, source).Do();

        for (var i = 0; i < expected.Count; i++)
            Check.Near(expected[i], target.Pages[1 + i].Viewport.Zoom, 1e-9, $"第 {i + 1} 张导入页的缩放应保留");
    }

    /// <summary>
    /// 端到端：把一份真实保存出来的 <c>.wb</c> 追加进另一份文档——
    /// 这正是 UI 上「追加导入」按钮走的路。
    /// </summary>
    public static void Test_Append_FromRealWbFileRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-append-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "课件片段.wb");

        var source = SourceDoc(pages: 2, objectsPerPage: 3);
        source.Pages[0].Objects[0].AddErasure(new EraserCircle(5, 5, 2));
        WbPackage.Save(source, file);

        var loaded = WbPackage.Load(file).Document;

        var target = new WhiteboardDocument { Id = 1 };
        target.EnsureAtLeastOnePage();
        target.Pages[0].Add(Stroke(target, 0, 0));

        var cmd = new AppendDocumentCommand(target, loaded);
        cmd.Do();

        Check.Equal(3, target.Pages.Count, "应导入 2 页");
        Check.Equal(3, target.Pages[1].Count, "导入页应带内容");
        Check.Equal(1, target.Pages[1].Objects.Count(o => o.Erasures.Count > 0),
            "擦除遮罩也应一起导入（导入的不是「干净的副本」）");

        // 导入后的文档再存一次，仍应是合法画板
        var round = Path.Combine(dir, "合并后.wb");
        WbPackage.Save(target, round);
        var reopened = WbPackage.Load(round).Document;
        Check.Equal(3, reopened.Pages.Count, "合并后的文档应能正常保存与打开");
        Check.Equal(target.Pages.Sum(p => p.Count), reopened.Pages.Sum(p => p.Count), "对象总数应一致");

        Directory.Delete(dir, true);
    }
}
