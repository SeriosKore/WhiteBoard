using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Rendering.Export;

/// <summary>PNG 导出参数。</summary>
public sealed class PngExportOptions
{
    /// <summary>输出倍率（1 = 与世界单位 1:1；2 = 更清晰，适合打印/投影截图）。</summary>
    public double Scale { get; init; } = 2.0;

    /// <summary>内容四周留白（世界单位）。</summary>
    public double MarginWorld { get; init; } = 24;

    /// <summary>按内容包围盒裁剪（true）还是按页面视口所见导出（false）。</summary>
    public bool UseContentBounds { get; init; } = true;

    /// <summary>单边最大像素（安全阀：防止个别失控对象把画布撑到几万像素导致内存爆炸）。</summary>
    public int MaxSide { get; init; } = 8000;

    /// <summary>
    /// 单边最小像素：一个点、一条极短的线（内容包围盒接近 0）也应导出成一张能看的图，
    /// 而不是 1×1 像素。
    /// </summary>
    public int MinSide { get; init; } = 64;

    /// <summary>背景色（取主题背景）。</summary>
    public Color Background { get; init; } = Color.FromRgb(0x2F, 0x4F, 0x3A);

    /// <summary>空页时的默认画布尺寸（世界单位）。</summary>
    public double EmptyPageWidth { get; init; } = 1280;
    public double EmptyPageHeight { get; init; } = 720;
}

/// <summary>一次导出的结果（含实际尺寸与是否被安全阀收敛）。</summary>
public sealed record PngExportResult(
    byte[] Bytes,
    int PixelWidth,
    int PixelHeight,
    double EffectiveScale,
    bool Clamped,
    string Note);

/// <summary>
/// PNG 导出。
///
/// 复用与屏幕渲染**同一个** <see cref="PageRenderer"/>：导出与屏幕看到的是同一套几何与遮罩差集
/// （不是另写一条绘制路径），所以"屏幕上擦掉的地方，导出图里也一定是空的"。
///
/// 导出位置由调用方决定：默认落在 <c>&lt;exe同级&gt;\data\export\</c>（C3）；
/// 用户显式选择其他目录时属于"C3 的显式导出例外"。
/// </summary>
public static class PngExporter
{
    /// <summary>把一页渲染成 PNG 字节。</summary>
    /// <param name="page">要导出的页。</param>
    /// <param name="geometry">几何构建器（与屏幕渲染共用同一个实例即可复用冻结缓存）。</param>
    /// <param name="options">导出参数。</param>
    /// <param name="viewportOverride">
    /// 仅在 <see cref="PngExportOptions.UseContentBounds"/> 为 false 时使用：
    /// 按该视口"所见即所得"导出。
    /// </param>
    /// <param name="viewportScreenWidth">配合 <paramref name="viewportOverride"/> 的屏幕尺寸。</param>
    /// <param name="viewportScreenHeight">配合 <paramref name="viewportOverride"/> 的屏幕尺寸。</param>
    public static PngExportResult RenderPage(
        Page page,
        SmoothGeometryBuilder geometry,
        PngExportOptions? options = null,
        ViewportState? viewportOverride = null,
        double viewportScreenWidth = 1280,
        double viewportScreenHeight = 720)
    {
        var o = options ?? new PngExportOptions();
        var renderer = new PageRenderer(geometry) { BackgroundColor = o.Background };

        var (worldRect, note) = ResolveWorldRect(
            page, o, viewportOverride, viewportScreenWidth, viewportScreenHeight);

        var wantedScale = o.Scale <= 0 ? 1 : o.Scale;

        // 内容太薄（点、短横线）时把世界矩形按最小边长补足，保证输出不是 1 像素高的条
        var minWorldW = o.MinSide / wantedScale;
        var minWorldH = o.MinSide / wantedScale;
        if (worldRect.Width < minWorldW || worldRect.Height < minWorldH)
        {
            var w2 = Math.Max(worldRect.Width, minWorldW);
            var h2 = Math.Max(worldRect.Height, minWorldH);
            worldRect = new RectD(
                worldRect.X - (w2 - worldRect.Width) / 2,
                worldRect.Y - (h2 - worldRect.Height) / 2,
                w2, h2);
            note += "；内容过小已补足最小画布";
        }

        var w = worldRect.Width * wantedScale;
        var h = worldRect.Height * wantedScale;

        var clamped = false;
        var scale = wantedScale;
        var longest = Math.Max(w, h);
        if (longest > o.MaxSide)
        {
            scale = wantedScale * (o.MaxSide / longest);
            clamped = true;
            w = worldRect.Width * scale;
            h = worldRect.Height * scale;
        }

        var pxW = Math.Max(1, (int)Math.Ceiling(w));
        var pxH = Math.Max(1, (int)Math.Ceiling(h));

        // 视口：让 worldRect 恰好铺满导出画布
        var vp = new ViewportState
        {
            Zoom = scale,
            PanX = worldRect.X,
            PanY = worldRect.Y
        };

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            renderer.Render(dc, page, vp, pxW, pxH);

        var bmp = new RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));

        using var ms = new MemoryStream();
        encoder.Save(ms);

        var fullNote = clamped
            ? $"{note}；已按单边上限 {o.MaxSide}px 收敛（实际倍率 {scale:0.###}）"
            : $"{note}；倍率 {scale:0.###}";

        return new PngExportResult(ms.ToArray(), pxW, pxH, scale, clamped, fullNote);
    }

    /// <summary>把一页导出到指定路径。</summary>
    public static PngExportResult SavePage(
        Page page, SmoothGeometryBuilder geometry, string path, PngExportOptions? options = null)
    {
        var result = RenderPage(page, geometry, options);
        var full = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? path : path + ".png";

        var dir = Path.GetDirectoryName(Path.GetFullPath(full));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllBytes(full, result.Bytes);
        return result;
    }

    /// <summary>
    /// 批量导出整本画板。文件名形如 <c>名称-第01页.png</c>，
    /// 返回实际写出的文件列表（供"导出完成，打开文件夹"使用）。
    /// </summary>
    public static IReadOnlyList<string> ExportAllPages(
        WhiteboardDocument document,
        SmoothGeometryBuilder geometry,
        string directory,
        string baseName,
        PngExportOptions? options = null)
    {
        Directory.CreateDirectory(directory);

        var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(baseName) ? "白板" : baseName);
        var written = new List<string>();

        for (var i = 0; i < document.Pages.Count; i++)
        {
            var page = document.Pages[i];
            var file = Path.Combine(directory, $"{safeName}-第{i + 1:00}页.png");
            SavePage(page, geometry, file, options);
            written.Add(file);
        }

        return written;
    }

    /// <summary>求导出用的世界矩形。</summary>
    private static (RectD Rect, string Note) ResolveWorldRect(
        Page page, PngExportOptions o, ViewportState? viewportOverride,
        double screenWidth, double screenHeight)
    {
        if (!o.UseContentBounds && viewportOverride is not null)
        {
            var vp = viewportOverride;
            var tl = vp.ScreenToWorld(0, 0);
            var br = vp.ScreenToWorld(screenWidth, screenHeight);
            return (new RectD(tl.X, tl.Y, Math.Max(br.X - tl.X, 1), Math.Max(br.Y - tl.Y, 1)), "按当前视口导出");
        }

        var bounds = page.ContentBounds();

        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return (new RectD(0, 0, o.EmptyPageWidth, o.EmptyPageHeight), "空页，按默认画布导出");
        }

        var m = Math.Max(0, o.MarginWorld);
        return (new RectD(bounds.X - m, bounds.Y - m, bounds.Width + m * 2, bounds.Height + m * 2),
            $"按内容裁剪（内容 {bounds.Width:0}×{bounds.Height:0}）");
    }

    /// <summary>把任意标题变成合法文件名（Windows 不允许 <c>\ / : * ? " &lt; &gt; |</c>）。</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim().TrimEnd('.');
        if (cleaned.Length == 0) cleaned = "白板";
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}
