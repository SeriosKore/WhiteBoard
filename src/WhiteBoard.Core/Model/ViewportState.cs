using WhiteBoard.Core.Geometry;

namespace WhiteBoard.Core.Model;

/// <summary>
/// 页面视口状态（每页独立保存）。
/// 变换约定：<c>screen = (world - Pan) * Zoom</c>，逆变换 <c>world = screen / Zoom + Pan</c>。
/// </summary>
public sealed class ViewportState
{
    public const double MinZoom = 0.1;   // 10%
    public const double MaxZoom = 50.0;  // 5000%（S2 要求"无损放大 50 倍"）

    private double _zoom = 1.0;

    public double Zoom
    {
        get => _zoom;
        set => _zoom = Math.Clamp(value, MinZoom, MaxZoom);
    }

    public double PanX { get; set; }
    public double PanY { get; set; }

    public PointD WorldToScreen(PointD world) => new((world.X - PanX) * Zoom, (world.Y - PanY) * Zoom);
    public PointD WorldToScreen(double wx, double wy) => WorldToScreen(new PointD(wx, wy));

    public PointD ScreenToWorld(PointD screen) => new(screen.X / Zoom + PanX, screen.Y / Zoom + PanY);
    public PointD ScreenToWorld(double sx, double sy) => ScreenToWorld(new PointD(sx, sy));

    /// <summary>当前可见的世界矩形（用于视口裁剪）。</summary>
    public RectD VisibleWorldRect(double screenWidth, double screenHeight)
    {
        var tl = ScreenToWorld(0, 0);
        var br = ScreenToWorld(screenWidth, screenHeight);
        return new RectD(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
    }

    /// <summary>以某个屏幕点为锚点缩放：该点下的世界坐标保持不动。</summary>
    public void ZoomAt(PointD screenAnchor, double newZoom)
    {
        var world = ScreenToWorld(screenAnchor);
        Zoom = newZoom;
        PanX = world.X - screenAnchor.X / Zoom;
        PanY = world.Y - screenAnchor.Y / Zoom;
    }

    /// <summary>按屏幕位移平移。</summary>
    public void PanByScreen(double dxScreen, double dyScreen)
    {
        PanX -= dxScreen / Zoom;
        PanY -= dyScreen / Zoom;
    }

    public ViewportState Clone() => new() { Zoom = Zoom, PanX = PanX, PanY = PanY };

    public override string ToString() => $"zoom={Zoom:0.###} pan=({PanX:0.#},{PanY:0.#})";
}
