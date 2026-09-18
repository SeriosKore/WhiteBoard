using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Rendering;

/// <summary>
/// 文字测量与几何构建。
///
/// **为什么要单独一个类**：Core 不依赖任何文字引擎（要保持 UI 无关、可单测），
/// 所以"一段文字占多大地方"必须由渲染层算出来，再把结果交给 Core 存储。
/// 工具层（新建文本）与渲染层（画文字）都用这里的同一份实现，
/// 因此"编辑框的大小"和"提交后文字的大小"必然一致，不会出现提交瞬间跳一下。
/// </summary>
public static class TextGeometry
{
    /// <summary>文本框最大宽度（世界单位）：超过就自动换行，避免一行文字无限长。</summary>
    public const double DefaultMaxWidth = 900;

    /// <summary>构造 <see cref="FormattedText"/>（渲染与测量共用，保证两者一致）。</summary>
    public static FormattedText Build(
        string text, double fontSize, string fontFamily, bool bold, bool italic, double maxWidth)
    {
        var typeface = new Typeface(
            new FontFamily(string.IsNullOrWhiteSpace(fontFamily) ? TextObject.DefaultFontFamily : fontFamily),
            italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);

        var ft = new FormattedText(
            text ?? "",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(fontSize, 1),
            Brushes.Black,
            96)   // 与设备无关：1 世界单位 = 1 DIP，字号随视口缩放由渲染层负责
        {
            TextAlignment = TextAlignment.Left
        };

        if (maxWidth > 0) ft.MaxTextWidth = maxWidth;

        return ft;
    }

    /// <summary>测量一段文字占用的世界单位尺寸。</summary>
    /// <remarks>
    /// 高度用 <c>Height</c>（含行距），而不是 <c>Baseline</c>：
    /// 命中测试与选中框用的是外接矩形，用基线算会"框不住字"。
    /// 宽度至少给 1，避免空字符串得到 0 宽导致后续退化。
    /// </remarks>
    public static (double Width, double Height) Measure(
        string text, double fontSize, string fontFamily = TextObject.DefaultFontFamily,
        bool bold = false, bool italic = false, double maxWidth = DefaultMaxWidth)
    {
        var ft = Build(text, fontSize, fontFamily, bold, italic, maxWidth);
        return (Math.Max(ft.WidthIncludingTrailingWhitespace, 1), Math.Max(ft.Height, 1));
    }

    /// <summary>构建文本对象在**局部坐标**下的填充几何（供渲染/差集/命中统一使用）。</summary>
    public static Geometry BuildLocalGeometry(TextObject obj)
    {
        var ft = Build(obj.Text, obj.FontSize, obj.FontFamily, obj.Bold, obj.Italic,
            Math.Max(obj.LocalWidth, 1));

        // 从左上角起排（局部原点即文本框左上角）
        var geo = ft.BuildGeometry(new Point(0, 0));
        if (geo is null) return Geometry.Empty;

        geo.Freeze();
        return geo;
    }
}
