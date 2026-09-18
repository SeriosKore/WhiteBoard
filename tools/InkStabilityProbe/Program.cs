using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteBoard.Core.Model;
using WhiteBoard.Rendering;

// 诊断小程序：定位"追加一个采样点后，到底哪些像素变了、离笔尖多远、离哪个采样点最近"
internal static class Program
{
    private const double PenWidth = 6.0;

    private static void Main()
    {
        var pts = new List<InkPoint>();
        for (var i = 0; i < 60; i++)
        {
            var jitterX = Math.Sin(i * 2.7) * 1.2;
            var jitterY = Math.Cos(i * 1.9) * 2.4;
            pts.Add(new InkPoint(i * 12 + jitterX, 100 + jitterY + Math.Sin(i * 0.4) * 30, 0.5f));
        }

        Console.WriteLine("比较两种平滑方式：追加一个采样点时，「已经画过的部分」被改动了多少");
        Console.WriteLine("（变化像素总数越小、波及距离越短，笔迹就越「下笔即固定」）");
        Console.WriteLine();

        foreach (var mode in new[] { FreehandSmoothing.FitToCurve, FreehandSmoothing.None })
        {
            SmoothGeometryBuilder.Smoothing = mode;

            var totalChanged = 0L;
            var worstDistance = 0.0;
            var worstStart = 0.0;      // 变化波及到"离起点最近"的距离（起点区域有没有被动过）
            var steps = 0;
            var startRegion = pts.Take(8).ToList();

            for (var k = 20; k < pts.Count - 1; k++)
            {
                var a = Render(SmoothGeometryBuilder.BuildLiveFreehandGeometry(pts.Take(k).ToList(), PenWidth));
                var b = Render(SmoothGeometryBuilder.BuildLiveFreehandGeometry(pts.Take(k + 1).ToList(), PenWidth));

                var tip = pts[k];
                var stride = a.PixelWidth * 4;
                var ba = new byte[stride * a.PixelHeight];
                var bb = new byte[stride * b.PixelHeight];
                a.CopyPixels(ba, stride, 0);
                b.CopyPixels(bb, stride, 0);

                for (var y = 0; y < a.PixelHeight; y++)
                {
                    for (var x = 0; x < a.PixelWidth; x++)
                    {
                        var i = y * stride + x * 4;
                        if (ba[i] == bb[i] && ba[i + 1] == bb[i + 1] && ba[i + 2] == bb[i + 2]) continue;

                        totalChanged++;
                        worstDistance = Math.Max(worstDistance, Math.Sqrt(Math.Pow(x - tip.X, 2) + Math.Pow(y - tip.Y, 2)));

                        // 该像素离"起点区域（前 8 个采样点）"最近有多远
                        var nearestStart = startRegion.Min(p => Math.Sqrt(Math.Pow(p.X - x, 2) + Math.Pow(p.Y - y, 2)));
                        if (worstStart == 0 || nearestStart < worstStart) worstStart = nearestStart;
                    }
                }

                steps++;
            }

            var modeName = mode == FreehandSmoothing.None ? "不平滑（现在的默认）" : "整条曲线拟合（改之前）";
            Console.WriteLine($"── {modeName} ──");
            Console.WriteLine($"  追加次数                : {steps}");
            Console.WriteLine($"  变化像素总数            : {totalChanged}（平均每次 {totalChanged / (double)steps:0}）");
            Console.WriteLine($"  变化波及的最远距离      : {worstDistance:0.0} px（相对笔尖；平均采样间距约 21 px）");
            Console.WriteLine($"  起点区域被波及到的最近距离: {worstStart:0.0} px（越大越好：说明起点没被动过）");
            Console.WriteLine();
        }

        SmoothGeometryBuilder.Smoothing = FreehandSmoothing.None;
    }

    private static RenderTargetBitmap Render(Geometry geo)
    {
        const int w = 1100, h = 400;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
            dc.DrawGeometry(Brushes.White, null, geo);
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        return bmp;
    }
}
