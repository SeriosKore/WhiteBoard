using WhiteBoard.Core.Geometry;

namespace WhiteBoard.Core.Model;

/// <summary>
/// 文本对象（S1 只支持**静态文本 + 不旋转**，见 `计划书_S1_基础版.md` §5.7）。
///
/// 几个刻意的约定：
/// <list type="bullet">
/// <item>**文本不参与橡皮擦除**（<see cref="IsErasable"/> = false）：这是基线的要求
///       （"擦除时跳过文本/公式/化学式"）。理由很实际——文字被橡皮啃掉半个字没有意义，
///       要删就整体删（选择工具 + Del）；</item>
/// <item>**尺寸由外部测量后存进来**：Core 不依赖任何文字渲染引擎（要保持 UI 无关、可单测），
///       所以 <see cref="ShapeObject.LocalWidth"/>/<see cref="ShapeObject.LocalHeight"/>
///       由渲染层的文字测量结果填入；</item>
/// <item>**字号是世界单位**：放大 50 倍时文字跟着放大（与笔迹同一规则），
///       而不是像位图那样被拉糊；</item>
/// <item>局部坐标 <c>(0,0)</c> 是文本框**左上角**（与 WPF 的文字排版一致，转换最少）。</item>
/// </list>
/// </summary>
public sealed class TextObject : ShapeObject
{
    /// <summary>默认字体（中文优先，Windows 上一定有）。</summary>
    public const string DefaultFontFamily = "Microsoft YaHei";

    /// <summary>常用字号（世界单位）：小 / 中 / 大。</summary>
    public const double SmallFontSize = 24;
    public const double MediumFontSize = 36;
    public const double LargeFontSize = 56;

    public override string Kind => "text";

    /// <summary>文本内容（可以含换行）。</summary>
    public string Text { get; set; } = "";

    /// <summary>字号（世界单位）。</summary>
    public double FontSize { get; set; } = MediumFontSize;

    /// <summary>字体族名。</summary>
    public string FontFamily { get; set; } = DefaultFontFamily;

    public bool Bold { get; set; }
    public bool Italic { get; set; }

    /// <summary>基线要求：橡皮擦跳过文本（要删文字请用选择工具 + Del）。</summary>
    public override bool IsErasable => false;

    /// <summary>行数（只用于显示信息，按 <c>\n</c> 计）。</summary>
    public int LineCount => Text.Length == 0 ? 0 : Text.Split('\n').Length;

    /// <summary>首行内容（页面目录/状态栏摘要用）。</summary>
    public string FirstLine
    {
        get
        {
            var idx = Text.IndexOf('\n');
            var line = idx < 0 ? Text : Text[..idx];
            return line.Length > 24 ? line[..24] + "…" : line;
        }
    }

    public override RectD LocalBounds => new(0, 0, Math.Max(LocalWidth, 1), Math.Max(LocalHeight, 1));

    /// <summary>文本相关的状态必须进指纹，否则改了字渲染缓存不会失效（会显示旧文字）。</summary>
    public override long GeometryFingerprint
    {
        get
        {
            var h = new HashCode();
            h.Add(Kind);
            h.Add(Text);
            h.Add(FontSize);
            h.Add(FontFamily);
            h.Add(Bold);
            h.Add(Italic);
            h.Add(LocalWidth);
            h.Add(LocalHeight);
            h.Add(Erasures.Count);
            return h.ToHashCode();
        }
    }

    public override ShapeObject Clone(int newId)
    {
        var c = new TextObject
        {
            Id = newId,
            Text = Text,
            FontSize = FontSize,
            FontFamily = FontFamily,
            Bold = Bold,
            Italic = Italic
        };
        CopyBaseTo(c, newId);
        return c;
    }

    /// <summary>
    /// 由**世界坐标的左上角** + 已测量的尺寸创建。
    /// 测量必须由渲染层完成（Core 不依赖文字引擎），因此尺寸是参数。
    /// </summary>
    public static TextObject FromWorldTopLeft(
        int id, string text, PointD worldTopLeft, double measuredWidth, double measuredHeight,
        double fontSize, string color, string fontFamily = DefaultFontFamily,
        bool bold = false, bool italic = false)
    {
        return new TextObject
        {
            Id = id,
            X = worldTopLeft.X,
            Y = worldTopLeft.Y,
            LocalWidth = Math.Max(measuredWidth, 1),
            LocalHeight = Math.Max(measuredHeight, 1),
            Text = text,
            FontSize = fontSize,
            FontFamily = string.IsNullOrWhiteSpace(fontFamily) ? DefaultFontFamily : fontFamily,
            Bold = bold,
            Italic = italic,
            Color = color
        };
    }

    public override string ToString() => $"文本「{FirstLine}」（{LineCount} 行，{FontSize:0.#}号）";
}
