using System.Windows.Media;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;
using WhiteBoard.Core.Tests;
using WhiteBoard.Rendering.Export;

namespace WhiteBoard.Rendering.Tests;

/// <summary>
/// 页面缩略图：取景、尺寸上限、空页兜底、冻结、以及"与主画布一致"
/// （缩略图里被擦掉的地方也必须是空的——否则页面目录会骗人）。
/// </summary>
public static class ThumbnailTests
{
    private static readonly Color Board = Color.FromRgb(0x2F, 0x4F, 0x3A);

    private static ThumbnailRenderer NewRenderer(int maxSide = 160)
        => new(new SmoothGeometryBuilder()) { MaxSide = maxSide, Background = Board, MarginWorld = 0 };

    public static void Test_Thumbnail_ContentCroppedWithinMaxSide()
    {
        var page = new Page { Id = 1 };
        page.Add(FreehandObject.FromWorldPoints(1,
            [new InkPoint(0, 0, 0.5f), new InkPoint(1000, 100, 0.5f)], "#F5F5F0", 20));

        var bmp = NewRenderer(160).Render(page);

        Check.Equal(160, bmp.PixelWidth, "宽的一边应被缩到 MaxSide");
        Check.True(bmp.PixelHeight < 160, $"矮的一边应更小，实际 {bmp.PixelHeight}");
        Check.True(bmp.PixelHeight >= 8, $"不应退化成一条线，实际 {bmp.PixelHeight}");
    }

    public static void Test_Thumbnail_TallContentIsAlsoCapped()
    {
        var page = new Page { Id = 1 };
        page.Add(FreehandObject.FromWorldPoints(1,
            [new InkPoint(0, 0, 0.5f), new InkPoint(50, 2000, 0.5f)], "#F5F5F0", 20));

        var bmp = NewRenderer(160).Render(page);

        Check.Equal(160, bmp.PixelHeight, "高的一边应被缩到 MaxSide");
        Check.True(bmp.PixelWidth < 160, $"窄的一边应更小，实际 {bmp.PixelWidth}");
    }

    /// <summary>空页不能给个 0×0 或一条线：给一个 4:3 的固定取景。</summary>
    public static void Test_Thumbnail_EmptyPageUsesDefaultAspect()
    {
        var page = new Page { Id = 1 };
        var bmp = NewRenderer(160).Render(page);

        Check.Equal(160, bmp.PixelWidth, "空页默认取景宽应为 MaxSide");
        Check.Equal(120, bmp.PixelHeight, "空页默认取景应为 4:3 → 高 120");
    }

    public static void Test_Thumbnail_IsFrozenAndReusable()
    {
        var page = new Page { Id = 1 };
        page.Add(FreehandObject.FromWorldPoints(1,
            [new InkPoint(0, 0, 0.5f), new InkPoint(200, 200, 0.5f)], "#F5F5F0", 10));

        var bmp = NewRenderer().Render(page);
        Check.True(bmp.IsFrozen, "缩略图应已冻结（可安全复用、不依赖渲染线程）");
        Check.True(bmp.CanFreeze, "应可冻结");
    }

    /// <summary>
    /// **一致性**：主画布上被橡皮擦掉的一段，在缩略图里也必须是空的。
    /// 缩略图与画布复用同一个 PageRenderer，所以这一点是"结构上成立"的——这里把它钉住，
    /// 防止以后有人为了性能给缩略图另写一条精简绘制路径。
    /// </summary>
    public static void Test_Thumbnail_ErasedGapIsEmptyLikeOnCanvas()
    {
        var geometry = new SmoothGeometryBuilder();
        var page = new Page { Id = 1 };

        var stroke = FreehandObject.FromWorldPoints(1,
            [new InkPoint(0, 100, 0.5f), new InkPoint(400, 100, 0.5f)], "#F5F5F0", 40);
        // 中间挖掉一大段（局部坐标 x=200 附近）
        stroke.AddErasure(new EraserCircle(200, 0, 40));
        page.Add(stroke);

        var thumb = new ThumbnailRenderer(geometry) { MaxSide = 160, Background = Board, MarginWorld = 0 };
        var bmp = thumb.Render(page);

        var stride = bmp.PixelWidth * 4;
        var px = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(px, stride, 0);

        bool IsBg(int x, int y)
        {
            var i = y * stride + x * 4;
            return Math.Abs(px[i] - 0x3A) <= 14
                   && Math.Abs(px[i + 1] - 0x4F) <= 14
                   && Math.Abs(px[i + 2] - 0x2F) <= 14;
        }

        var midRow = bmp.PixelHeight / 2;
        var midX = bmp.PixelWidth / 2;

        Check.True(!IsBg(bmp.PixelWidth / 8, midRow), "笔迹未被擦除处应可见");
        Check.True(IsBg(midX, midRow), "缩略图里被擦掉的一段也应为空（与画布一致）");
        Check.True(!IsBg(bmp.PixelWidth * 7 / 8, midRow), "擦除区之外仍应可见");
    }

    public static void Test_Thumbnail_PngBytesAreValidPng()
    {
        var page = new Page { Id = 1 };
        page.Add(RectObject.FromWorldCorners(1, new PointD(0, 0), new PointD(100, 80), "#FFFFFF", 4));

        var bytes = NewRenderer().RenderPngBytes(page);

        Check.True(bytes.Length > 100, $"PNG 应有一定体积，实际 {bytes.Length} 字节");
        Check.Equal(0x89, bytes[0], "PNG 魔数第 1 字节");
        Check.Equal(0x50, bytes[1], "PNG 魔数第 2 字节（P）");
        Check.Equal(0x4E, bytes[2], "PNG 魔数第 3 字节（N）");
        Check.Equal(0x47, bytes[3], "PNG 魔数第 4 字节（G）");
    }

    /// <summary>缩略图用的是"内容取景"，所以内容位置不同不影响成图尺寸（页面目录里对齐好看）。</summary>
    public static void Test_Thumbnail_SizeDependsOnContentExtentNotPosition()
    {
        var a = new Page { Id = 1 };
        a.Add(FreehandObject.FromWorldPoints(1,
            [new InkPoint(0, 0, 0.5f), new InkPoint(300, 200, 0.5f)], "#FFFFFF", 10));

        var b = new Page { Id = 2 };
        b.Add(FreehandObject.FromWorldPoints(1,
            [new InkPoint(5000, 9000, 0.5f), new InkPoint(5300, 9200, 0.5f)], "#FFFFFF", 10));

        var ta = NewRenderer().Render(a);
        var tb = NewRenderer().Render(b);

        Check.Equal(ta.PixelWidth, tb.PixelWidth, "位置不同的同样内容应得到同样尺寸的缩略图");
        Check.Equal(ta.PixelHeight, tb.PixelHeight, "位置不同的同样内容应得到同样尺寸的缩略图");
    }
}
