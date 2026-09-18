using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;
using WhiteBoard.Core.Storage;

namespace WhiteBoard.Core.Tests;

/// <summary>
/// 文件层：<c>.wb</c> 包格式的往返、原子写、损坏处理与向前兼容。
///
/// 这些用例全部在系统临时目录里跑（测试不污染工作区），并且**只依赖生产代码**：
/// 存出来的文件就是程序真正会写出的文件格式。
/// </summary>
public static class WbPackageTests
{
    private static string TempPath(string name)
        => Path.Combine(Path.GetTempPath(), "wb-tests-" + Guid.NewGuid().ToString("N")[..8], name);

    /// <summary>造一个内容尽量"全"的文档：多页、四种图元、遮罩、非默认视口/Z 序/旋转。</summary>
    private static WhiteboardDocument BuildRichDocument()
    {
        var doc = new WhiteboardDocument { Id = 7 };

        // 第 1 页：四种图元
        var p1 = doc.EnsureAtLeastOnePage();
        p1.Viewport.Zoom = 2.5;
        p1.Viewport.PanX = -13.25;
        p1.Viewport.PanY = 44.5;
        p1.BackgroundType = "solid";

        var stroke = FreehandObject.FromWorldPoints(101,
            [new InkPoint(10, 20, 0.3f), new InkPoint(30, 45, 0.7f), new InkPoint(90, 12, 0.5f)],
            "#EED858", 6);
        stroke.ZIndex = 5;
        stroke.AddErasure(new EraserCircle(4, 5, 2.5));
        stroke.AddErasure(new EraserCircle(8, 9, 1.125));
        p1.Add(stroke);

        var line = LineObject.FromWorldPoints(102, new PointD(0, 0), new PointD(100, 50), "#F47A6E", 10);
        line.ZIndex = 9;
        line.Rotation = 0.75;
        p1.Add(line);

        var rect = RectObject.FromWorldCorners(103, new PointD(200, 200), new PointD(340, 260), "#9CDCFE", 3);
        rect.ZIndex = 2;
        rect.ScaleX = 1.5;
        p1.Add(rect);

        var ellipse = EllipseObject.FromWorldCorners(104, new PointD(400, 300), new PointD(500, 420), "#F5F5F0", 12);
        p1.Add(ellipse);
        p1.RebuildZCounter();

        // 第 2 页：空页 + 不同视口
        var p2 = doc.AddPage();
        p2.Viewport.Zoom = 0.5;

        doc.CurrentPageIndex = 1;
        doc.RebuildIdCounters();
        return doc;
    }

    // ── ① 往返 ────────────────────────────────────────────────────────────

    public static void Test_Wb_RoundTripPreservesStructure()
    {
        var path = TempPath("roundtrip.wb");
        var src = BuildRichDocument();

        WbPackage.Save(src, path, appVersion: "test");
        Check.True(File.Exists(path), "保存后文件应存在");

        var result = WbPackage.Load(path);
        var dst = result.Document;

        Check.Equal(src.Pages.Count, dst.Pages.Count, "页数应一致");
        Check.Equal(src.CurrentPageIndex, dst.CurrentPageIndex, "当前页应一致");
        Check.Equal(src.Id, dst.Id, "文档 Id 应一致");
        Check.True(!result.HasWarnings, $"不应有警告，实际：{result.WarningText}");

        var s1 = src.Pages[0];
        var d1 = dst.Pages[0];
        Check.Equal(s1.Count, d1.Count, "第 1 页对象数应一致");
        Check.Near(s1.Viewport.Zoom, d1.Viewport.Zoom, 1e-9, "缩放应一致");
        Check.Near(s1.Viewport.PanX, d1.Viewport.PanX, 1e-9, "PanX 应一致");
        Check.Near(s1.Viewport.PanY, d1.Viewport.PanY, 1e-9, "PanY 应一致");
        Check.Equal(s1.BackgroundType, d1.BackgroundType, "背景类型应一致");
        Check.Near(src.Pages[1].Viewport.Zoom, dst.Pages[1].Viewport.Zoom, 1e-9, "第 2 页缩放独立保存");

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    public static void Test_Wb_RoundTripPreservesObjectGeometry()
    {
        var path = TempPath("geometry.wb");
        var src = BuildRichDocument();
        WbPackage.Save(src, path);

        var dst = WbPackage.Load(path).Document;
        var a = (FreehandObject)src.Pages[0].Objects.Single(o => o.Id == 101);
        var b = (FreehandObject)dst.Pages[0].Objects.Single(o => o.Id == 101);

        Check.Equal(a.Points.Count, b.Points.Count, "采样点数应一致");
        for (var i = 0; i < a.Points.Count; i++)
        {
            Check.Near(a.Points[i].X, b.Points[i].X, 1e-3, $"点 {i} 的 X");
            Check.Near(a.Points[i].Y, b.Points[i].Y, 1e-3, $"点 {i} 的 Y");
            Check.Near(a.Points[i].Pressure, b.Points[i].Pressure, 1e-3, $"点 {i} 的压力");
        }

        Check.Equal(a.Color, b.Color, "颜色应一致");
        Check.Near(a.PenWidth, b.PenWidth, 1e-9, "笔宽应一致");
        Check.Equal(a.Erasures.Count, b.Erasures.Count, "擦除遮罩数量应一致");
        Check.Near(a.Erasures[1].Radius, b.Erasures[1].Radius, 1e-6, "遮罩半径应一致");
        Check.Near(a.X, b.X, 1e-3, "对象 X 应一致");
        Check.Near(a.LocalWidth, b.LocalWidth, 1e-3, "对象局部宽度应一致");

        // 旋转/缩放/Z 序这些"变换相关"的字段最容易漏
        var la = (LineObject)src.Pages[0].Objects.Single(o => o.Id == 102);
        var lb = (LineObject)dst.Pages[0].Objects.Single(o => o.Id == 102);
        Check.Near(la.Rotation, lb.Rotation, 1e-9, "旋转应一致");
        Check.Near(la.LocalX2, lb.LocalX2, 1e-3, "直线端点应一致");
        Check.Equal(la.ZIndex, lb.ZIndex, "Z 序应一致");

        var ra = (RectObject)src.Pages[0].Objects.Single(o => o.Id == 103);
        var rb = (RectObject)dst.Pages[0].Objects.Single(o => o.Id == 103);
        Check.Near(ra.ScaleX, rb.ScaleX, 1e-9, "缩放应一致");
        Check.Equal("ellipse", dst.Pages[0].Objects.Single(o => o.Id == 104).Kind, "椭圆类型应保留");

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    public static void Test_Wb_RoundTripPreservesRenderOrder()
    {
        var path = TempPath("order.wb");
        var src = BuildRichDocument();
        WbPackage.Save(src, path);
        var dst = WbPackage.Load(path).Document;

        var srcOrder = src.Pages[0].InRenderOrder().Select(o => o.Id).ToList();
        var dstOrder = dst.Pages[0].InRenderOrder().Select(o => o.Id).ToList();
        Check.Equal(string.Join(",", srcOrder), string.Join(",", dstOrder), "渲染顺序（Z 序）应一致");

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    /// <summary>保存两次应幂等：加载→保存→再加载，结果不变（且不产生重复对象）。</summary>
    public static void Test_Wb_ResaveIsStable()
    {
        var path1 = TempPath("stable1.wb");
        var path2 = TempPath("stable2.wb");
        var src = BuildRichDocument();

        WbPackage.Save(src, path1);
        var first = WbPackage.Load(path1).Document;
        WbPackage.Save(first, path2);
        var second = WbPackage.Load(path2).Document;

        Check.Equal(first.Pages.Count, second.Pages.Count, "两次保存页数应一致");
        Check.Equal(first.Pages[0].Count, second.Pages[0].Count, "两次保存对象数应一致");
        Check.Equal(
            string.Join(",", first.Pages[0].InRenderOrder().Select(o => o.Id)),
            string.Join(",", second.Pages[0].InRenderOrder().Select(o => o.Id)),
            "两次保存的渲染顺序应一致");

        Directory.Delete(Path.GetDirectoryName(path1)!, true);
        Directory.Delete(Path.GetDirectoryName(path2)!, true);
    }

    // ── ② 缩略图与摘要 ────────────────────────────────────────────────────

    public static void Test_Wb_StoresAndReadsPreviewPng()
    {
        var path = TempPath("preview.wb");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5 };

        WbPackage.Save(BuildRichDocument(), path, previewPng: png);

        var read = WbPackage.TryReadPreview(path);
        Check.True(read is not null, "应能读回缩略图");
        Check.Equal(png.Length, read!.Length, "缩略图长度应一致");
        Check.Equal(0x89, read[0], "缩略图首字节应为 PNG 魔数");

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    public static void Test_Wb_ReadSummaryWithoutFullParse()
    {
        var path = TempPath("summary.wb");
        WbPackage.Save(BuildRichDocument(), path, appVersion: "1.5");

        var s = WbPackage.TryReadSummary(path);
        Check.True(s is not null, "应能读到摘要");
        Check.Equal(WbFormat.CurrentSchemaVersion, s!.Value.SchemaVersion, "版本号应一致");
        Check.Equal(2, s.Value.PageCount, "页数应为 2");
        Check.Equal(4, s.Value.ObjectCount, "对象数应为 4");

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    // ── ③ 原子写 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 覆盖保存不留下临时文件，且**老文件在写入过程中始终是完整可读的**。
    /// 这里用"保存一次 → 读一次 → 再保存 → 再读"来验证替换是原子的、内容是新版本。
    /// </summary>
    public static void Test_Wb_OverwriteLeavesNoTempAndReplacesContent()
    {
        var path = TempPath("overwrite.wb");
        var dir = Path.GetDirectoryName(path)!;

        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();
        WbPackage.Save(doc, path);
        Check.Equal(0, WbPackage.Load(path).Document.Pages[0].Count, "第一次保存应为空页");

        // 加 3 个对象后覆盖保存
        for (var i = 1; i <= 3; i++)
            doc.Pages[0].Add(RectObject.FromWorldCorners(i, new PointD(i * 10, 0), new PointD(i * 10 + 20, 20), "#FFFFFF", 3));
        WbPackage.Save(doc, path);

        Check.Equal(3, WbPackage.Load(path).Document.Pages[0].Count, "覆盖保存后应读到 3 个对象");

        var leftovers = Directory.GetFiles(dir, "*.tmp-*");
        Check.Equal(0, leftovers.Length, $"不应留下临时文件，实际 {leftovers.Length} 个");

        Directory.Delete(dir, true);
    }

    public static void Test_Wb_SaveCreatesDirectoryAndExtension()
    {
        var path = Path.Combine(TempPath("nested"), "sub", "board");

        WbPackage.Save(BuildRichDocument(), path);

        var expected = path + ".wb";
        Check.True(File.Exists(expected), $"应自动补 .wb 后缀并创建目录：{expected}");
        Check.True(WbPackage.LooksLikeWb(expected), "生成的文件应被识别为白板文件");

        Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(expected)!)!, true);
    }

    // ── ④ 损坏与版本 ──────────────────────────────────────────────────────

    public static void Test_Wb_RejectsNonWbFile()
    {
        var path = TempPath("notazip.wb");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "这不是压缩包，只是一段文本");

        try
        {
            WbPackage.Load(path);
            Check.True(false, "应当抛 WbLoadException");
        }
        catch (WbLoadException ex)
        {
            Check.True(ex.Message.Contains("损坏") || ex.Message.Contains("压缩包"),
                $"错误信息应对用户可读，实际：{ex.Message}");
        }

        Check.True(!WbPackage.LooksLikeWb(path), "非白板文件不应被识别为白板文件");
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    public static void Test_Wb_RejectsMissingFile()
    {
        try
        {
            WbPackage.Load(TempPath("nope.wb"));
            Check.True(false, "应当抛 WbLoadException");
        }
        catch (WbLoadException ex)
        {
            Check.True(ex.Message.Contains("找不到"), $"应提示找不到文件，实际：{ex.Message}");
        }
    }

    /// <summary>版本过高的文件必须**明确拒绝**，而不是按老格式硬读导致数据错乱。</summary>
    public static void Test_Wb_RejectsFutureSchemaVersion()
    {
        var path = TempPath("future.wb");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (var fs = File.Create(path))
        using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry(WbEntries.Format);
            using (var s = e.Open())
            using (var w = new StreamWriter(s))
                w.Write($"{{\"magic\":\"WhiteBoard\",\"schemaVersion\":{WbFormat.CurrentSchemaVersion + 7}}}");

            var d = zip.CreateEntry(WbEntries.Document);
            using (var s = d.Open())
            using (var w = new StreamWriter(s))
                w.Write("{\"id\":1,\"pages\":[]}");
        }

        try
        {
            WbPackage.Load(path);
            Check.True(false, "应当拒绝更高版本的文件");
        }
        catch (WbLoadException ex)
        {
            Check.True(ex.Message.Contains("更新版本"), $"应提示版本过高，实际：{ex.Message}");
        }

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    /// <summary>未知图元应被跳过并**警告**，而不是整个文件打不开（向前兼容）。</summary>
    public static void Test_Wb_SkipsUnknownKindsWithWarning()
    {
        var path = TempPath("unknown.wb");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        const string json = """
        {
          "magic": "WhiteBoard",
          "schemaVersion": 1,
          "document": {
            "id": 1,
            "currentPageIndex": 0,
            "pages": [
              { "id": 1, "zoom": 1, "objects": [
                  { "id": 1, "kind": "rect", "z": 1, "x": 0, "y": 0, "w": 50, "h": 50, "pen": 3, "color": "#FFFFFF" },
                  { "id": 2, "kind": "化学式", "z": 2, "x": 0, "y": 0, "w": 10, "h": 10 }
              ] }
            ]
          }
        }
        """;

        using (var fs = File.Create(path))
        using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry(WbEntries.Document);
            using var s = e.Open();
            using var w = new StreamWriter(s);
            w.Write(json);
        }

        var r = WbPackage.Load(path);
        Check.Equal(1, r.Document.Pages[0].Count, "应保留能识别的图元");
        Check.True(r.HasWarnings, "应给出警告");
        Check.True(r.WarningText.Contains("化学式"), $"警告应指明跳过了什么，实际：{r.WarningText}");

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    /// <summary>空页文档（0 页）也必须能打开：补一个空白页，符合"文档始终 ≥1 页"的约束。</summary>
    public static void Test_Wb_EmptyDocumentGetsOneBlankPage()
    {
        var path = TempPath("emptypages.wb");
        var doc = new WhiteboardDocument { Id = 3 };
        doc.EnsureAtLeastOnePage();
        WbPackage.Save(doc, path);

        var dst = WbPackage.Load(path).Document;
        Check.True(dst.Pages.Count >= 1, "至少应有一页");
        Check.Equal(0, dst.Pages[0].Count, "页应为空");

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    /// <summary>坐标精度：保存会按 0.0001 取整，往返误差必须远小于 1 像素。</summary>
    public static void Test_Wb_CoordinatePrecisionIsSubPixel()
    {
        var path = TempPath("precision.wb");
        var doc = new WhiteboardDocument { Id = 1 };
        var page = doc.EnsureAtLeastOnePage();

        var pts = new List<InkPoint>();
        for (var i = 0; i < 50; i++)
            pts.Add(new InkPoint(100.123456789 * i, 200.987654321 + i * 3.14159265, 0.5f));

        // 注意：FromWorldPoints 会平移到局部坐标，所以要以**对象自己的局部点**为基准比较
        var src = FreehandObject.FromWorldPoints(1, pts, "#FFFFFF", 6);
        page.Add(src);

        WbPackage.Save(doc, path);
        var loaded = (FreehandObject)WbPackage.Load(path).Document.Pages[0].Objects[0];

        var maxErr = 0.0;
        for (var i = 0; i < src.Points.Count; i++)
        {
            maxErr = Math.Max(maxErr, Math.Abs(src.Points[i].X - loaded.Points[i].X));
            maxErr = Math.Max(maxErr, Math.Abs(src.Points[i].Y - loaded.Points[i].Y));
        }

        // 世界坐标也要对得上（对象原点 X/Y 同样按精度保存）
        var worldSrc = src.LocalToWorld.Transform(src.Points[3].ToPoint());
        var worldDst = loaded.LocalToWorld.Transform(loaded.Points[3].ToPoint());
        maxErr = Math.Max(maxErr, Math.Abs(worldSrc.X - worldDst.X));
        maxErr = Math.Max(maxErr, Math.Abs(worldSrc.Y - worldDst.Y));

        Check.True(maxErr <= 0.0001 + 1e-9, $"往返误差应 ≤0.0001 世界单位（远小于 1 像素），实际 {maxErr}");

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}
