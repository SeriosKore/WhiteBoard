using System.IO;
using System.Windows.Media;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;
using WhiteBoard.Core.Tests;
using WhiteBoard.Rendering.Export;

namespace WhiteBoard.Rendering.Tests;

/// <summary>
/// PNG 导出：尺寸、内容裁剪、空页兜底、安全阀、以及与屏幕渲染的一致性
/// （"屏幕上擦掉的地方，导出图里也必须没有内容"）。
/// </summary>
public static class ExportTests
{
    private static Page OnePageWithStroke(out SmoothGeometryBuilder geometry)
    {
        geometry = new SmoothGeometryBuilder();
        var page = new Page { Id = 1 };
        page.Add(FreehandObject.FromWorldPoints(1,
            [new InkPoint(100, 100, 0.5f), new InkPoint(400, 300, 0.5f)], "#F5F5F0", 12));
        return page;
    }

    private static bool IsPng(byte[] bytes)
        => bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    public static void Test_Export_ProducesValidPng()
    {
        var page = OnePageWithStroke(out var geo);
        var r = PngExporter.RenderPage(page, geo, new PngExportOptions { Scale = 1 });

        Check.True(IsPng(r.Bytes), "输出应是 PNG（魔数正确）");
        Check.True(r.PixelWidth > 100 && r.PixelHeight > 100,
            $"尺寸应合理，实际 {r.PixelWidth}×{r.PixelHeight}");

        // 尺寸 = 内容包围盒（含笔宽）+ 两侧留白
        var bounds = page.ContentBounds();
        Check.Equal((int)Math.Ceiling(bounds.Width + 48), r.PixelWidth, "宽度应为 内容宽+留白48");
        Check.Equal((int)Math.Ceiling(bounds.Height + 48), r.PixelHeight, "高度应为 内容高+留白48");
    }

    public static void Test_Export_ScaleMultipliesPixels()
    {
        var page = OnePageWithStroke(out var geo);

        var at1 = PngExporter.RenderPage(page, geo, new PngExportOptions { Scale = 1 });
        var at2 = PngExporter.RenderPage(page, geo, new PngExportOptions { Scale = 2 });

        Check.Equal(at1.PixelWidth * 2, at2.PixelWidth, "2 倍导出的宽度应翻倍");
        Check.Equal(at1.PixelHeight * 2, at2.PixelHeight, "2 倍导出的高度应翻倍");
        Check.True(at2.Bytes.Length > at1.Bytes.Length, "2 倍导出的文件应更大");
    }

    /// <summary>安全阀：超长内容必须被收敛到单边上限内，而不是分配一张巨大的位图。</summary>
    public static void Test_Export_ClampsHugeContent()
    {
        var geometry = new SmoothGeometryBuilder();
        var page = new Page { Id = 1 };
        page.Add(LineObject.FromWorldPoints(1, new PointD(0, 0), new PointD(200000, 100), "#FFFFFF", 6));

        var r = PngExporter.RenderPage(page, geometry, new PngExportOptions
        {
            Scale = 2,
            MaxSide = 4000
        });

        Check.True(r.Clamped, "应报告被安全阀收敛");
        Check.True(r.PixelWidth <= 4000 && r.PixelHeight <= 4000,
            $"单边不应超过上限，实际 {r.PixelWidth}×{r.PixelHeight}");
        Check.True(r.EffectiveScale < 2, $"实际倍率应小于请求的 2，实际 {r.EffectiveScale}");
        Check.True(IsPng(r.Bytes), "收敛后仍应产出合法 PNG");
    }

    public static void Test_Export_EmptyPageUsesDefaultCanvas()
    {
        var geometry = new SmoothGeometryBuilder();
        var page = new Page { Id = 1 };

        var r = PngExporter.RenderPage(page, geometry, new PngExportOptions
        {
            Scale = 1,
            EmptyPageWidth = 640,
            EmptyPageHeight = 360
        });

        Check.Equal(640, r.PixelWidth, "空页应使用默认画布宽度");
        Check.Equal(360, r.PixelHeight, "空页应使用默认画布高度");
        Check.True(IsPng(r.Bytes), "空页也应产出合法 PNG");
    }

    /// <summary>
    /// 与屏幕渲染一致性：同一条笔迹、同一处擦除，导出图里那块区域也必须是背景色。
    /// 这条断言守护的是"导出没有另起一条绘制路径"。
    /// </summary>
    public static void Test_Export_ErasedAreaIsEmptyLikeOnScreen()
    {
        var geometry = new SmoothGeometryBuilder();
        var page = new Page { Id = 1 };
        page.Viewport.Zoom = 1;

        var stroke = FreehandObject.FromWorldPoints(1,
            [new InkPoint(100, 200, 0.5f), new InkPoint(700, 200, 0.5f)], "#F5F5F0", 20);
        // 在中间挖一个半径 40 的洞（局部坐标：对象 X=100,Y=200 → 局部中心约 (300,0)）
        stroke.AddErasure(new EraserCircle(300, 0, 40));
        page.Add(stroke);

        var opts = new PngExportOptions
        {
            Scale = 1,
            MarginWorld = 0,
            Background = Color.FromRgb(0x2F, 0x4F, 0x3A)
        };
        var r = PngExporter.RenderPage(page, geometry, opts);

        // 用标准解码器读回，确保测的是"文件里真实的像素"
        var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
            new System.IO.MemoryStream(r.Bytes),
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        var stride = frame.PixelWidth * 4;
        var px = new byte[stride * frame.PixelHeight];
        converted.CopyPixels(px, stride, 0);

        bool IsBg(int x, int y)
        {
            var i = y * stride + x * 4;
            return Math.Abs(px[i] - 0x3A) <= 12
                   && Math.Abs(px[i + 1] - 0x4F) <= 12
                   && Math.Abs(px[i + 2] - 0x2F) <= 12;
        }

        // 导出矩形 = 内容包围盒（含笔宽）= 世界 (90,190) 起，620×20 像素
        var midRow = frame.PixelHeight / 2;
        Check.True(!IsBg(50, midRow), "笔迹未被擦除处应有像素");
        Check.True(IsBg(300, midRow), "擦除中心应为背景（导出与屏幕一致）");
        Check.True(!IsBg(400, midRow), "擦除区之外应仍有像素");
    }

    public static void Test_Export_AllPagesNamesFilesSequentially()
    {
        var geometry = new SmoothGeometryBuilder();
        var doc = new WhiteboardDocument { Id = 1 };
        var p1 = doc.EnsureAtLeastOnePage();
        p1.Add(FreehandObject.FromWorldPoints(1, [new InkPoint(0, 0, 0.5f), new InkPoint(100, 100, 0.5f)], "#FFFFFF", 6));
        var p2 = doc.AddPage();
        p2.Add(RectObject.FromWorldCorners(2, new PointD(0, 0), new PointD(50, 50), "#FFFFFF", 3));

        var dir = Path.Combine(Path.GetTempPath(), "wb-export-" + Guid.NewGuid().ToString("N")[..8]);

        var files = PngExporter.ExportAllPages(doc, geometry, dir, "测试:画板*名", new PngExportOptions { Scale = 1 });

        Check.Equal(2, files.Count, "应导出 2 个文件");
        Check.True(files.All(File.Exists), "文件应都存在");
        Check.True(files[0].EndsWith("第01页.png"), $"命名应带页序，实际 {Path.GetFileName(files[0])}");

        // 只看文件名本身（完整路径里的盘符冒号是合法的）
        var name = Path.GetFileName(files[0]);
        Check.True(!name.Contains(':') && !name.Contains('*'),
            $"非法文件名字符应被替换，实际 {name}");

        Directory.Delete(dir, true);
    }

    /// <summary>
    /// 回归：一条**水平**笔迹的采样点包围盒高度为 0。
    /// 若外接矩形不含笔宽，视口只覆盖笔迹上半部分时会被"裁剪"整条丢掉 —— 用户会看到笔迹凭空消失。
    /// </summary>
    public static void Test_Export_ThickHorizontalStrokeIsNotDegenerate()
    {
        var geometry = new SmoothGeometryBuilder();
        var page = new Page { Id = 1 };
        page.Add(FreehandObject.FromWorldPoints(1,
            [new InkPoint(100, 200, 0.5f), new InkPoint(700, 200, 0.5f)], "#F5F5F0", 20));

        var bounds = page.ContentBounds();
        Check.True(bounds.Height >= 20 - 1e-6,
            $"水平笔迹的内容高度应包含笔宽（≥20），实际 {bounds.Height}");

        var r = PngExporter.RenderPage(page, geometry, new PngExportOptions { Scale = 1, MarginWorld = 0 });
        Check.True(r.PixelHeight >= 20, $"导出高度应容纳笔宽，实际 {r.PixelHeight}");
    }

    /// <summary>内容极小的情形（一个点）应被补足到最小画布，而不是 1×1 像素。</summary>
    public static void Test_Export_TinyContentGetsMinimumCanvas()
    {
        var geometry = new SmoothGeometryBuilder();
        var page = new Page { Id = 1 };
        page.Add(FreehandObject.FromWorldPoints(1, [new InkPoint(50, 50, 0.5f)], "#FFFFFF", 4));

        var r = PngExporter.RenderPage(page, geometry, new PngExportOptions { Scale = 1, MinSide = 64 });

        Check.True(r.PixelWidth >= 64 && r.PixelHeight >= 64,
            $"单点内容应被补足到至少 64×64，实际 {r.PixelWidth}×{r.PixelHeight}");
    }
}
