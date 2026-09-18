using System.Windows;
using System.Windows.Media;
using WhiteBoard.Core.Geometry;

namespace WhiteBoard.Rendering;

/// <summary>Core 的几何类型与 WPF 几何类型互转（两者约定一致，转换是零成本重排）。</summary>
public static class GeometryInterop
{
    public static Matrix ToWpf(this in MatrixD m)
        => new(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY);

    public static MatrixD ToCore(this in Matrix m)
        => new(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY);

    public static Point ToWpf(this PointD p) => new(p.X, p.Y);

    public static PointD ToCore(this Point p) => new(p.X, p.Y);

    public static Rect ToWpf(this RectD r) => new(r.X, r.Y, r.Width, r.Height);

    public static RectD ToCore(this Rect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>解析 #RRGGBB / #AARRGGBB。</summary>
    public static Color ParseColor(string hex)
    {
        var (a, r, g, b) = Core.Theme.ColorMath.ParseHex(hex);
        return Color.FromArgb(a, r, g, b);
    }

    public static SolidColorBrush BrushFromHex(string hex)
    {
        var brush = new SolidColorBrush(ParseColor(hex));
        brush.Freeze();
        return brush;
    }
}
