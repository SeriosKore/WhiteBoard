using System.Text.Json;
using System.Text.Json.Serialization;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Core.Storage;

/// <summary>
/// <c>.wb</c> 画板文件的**数据契约**（DTO 层）。
///
/// 为什么不直接序列化模型类：模型类会随实现演进（新增字段、改名、调整内部结构），
/// 而**文件格式一旦发布就必须向后兼容**。中间隔一层 DTO，才能保证"改代码不炸老文件"。
/// 这一层同时承担三件事：字段改名保护、缺省值兜底、版本号演进。
///
/// 包结构（ZIP）：
/// <code>
/// xxx.wb
///  ├─ format.json     格式标识 + schemaVersion + 生成信息
///  ├─ document.json   文档内容（本文件的 DTO）
///  └─ preview.png     第 1 页缩略图（可选；便于"最近文件"与资源管理器预览）
/// </code>
/// </summary>
public static class WbFormat
{
    /// <summary>格式标识（防止把别的 ZIP 当成画板）。</summary>
    public const string Magic = "WhiteBoard";

    /// <summary>当前写出的格式版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>坐标保存精度：4 位小数（0.0001 世界单位，远小于 1 像素，肉眼不可能察觉）。</summary>
    public const int CoordinateDecimals = 4;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly JsonSerializerOptions JsonOptionsIndented = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>按保存精度取整（避免把 0.30000000000000004 这类浮点噪声写进文件）。</summary>
    public static double R(double v)
        => double.IsFinite(v) ? Math.Round(v, CoordinateDecimals, MidpointRounding.AwayFromZero) : 0;

    public static double[] R(IEnumerable<double> values) => values.Select(R).ToArray();

    // ── 映射：模型 → DTO ─────────────────────────────────────────────────

    public static FormatDto ToDto(WhiteboardDocument doc, string? appVersion)
    {
        var dto = new FormatDto
        {
            Magic = Magic,
            SchemaVersion = CurrentSchemaVersion,
            AppVersion = appVersion,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            Document = new DocumentDto
            {
                Id = doc.Id,
                CurrentPageIndex = doc.CurrentPageIndex
            }
        };

        foreach (var page in doc.Pages)
        {
            var pd = new PageDto
            {
                Id = page.Id,
                BackgroundType = page.BackgroundType,
                Zoom = R(page.Viewport.Zoom),
                PanX = R(page.Viewport.PanX),
                PanY = R(page.Viewport.PanY)
            };

            foreach (var obj in page.InRenderOrder())
                pd.Objects.Add(ToDto(obj));

            dto.Document.Pages.Add(pd);
        }

        return dto;
    }

    private static ObjectDto ToDto(ShapeObject obj)
    {
        var dto = new ObjectDto
        {
            Id = obj.Id,
            Kind = obj.Kind,
            Z = obj.ZIndex,
            X = R(obj.X),
            Y = R(obj.Y),
            W = R(obj.LocalWidth),
            H = R(obj.LocalHeight),
            Rot = R(obj.Rotation),
            Sx = R(obj.ScaleX),
            Sy = R(obj.ScaleY),
            Color = obj.Color
        };

        switch (obj)
        {
            case FreehandObject f:
                dto.Pen = R(f.PenWidth);
                dto.Points = FlattenPoints(f.Points);
                break;

            case TextObject t:
                dto.Text = t.Text;
                dto.FontSize = R(t.FontSize);
                dto.FontFamily = t.FontFamily;
                dto.Bold = t.Bold;
                dto.Italic = t.Italic;
                break;

            case LineObject l:
                dto.Pen = R(l.PenWidth);
                dto.X1 = R(l.LocalX1);
                dto.Y1 = R(l.LocalY1);
                dto.X2 = R(l.LocalX2);
                dto.Y2 = R(l.LocalY2);
                break;

            case StrokedShapeObject s:
                dto.Pen = R(s.PenWidth);
                break;
        }

        if (obj.Erasures.Count > 0)
        {
            var flat = new List<double>(obj.Erasures.Count * 3);
            foreach (var e in obj.Erasures)
            {
                flat.Add(R(e.X));
                flat.Add(R(e.Y));
                flat.Add(R(e.Radius));
            }
            dto.Erasures = flat.ToArray();
        }

        return dto;
    }

    /// <summary>点集打平为 <c>[x,y,压力, x,y,压力 …]</c>：比对象数组小得多，且解析快。</summary>
    private static double[] FlattenPoints(IReadOnlyList<InkPoint> points)
    {
        var flat = new double[points.Count * 3];
        for (var i = 0; i < points.Count; i++)
        {
            flat[i * 3] = R(points[i].X);
            flat[i * 3 + 1] = R(points[i].Y);
            flat[i * 3 + 2] = R(points[i].Pressure);
        }
        return flat;
    }

    // ── 映射：DTO → 模型 ─────────────────────────────────────────────────

    /// <summary>
    /// 由 DTO 重建文档。未知类型的对象会被**跳过并记入警告**，而不是让整个文件打不开
    /// （向前兼容：新版本程序存的文件，老版本至少能打开认识的图元）。
    /// </summary>
    public static (WhiteboardDocument Document, List<string> Warnings) FromDto(FormatDto dto)
    {
        var warnings = new List<string>();
        var doc = new WhiteboardDocument { Id = dto.Document.Id <= 0 ? 1 : dto.Document.Id };

        if (dto.Document.Pages.Count == 0)
        {
            warnings.Add("文件里没有任何页面，已补一个空白页");
            doc.EnsureAtLeastOnePage();
            return (doc, warnings);
        }

        foreach (var pd in dto.Document.Pages)
        {
            var page = new Page
            {
                Id = pd.Id <= 0 ? doc.AllocatePageId() : pd.Id,
                BackgroundType = string.IsNullOrWhiteSpace(pd.BackgroundType) ? "solid" : pd.BackgroundType
            };

            page.Viewport.Zoom = pd.Zoom <= 0 ? 1.0 : pd.Zoom;
            page.Viewport.PanX = pd.PanX;
            page.Viewport.PanY = pd.PanY;

            foreach (var od in pd.Objects)
            {
                var obj = FromDto(od, warnings);
                if (obj is null) continue;

                // 直接入列表（不走 Page.Add），以**保留文件里的 Z 序**
                page.Objects.Add(obj);
            }

            doc.Pages.Add(page);
        }

        doc.CurrentPageIndex = dto.Document.CurrentPageIndex;
        doc.RebuildIdCounters();

        // 页 Id 可能被上面的重建改过，再确保唯一
        var seen = new HashSet<int>();
        foreach (var p in doc.Pages)
            if (!seen.Add(p.Id))
                warnings.Add($"页面 Id {p.Id} 重复（文件可能被手工改过）");

        return (doc, warnings);
    }

    private static ShapeObject? FromDto(ObjectDto od, List<string> warnings)
    {
        ShapeObject? obj = od.Kind switch
        {
            "freehand" => BuildFreehand(od),
            "text" => BuildText(od),
            "line" => BuildLine(od),
            "rect" => BuildRect(od),
            "ellipse" => BuildEllipse(od),
            _ => null
        };

        if (obj is null)
        {
            warnings.Add($"跳过 1 个无法识别的图元（kind=\"{od.Kind}\"）—— 可能由更新版本的程序创建");
            return null;
        }

        obj.ZIndex = od.Z;
        obj.Rotation = od.Rot;
        obj.ScaleX = od.Sx <= 0 ? 1.0 : od.Sx;
        obj.ScaleY = od.Sy <= 0 ? 1.0 : od.Sy;
        obj.Color = string.IsNullOrWhiteSpace(od.Color) ? "#F5F5F0" : od.Color;

        if (od.Erasures is { Length: >= 3 })
        {
            for (var i = 0; i + 2 < od.Erasures.Length; i += 3)
                obj.Erasures.Add(new EraserCircle(od.Erasures[i], od.Erasures[i + 1], od.Erasures[i + 2]));
        }

        return obj;
    }

    private static ShapeObject? BuildFreehand(ObjectDto od)
    {
        var flat = od.Points;
        if (flat is null || flat.Length < 3) return null;

        var obj = new FreehandObject
        {
            Id = od.Id,
            X = od.X,
            Y = od.Y,
            LocalWidth = od.W,
            LocalHeight = od.H,
            PenWidth = od.Pen <= 0 ? 3.0 : od.Pen
        };

        for (var i = 0; i + 2 < flat.Length; i += 3)
            obj.Points.Add(new InkPoint(flat[i], flat[i + 1], (float)flat[i + 2]));

        return obj.Points.Count > 0 ? obj : null;
    }

    /// <summary>
    /// 文本对象。**尺寸直接从文件读**，不重新测量——
    /// 换了机器、换了字体版本时测量结果会略有差异，若重测会导致老文件里的文字
    /// 位置/外框轻微漂移（"打开后排版变了"）。
    /// 空文本也照原样读回来（不丢），以保证"存读一致"；UI 侧不会产生空文本对象。
    /// </summary>
    private static ShapeObject BuildText(ObjectDto od)
    {
        return new TextObject
        {
            Id = od.Id,
            X = od.X,
            Y = od.Y,
            LocalWidth = od.W <= 0 ? 1 : od.W,
            LocalHeight = od.H <= 0 ? 1 : od.H,
            Text = od.Text ?? "",
            FontSize = od.FontSize <= 0 ? TextObject.MediumFontSize : od.FontSize,
            FontFamily = string.IsNullOrWhiteSpace(od.FontFamily) ? TextObject.DefaultFontFamily : od.FontFamily,
            Bold = od.Bold,
            Italic = od.Italic
        };
    }

    private static ShapeObject BuildLine(ObjectDto od) => new LineObject
    {
        Id = od.Id,
        X = od.X,
        Y = od.Y,
        LocalWidth = od.W,
        LocalHeight = od.H,
        PenWidth = od.Pen <= 0 ? 3.0 : od.Pen,
        LocalX1 = od.X1,
        LocalY1 = od.Y1,
        LocalX2 = od.X2,
        LocalY2 = od.Y2
    };

    private static ShapeObject BuildRect(ObjectDto od) => new RectObject    {
        Id = od.Id,
        X = od.X,
        Y = od.Y,
        LocalWidth = od.W,
        LocalHeight = od.H,
        PenWidth = od.Pen <= 0 ? 3.0 : od.Pen
    };

    private static ShapeObject BuildEllipse(ObjectDto od) => new EllipseObject
    {
        Id = od.Id,
        X = od.X,
        Y = od.Y,
        LocalWidth = od.W,
        LocalHeight = od.H,
        PenWidth = od.Pen <= 0 ? 3.0 : od.Pen
    };

    // ── DTO 定义 ─────────────────────────────────────────────────────────

    public sealed class FormatDto
    {
        [JsonPropertyName("magic")] public string Magic { get; set; } = WbFormat.Magic;
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        [JsonPropertyName("appVersion")] public string? AppVersion { get; set; }
        [JsonPropertyName("createdUtc")] public string? CreatedUtc { get; set; }
        [JsonPropertyName("document")] public DocumentDto Document { get; set; } = new();
    }

    public sealed class DocumentDto
    {
        [JsonPropertyName("id")] public int Id { get; set; } = 1;
        [JsonPropertyName("currentPageIndex")] public int CurrentPageIndex { get; set; }
        [JsonPropertyName("pages")] public List<PageDto> Pages { get; set; } = [];
    }

    public sealed class PageDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("backgroundType")] public string? BackgroundType { get; set; }
        [JsonPropertyName("zoom")] public double Zoom { get; set; } = 1.0;
        [JsonPropertyName("panX")] public double PanX { get; set; }
        [JsonPropertyName("panY")] public double PanY { get; set; }
        [JsonPropertyName("objects")] public List<ObjectDto> Objects { get; set; } = [];
    }

    public sealed class ObjectDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("z")] public int Z { get; set; }
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }
        [JsonPropertyName("w")] public double W { get; set; }
        [JsonPropertyName("h")] public double H { get; set; }
        [JsonPropertyName("rot")] public double Rot { get; set; }
        [JsonPropertyName("sx")] public double Sx { get; set; } = 1.0;
        [JsonPropertyName("sy")] public double Sy { get; set; } = 1.0;
        [JsonPropertyName("color")] public string Color { get; set; } = "#F5F5F0";
        [JsonPropertyName("pen")] public double Pen { get; set; } = 3.0;

        /// <summary>freehand：[x,y,压力, …]</summary>
        [JsonPropertyName("points")] public double[]? Points { get; set; }

        /// <summary>line：局部坐标两端点。</summary>
        [JsonPropertyName("x1")] public double X1 { get; set; }
        [JsonPropertyName("y1")] public double Y1 { get; set; }
        [JsonPropertyName("x2")] public double X2 { get; set; }
        [JsonPropertyName("y2")] public double Y2 { get; set; }

        /// <summary>擦除遮罩：<c>[x,y,半径, …]</c>（局部坐标，ADR-18）。</summary>
        [JsonPropertyName("erasures")] public double[]? Erasures { get; set; }

        // ── text（S1 只做静态文本、不旋转）────────────────────────────────
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("fontSize")] public double FontSize { get; set; }
        [JsonPropertyName("fontFamily")] public string? FontFamily { get; set; }
        [JsonPropertyName("bold")] public bool Bold { get; set; }
        [JsonPropertyName("italic")] public bool Italic { get; set; }
    }
}
