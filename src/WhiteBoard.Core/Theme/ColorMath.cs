using System.Globalization;

namespace WhiteBoard.Core.Theme;

/// <summary>颜色计算工具（sRGB / WCAG 相对亮度 / 对比度）。</summary>
public static class ColorMath
{
    /// <summary>解析 #RRGGBB 或 #AARRGGBB。失败抛 <see cref="FormatException"/>。</summary>
    public static (byte A, byte R, byte G, byte B) ParseHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            throw new FormatException("颜色值不能为空");

        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];

        if (s.Length == 6)
        {
            return (255,
                byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        if (s.Length == 8)
        {
            return (
                byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(s[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        throw new FormatException($"无法识别的颜色值：{hex}（应为 #RRGGBB 或 #AARRGGBB）");
    }

    public static bool IsValidHex(string? hex)
    {
        try { ParseHex(hex!); return true; }
        catch (Exception) { return false; }
    }

    /// <summary>sRGB 相对亮度（WCAG 2.x）。</summary>
    public static double RelativeLuminance(string hex)
    {
        var (_, r, g, b) = ParseHex(hex);
        return 0.2126 * Linear(r / 255.0) + 0.7152 * Linear(g / 255.0) + 0.0722 * Linear(b / 255.0);

        static double Linear(double c)
            => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>WCAG 对比度（1.0 ~ 21.0）。图形元素的可读性下限一般取 3:1。</summary>
    public static double ContrastRatio(string foregroundHex, string backgroundHex)
    {
        var l1 = RelativeLuminance(foregroundHex);
        var l2 = RelativeLuminance(backgroundHex);
        var (hi, lo) = l1 >= l2 ? (l1, l2) : (l2, l1);
        return (hi + 0.05) / (lo + 0.05);
    }
}
