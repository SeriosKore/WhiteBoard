using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Rendering.Export;

/// <summary>
/// 页面缩略图渲染（页面目录面板用，也用于 <c>.wb</c> 包内的预览图）。
///
/// 三个要点：
/// <list type="number">
/// <item>**复用同一个 <see cref="PageRenderer"/>**：缩略图与主画布走同一套几何与遮罩差集，
///       所以"缩略图上看到有内容的地方，主画布上一定有"（不会出现缩略图和实际不一致）；</item>
/// <item>**按内容裁剪**：一页只画在右下角时，缩略图也应显示那部分内容，
///       而不是给一块空白的大画布（否则用户根本判断不出哪页写了什么）；</item>
/// <item>**结果冻结**（<see cref="Freezable.Freeze"/>）：冻结后的位图可以安全地在多处复用，
///       也不会因为渲染线程不同而出错。</item>
/// </list>
/// </summary>
public sealed class ThumbnailRenderer
{
    private readonly SmoothGeometryBuilder _geometry;
    private readonly PageRenderer _renderer;

    public ThumbnailRenderer(SmoothGeometryBuilder geometry)
    {
        _geometry = geometry;
        _renderer = new PageRenderer(geometry);
    }

    /// <summary>缩略图最长边的像素上限。</summary>
    public int MaxSide { get; init; } = 168;

    /// <summary>内容四周留白（世界单位）。</summary>
    public double MarginWorld { get; init; } = 16;

    /// <summary>背景色（取主题背景）。</summary>
    public Color Background
    {
        get => _renderer.BackgroundColor;
        set => _renderer.BackgroundColor = value;
    }

    /// <summary>渲染一页为缩略图（已冻结，可直接给 Image 用）。</summary>
    public BitmapSource Render(Page page)
    {
        var (rect, scale) = ComputeView(page);

        var pxW = Math.Max(8, (int)Math.Ceiling(rect.Width * scale));
        var pxH = Math.Max(8, (int)Math.Ceiling(rect.Height * scale));

        var vp = new ViewportState
        {
            Zoom = scale,
            PanX = rect.X,
            PanY = rect.Y
        };

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            _renderer.Render(dc, page, vp, pxW, pxH);

        var bmp = new RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>渲染为 PNG 字节（用于写进 <c>.wb</c> 包做预览）。</summary>
    public byte[] RenderPngBytes(Page page)
    {
        var bmp = Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>求缩略图的取景矩形与缩放比。</summary>
    private (Core.Geometry.RectD Rect, double Scale) ComputeView(Page page)
    {
        var bounds = page.ContentBounds();

        // 空页：给一个 4:3 的固定取景，缩略图不至于是一条线或一个点
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            return (new Core.Geometry.RectD(0, 0, 960, 720), MaxSide / 960.0);

        var m = Math.Max(0, MarginWorld);
        var rect = new Core.Geometry.RectD(
            bounds.X - m, bounds.Y - m, bounds.Width + m * 2, bounds.Height + m * 2);

        var longest = Math.Max(rect.Width, rect.Height);
        var scale = longest <= 1e-6 ? 1.0 : MaxSide / longest;
        scale = Math.Clamp(scale, 1e-4, 8.0);

        return (rect, scale);
    }
}
