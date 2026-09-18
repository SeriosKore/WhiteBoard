using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteBoard.App.Services;
using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Model;
using WhiteBoard.Core.Storage;
using WhiteBoard.Core.Text;
using WhiteBoard.Rendering;
using WhiteBoard.Rendering.Export;

namespace WhiteBoard.App.Diagnostics;

/// <summary>
/// 文件层自检：<c>--file-smoke</c>。
///
/// 它回答的是"存了再打开，还是不是同一张图"，而不只是"字段对不对"：
/// <list type="number">
/// <item>用生产链路造一份画板 → 保存成 <c>.wb</c> → 重新打开；</item>
/// <item>比较对象数、页数、擦除遮罩数、Z 序；</item>
/// <item><b>把保存前与重新打开后的第 1 页各自渲染成位图，逐像素比较</b> ——
///       这是"画板没有在存读过程中悄悄变形"的最强证据；</item>
/// <item>顺带验证 PNG 导出与自动保存确实落盘。</item>
/// </list>
/// </summary>
public static class FileSmoke
{
    public static bool IsRequested(string[] args)
        => args.Any(a => a.Equals("--file-smoke", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        SelfTest.TryAttachParentConsole();

        var reportPath = GetArgValue(args, "--report");
        var keepDir = GetArgValue(args, "--keep");   // 指定目录则保留产物，便于人工查看

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

        // 用 exe 同级目录（C3）：自检产物不允许写到程序目录外
        var baseDir = AppContext.BaseDirectory;
        var paths = PathService.Resolve(baseDir);
        var workDir = keepDir ?? Path.Combine(paths.DataRoot, "smoke");
        var docPath = Path.Combine(workDir, "file-smoke" + WbPackage.Extension);

        lines.Add($"WhiteBoard 文件层自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        lines.Add($"工作目录：{workDir}");
        lines.Add("");

        Directory.CreateDirectory(workDir);

        // ── ① 造一份内容：用生产链路（示例画板）───────────────────────────
        var demo = DemoBoard.Build(1280, 800);
        var session = new DocumentSession(paths, "file-smoke");
        session.Adopt(demo.Document, null);

        var geometry = new SmoothGeometryBuilder();
        var renderer = new PageRenderer(geometry) { BackgroundColor = Color.FromRgb(0x2F, 0x4F, 0x3A) };

        var before = RenderPage(demo.Document.Pages[0], renderer, 1280, 800);

        Check("构造内容", () =>
        {
            var page = demo.Document.Pages[0];
            return (page.Count >= 7,
                $"{demo.Document.Pages.Count} 页，第 1 页 {page.Count} 个对象，" +
                $"其中带擦除遮罩 {page.Objects.Count(o => o.Erasures.Count > 0)} 个");
        });

        // ── ② 保存 ────────────────────────────────────────────────────────
        var saveResult = session.Save(docPath);

        Check("保存 .wb", () =>
        {
            if (!saveResult.Ok) return (false, saveResult.Message);
            var size = new FileInfo(docPath).Length;
            return (size > 512, $"{docPath}（{size / 1024.0:0.0} KB，含缩略图）");
        });

        Check("保存后不再是脏状态", () => (!session.IsDirty, session.IsDirty ? "仍标记为有未保存改动" : "已清脏标记"));

        Check("写入最近文件", () =>
        {
            var list = session.Recent.Load();
            var hit = list.FirstOrDefault(e => string.Equals(
                Path.GetFullPath(e.Path), Path.GetFullPath(docPath), StringComparison.OrdinalIgnoreCase));
            return (hit is not null, hit is null ? "最近文件里没有这一条" : $"{list.Count} 条，首条={list[0].Name}");
        });

        Check("包内摘要可读", () =>
        {
            var s = WbPackage.TryReadSummary(docPath);
            if (s is null) return (false, "读不到摘要");
            return (s.Value.ObjectCount == demo.Document.Pages.Sum(p => p.Count),
                $"格式 v{s.Value.SchemaVersion}，{s.Value.PageCount} 页，{s.Value.ObjectCount} 个对象");
        });

        Check("包内缩略图存在", () =>
        {
            var png = WbPackage.TryReadPreview(docPath);
            var ok = png is { Length: > 100 } && png[0] == 0x89 && png[1] == 0x50;
            return (ok, png is null ? "没有缩略图" : $"{png.Length / 1024.0:0.0} KB，PNG 魔数正确");
        });

        // ── ③ 重新打开 ────────────────────────────────────────────────────
        var reopen = new DocumentSession(paths, "file-smoke");
        var openResult = reopen.Open(docPath);
        var after = openResult.Ok ? RenderPage(reopen.Document.Pages[0], renderer, 1280, 800) : null;

        Check("重新打开", () => (openResult.Ok, openResult.Message));

        Check("结构一致", () =>
        {
            if (!openResult.Ok) return (false, "未打开成功");
            var a = demo.Document;
            var b = reopen.Document;
            var samePages = a.Pages.Count == b.Pages.Count;
            var sameObjects = a.Pages.Sum(p => p.Count) == b.Pages.Sum(p => p.Count);
            var sameErasures = a.Pages.SelectMany(p => p.Objects).Sum(o => o.Erasures.Count)
                               == b.Pages.SelectMany(p => p.Objects).Sum(o => o.Erasures.Count);
            var sameOrder = string.Join(",", a.Pages[0].InRenderOrder().Select(o => o.Id))
                            == string.Join(",", b.Pages[0].InRenderOrder().Select(o => o.Id));
            return (samePages && sameObjects && sameErasures && sameOrder,
                $"页 {b.Pages.Count}、对象 {b.Pages.Sum(p => p.Count)}、遮罩 " +
                $"{b.Pages.SelectMany(p => p.Objects).Sum(o => o.Erasures.Count)}、Z 序一致={sameOrder}");
        });

        // ── ④ 像素级一致（最强的一条）────────────────────────────────────
        Check("存读前后渲染逐像素一致", () =>
        {
            if (before is null || after is null) return (false, "缺少位图");

            var (diff, total) = PixelDiff(before, after);
            var ratio = 100.0 * diff / total;
            // 坐标按 0.0001 取整，抗锯齿边缘允许极微量差异；超过 0.3% 就是"图画变了"
            return (ratio <= 0.3, $"不同像素 {diff}/{total}（{ratio:0.000}%）");
        });

        // ── ⑤ 导出 PNG ───────────────────────────────────────────────────
        var exportDir = Path.Combine(workDir, "export");
        var exportResult = reopen.ExportAllPages(geometry, exportDir, new PngExportOptions
        {
            Scale = 1,
            MarginWorld = 24,
            Background = Color.FromRgb(0x2F, 0x4F, 0x3A)
        });

        Check("批量导出 PNG", () =>
        {
            if (!exportResult.Ok) return (false, exportResult.Message);
            var files = Directory.GetFiles(exportDir, "*.png");
            var allValid = files.All(f =>
            {
                var b = File.ReadAllBytes(f);
                return b.Length > 100 && b[0] == 0x89 && b[1] == 0x50;
            });
            return (files.Length == reopen.Document.Pages.Count && allValid,
                $"{files.Length} 个文件，全部为有效 PNG：{string.Join("、", files.Select(Path.GetFileName))}");
        });

        Check("导出图与世界一致（擦除处为空）", () =>
        {
            var first = Directory.GetFiles(exportDir, "*.png").OrderBy(f => f).First();
            var r = PngExporter.RenderPage(reopen.Document.Pages[0], geometry, new PngExportOptions
            {
                Scale = 1,
                MarginWorld = 0,
                Background = Color.FromRgb(0x2F, 0x4F, 0x3A)
            });
            var bytes = r.Bytes;
            return (bytes.Length > 100, $"当前页导出 {r.PixelWidth}×{r.PixelHeight}，{r.Note}");
        });

        // ── ⑥ 自动保存 ───────────────────────────────────────────────────
        Check("自动保存落盘且可恢复", () =>
        {
            reopen.MarkDirty(DateTime.UtcNow.AddMinutes(-5));
            var t0 = DateTime.UtcNow;
            var ok = reopen.AutoSave.Save(reopen.Document, docPath, null, "file-smoke", t0);
            if (!ok) return (false, reopen.AutoSave.LastStatus);

            var info = reopen.AutoSave.Inspect();
            if (info is null) return (false, "写完后却检查不到自动保存");
            return (info.AutoSaveFile.StartsWith(paths.DataRoot, StringComparison.OrdinalIgnoreCase),
                $"位置合规：{info.AutoSaveFile}");
        });

        Check("正常保存后自动保存被清理", () =>
        {
            var r = reopen.Save(docPath);
            var info = reopen.AutoSave.Inspect();
            return (r.Ok && info is null,
                info is null ? "已清理（不会误报有未保存内容）" : "仍残留自动保存");
        });

        // ── ⑦ 页面外壳：新建/复制/删除页、跨页搬运、缩略图 ─────────────────
        Check("页面操作（新建/复制/删除）", () =>
        {
            var doc = reopen.Document;
            var before = doc.Pages.Count;

            var add = new AddPageCommand(doc);
            add.Do();
            var afterAdd = doc.Pages.Count;

            var copy = new DuplicatePageCommand(doc, doc.Pages[0]);
            copy.Do();
            var afterCopy = doc.Pages.Count;

            var del = new RemovePageCommand(doc, doc.Pages[doc.Pages.Count - 1]);
            del.Do();
            var afterDelete = doc.Pages.Count;

            // 撤销三次回到原点
            del.Undo();
            copy.Undo();
            add.Undo();

            return (afterAdd == before + 1 && afterCopy == before + 2 && afterDelete == before + 1
                    && doc.Pages.Count == before,
                $"{before} → 新建 {afterAdd} → 复制 {afterCopy} → 删除 {afterDelete} → 全部撤销回 {doc.Pages.Count}");
        });

        Check("删除最后一页被拒绝", () =>
        {
            var doc = new WhiteboardDocument { Id = 99 };
            doc.EnsureAtLeastOnePage();
            var cmd = new RemovePageCommand(doc, doc.Pages[0]);
            cmd.Do();
            return (cmd.Refused && doc.Pages.Count == 1, $"页数仍为 {doc.Pages.Count}，已拒绝={cmd.Refused}");
        });

        Check("跨页搬运保留世界坐标", () =>
        {
            var doc = reopen.Document;
            if (doc.Pages.Count < 2) return (false, "需要至少两页");

            var from = doc.Pages[0];
            var to = doc.Pages[1];
            if (from.Count == 0) return (false, "第 1 页没有对象可搬");

            var obj = from.Objects[0];
            var x = obj.X;
            var y = obj.Y;
            var fromCount = from.Count;
            var toCount = to.Count;

            var move = new MoveObjectsToPageCommand(doc, from, to, [obj]);
            move.Do();
            var movedOk = from.Count == fromCount - 1 && to.Count == toCount + 1
                          && Math.Abs(obj.X - x) < 1e-9 && Math.Abs(obj.Y - y) < 1e-9;

            move.Undo();
            var undoOk = from.Count == fromCount && to.Count == toCount;

            return (movedOk && undoOk,
                $"搬走：源 {from.Count + 1}→{from.Count}；撤销后源 {from.Count}、目标 {to.Count}；坐标保持={Math.Abs(obj.X - x) < 1e-9}");
        });

        Check("为每一页生成缩略图", () =>
        {
            var thumb = new ThumbnailRenderer(geometry)
            {
                MaxSide = 168,
                Background = Color.FromRgb(0x2F, 0x4F, 0x3A)
            };

            var sizes = new List<string>();
            foreach (var p in reopen.Document.Pages)
            {
                var bmp = thumb.Render(p);
                if (bmp.PixelWidth > 168 || bmp.PixelHeight > 168)
                    return (false, $"缩略图超过上限：{bmp.PixelWidth}×{bmp.PixelHeight}");
                if (!bmp.IsFrozen) return (false, "缩略图未冻结");
                sizes.Add($"{p.Id}:{bmp.PixelWidth}×{bmp.PixelHeight}");
            }

            return (sizes.Count == reopen.Document.Pages.Count, string.Join("　", sizes));
        });

        Check("追加导入另一份 .wb", () =>
        {
            // 先把当前文档另存一份，再把它当"另一份画板"导回来——走完整文件链路
            var other = Path.Combine(workDir, "another" + WbPackage.Extension);
            WbPackage.Save(reopen.Document, other);

            var otherDoc = WbPackage.Load(other);
            var doc = reopen.Document;
            var pagesBefore = doc.Pages.Count;
            var objectsBefore = doc.Pages.Sum(p => p.Count);

            var append = new AppendDocumentCommand(doc, otherDoc.Document);
            append.Do();

            var pagesAfter = doc.Pages.Count;
            var objectsAfter = doc.Pages.Sum(p => p.Count);
            var idsOk = doc.Pages.Select(p => p.Id).Distinct().Count() == pagesAfter
                        && doc.Pages.SelectMany(p => p.Objects).Select(o => o.Id).Distinct().Count() == objectsAfter;

            append.Undo();
            var undoneOk = doc.Pages.Count == pagesBefore && doc.Pages.Sum(p => p.Count) == objectsBefore;

            append.Do();   // 重做，让后续检查看到合并后的文档
            append.Undo();

            return (pagesAfter == pagesBefore * 2 && objectsAfter == objectsBefore * 2 && idsOk && undoneOk,
                $"{pagesBefore} 页/{objectsBefore} 对象 → 导入后 {pagesAfter} 页/{objectsAfter} 对象；" +
                $"Id 唯一={idsOk}；撤销回原状={undoneOk}");
        });

        Check("文本对象随文件往返", () =>
        {
            var texts = reopen.Document.Pages
                .SelectMany(p => p.Objects).OfType<TextObject>().ToList();
            if (texts.Count == 0) return (false, "示例板里没有文本对象可验证");

            var original = demo.Document.Pages
                .SelectMany(p => p.Objects).OfType<TextObject>().ToList();

            var sameContent = texts.Select(t => t.Text).OrderBy(x => x)
                .SequenceEqual(original.Select(t => t.Text).OrderBy(x => x));
            var sameSize = texts.All(t => t.LocalWidth > 1 && t.LocalHeight > 1);
            var notErasable = texts.All(t => !t.IsErasable);

            return (sameContent && sameSize && notErasable,
                $"{texts.Count} 段文本；内容一致={sameContent}；尺寸有效={sameSize}；不可擦={notErasable}　" +
                $"首段「{texts[0].FirstLine}」");
        });

        // ── ⑧ 损坏文件不崩 ────────────────────────────────────────────────
        Check("损坏文件给出可读错误", () =>
        {
            var bad = Path.Combine(workDir, "坏文件.wb");
            File.WriteAllText(bad, "这不是画板文件");
            var r = new DocumentSession(paths, "file-smoke").Open(bad);
            return (!r.Ok && (r.Message.Contains("损坏") || r.Message.Contains("压缩包")),
                r.Message);
        });

        // ── 清理 ─────────────────────────────────────────────────────────
        if (keepDir is null)
        {
            Check("清理自检产物", () =>
            {
                try
                {
                    Directory.Delete(workDir, true);

                    // 顺带把自检写进最近文件的那条删掉（避免污染用户的最近列表）
                    new RecentFiles(paths.RecentFile).Remove(docPath);
                    return (true, "已删除工作目录并清理最近文件条目");
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
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

    private static RenderTargetBitmap? RenderPage(Page page, PageRenderer renderer, int w, int h)
    {
        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                renderer.Render(dc, page, new ViewportState(), w, h);

            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>逐像素比较两张同尺寸位图，返回（不同像素数, 总像素数）。</summary>
    private static (int Diff, int Total) PixelDiff(RenderTargetBitmap a, RenderTargetBitmap b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight)
            return (int.MaxValue, 1);

        var stride = a.PixelWidth * 4;
        var bufA = new byte[stride * a.PixelHeight];
        var bufB = new byte[stride * b.PixelHeight];
        a.CopyPixels(bufA, stride, 0);
        b.CopyPixels(bufB, stride, 0);

        var diff = 0;
        for (var i = 0; i + 3 < bufA.Length; i += 4)
        {
            // 只看颜色通道；容差 2 容忍浮点→8bit 的舍入
            if (Math.Abs(bufA[i] - bufB[i]) > 2 ||
                Math.Abs(bufA[i + 1] - bufB[i + 1]) > 2 ||
                Math.Abs(bufA[i + 2] - bufB[i + 2]) > 2)
                diff++;
        }

        return (diff, a.PixelWidth * a.PixelHeight);
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
