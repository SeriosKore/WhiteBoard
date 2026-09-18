using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace WhiteBoard.Poc.InkLatency;

public interface IInkRoute
{
    string Name { get; }
    string Description { get; }

    /// <summary>true = 湿笔迹由方案自己处理（InkCanvas 路线）；false = 需要外层逐点喂给 <see cref="OnWetPoint"/>。</summary>
    bool DrawsOwnWetInk { get; }

    FrameworkElement View { get; }

    void OnWetPoint(InputPoint p);
    void OnStrokeEnd(InkStroke committed);
    void ClearInk();
    void SetPen(Color color, double width);
    int StrokeCount { get; }

    /// <summary>以编程方式加入一条已完成的笔迹（供自动化冒烟自检使用，不经过真实输入）。</summary>
    void AddSyntheticStroke(IReadOnlyList<InputPoint> points, Color color, double width);
}

// ─────────────────────────────────────────────────────────────────────────────
// 路线 0：裸 InkCanvas 基线（不做任何分层、不做手绘风格）
// 用来回答："WPF 自己最快能到什么程度"
// ─────────────────────────────────────────────────────────────────────────────
public sealed class BaselineRoute : IInkRoute
{
    private readonly InkCanvas _ink = new()
    {
        Background = new SolidColorBrush(Colors.Transparent),
        EditingMode = InkCanvasEditingMode.Ink
    };

    private Color _color = Colors.White;
    private double _width = 4;

    public BaselineRoute()
    {
        _ink.DefaultDrawingAttributes = MakeAttributes(_color, _width);
    }

    public string Name => "baseline";
    public string Description => "裸 InkCanvas（基线）";
    public bool DrawsOwnWetInk => true;
    public FrameworkElement View => _ink;
    public int StrokeCount => _ink.Strokes.Count;

    public void OnWetPoint(InputPoint p) { /* InkCanvas 自己处理 */ }
    public void OnStrokeEnd(InkStroke committed) { /* InkCanvas 已经画好了 */ }

    public void AddSyntheticStroke(IReadOnlyList<InputPoint> points, Color color, double width)
    {
        var pts = new StylusPointCollection();
        foreach (var p in points)
            pts.Add(new StylusPoint(p.X, p.Y, p.Pressure));
        _ink.Strokes.Add(new Stroke(pts, MakeAttributes(color, width)));
    }

    public void ClearInk() => _ink.Strokes.Clear();

    public void SetPen(Color color, double width)
    {
        _color = color;
        _width = width;
        _ink.DefaultDrawingAttributes = MakeAttributes(color, width);
    }

    internal static DrawingAttributes MakeAttributes(Color color, double width) => new()
    {
        Color = color,
        Width = width,
        Height = width,
        StylusTip = StylusTip.Ellipse,
        FitToCurve = true,
        IgnorePressure = false
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// 路线 A1：文档层（自绘手绘风格） + InkCanvas 湿笔迹叠加
// 抬笔后把笔迹交给文档层，并等文档层渲染出一帧再清掉湿笔迹（避免"抬手闪一下"）
// ─────────────────────────────────────────────────────────────────────────────
public sealed class OverlayRoute : IInkRoute
{
    private readonly CanvasHost _host = new() { DrawLive = false };
    private readonly InkCanvas _ink = new()
    {
        Background = new SolidColorBrush(Colors.Transparent),
        EditingMode = InkCanvasEditingMode.Ink
    };
    private readonly Grid _root = new();
    private bool _pendingClear;

    public OverlayRoute()
    {
        _root.Children.Add(_host);
        _root.Children.Add(_ink);
        _ink.DefaultDrawingAttributes = BaselineRoute.MakeAttributes(Colors.White, 4);
    }

    public string Name => "a1";
    public string Description => "A1 · 文档层自绘 + InkCanvas 湿笔迹叠加";
    public bool DrawsOwnWetInk => true;
    public FrameworkElement View => _root;
    public int StrokeCount => _host.Strokes.Count;

    public void OnWetPoint(InputPoint p) { }

    public void OnStrokeEnd(InkStroke committed)
    {
        _host.Strokes.Add(committed);
        _host.InvalidateVisual();

        // 文档层已经在新一帧里画出了这条笔迹之后再移除湿笔迹
        if (!_pendingClear)
        {
            _pendingClear = true;
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                CompositionTarget.Rendering -= handler;
                _pendingClear = false;
                if (_ink.Strokes.Count > 0)
                    _ink.Strokes.RemoveAt(_ink.Strokes.Count - 1);
            };
            CompositionTarget.Rendering += handler;
        }
    }

    public void ClearInk()
    {
        _host.Strokes.Clear();
        _ink.Strokes.Clear();
        _host.InvalidateVisual();
    }

    public void AddSyntheticStroke(IReadOnlyList<InputPoint> points, Color color, double width)
    {
        var s = new InkStroke { Id = points.Count * 31 + 7, Color = color, Width = width };
        s.Points.AddRange(points);
        _host.Strokes.Add(s);
        _host.InvalidateVisual();
    }

    public void SetPen(Color color, double width)
    {
        _host.PenColor = color;
        _host.PenWidth = width;
        _ink.DefaultDrawingAttributes = BaselineRoute.MakeAttributes(color, width);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 路线 A2：单层自绘（文档 + 实时笔迹都在同一个 OnRender 里）
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SingleLayerRoute : IInkRoute
{
    private readonly CanvasHost _host = new() { DrawLive = true };
    private InkStroke? _live;

    public string Name => "a2";
    public string Description => "A2 · 单层自绘（湿笔迹也在 OnRender 里画）";
    public bool DrawsOwnWetInk => false;
    public FrameworkElement View => _host;
    public int StrokeCount => _host.Strokes.Count;

    public void OnWetPoint(InputPoint p)
    {
        _live ??= new InkStroke
        {
            Id = Environment.TickCount & 0x3FFFFFF,
            Color = _host.PenColor,
            Width = _host.PenWidth
        };
        _live.Add(p);

        // 实时笔迹只保留最近若干点用于重绘代价控制（正式实现会做增量绘制）
        _host.Live = _live;
        _host.InvalidateVisual();
    }

    public void OnStrokeEnd(InkStroke committed)
    {
        _live = null;
        _host.Live = null;
        _host.Strokes.Add(committed);
        _host.InvalidateVisual();
    }

    public void ClearInk()
    {
        _live = null;
        _host.Live = null;
        _host.Strokes.Clear();
        _host.InvalidateVisual();
    }

    public void SetPen(Color color, double width)
    {
        _host.PenColor = color;
        _host.PenWidth = width;
    }

    public void AddSyntheticStroke(IReadOnlyList<InputPoint> points, Color color, double width)
    {
        var s = new InkStroke { Id = points.Count * 17 + 3, Color = color, Width = width };
        s.Points.AddRange(points);
        OnStrokeEnd(s);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 画布宿主：自绘已提交笔迹（手绘风格），可选自绘实时笔迹
// ─────────────────────────────────────────────────────────────────────────────
public sealed class CanvasHost : Control
{
    public List<InkStroke> Strokes { get; } = [];
    public InkStroke? Live { get; set; }
    public bool DrawLive { get; set; }
    public Color PenColor { get; set; } = Colors.White;
    public double PenWidth { get; set; } = 4;
    public Color BoardColor { get; set; } = Color.FromRgb(0x2F, 0x4F, 0x3A);

    /// <summary>视口缩放（世界坐标 → 屏幕）。几何始终按世界坐标构建，缩放只改变换矩阵，
    /// 因此**放大是无损的**（矢量重绘，不是位图放大）。</summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>缩放中心（屏幕坐标）。</summary>
    public Point ZoomCenter { get; set; }

    public CanvasHost()
    {
        Background = new SolidColorBrush(BoardColor);
        ClipToBounds = true;

        // 抗锯齿：几何渲染保持默认（Unspecified = 开启抗锯齿），
        // 并禁用像素对齐/位图缩放降质，避免边缘出现阶梯与模糊。
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SnapsToDevicePixels = false;
        UseLayoutRounding = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle(new SolidColorBrush(BoardColor), null, new Rect(0, 0, w, h));

        var zoomed = Math.Abs(Zoom - 1.0) > 1e-9;
        if (zoomed)
        {
            var cx = ZoomCenter.X == 0 ? w / 2 : ZoomCenter.X;
            var cy = ZoomCenter.Y == 0 ? h / 2 : ZoomCenter.Y;
            dc.PushTransform(new ScaleTransform(Zoom, Zoom, cx, cy));
        }

        foreach (var s in Strokes)
        {
            // 平滑风：几何填充（与湿笔迹同源，逐像素一致）
            var brush = new SolidColorBrush(s.Color);
            brush.Freeze();
            dc.DrawGeometry(brush, null, s.GetSmoothGeometry());
        }

        if (DrawLive && Live is { Points.Count: > 0 } live)
        {
            // 实时笔迹：同样走 Stroke 几何填充（每次重建），
            // 与干笔迹**同一套构建方式** → 抬笔时外观零跳变
            var liveBrush = new SolidColorBrush(live.Color);
            liveBrush.Freeze();
            dc.DrawGeometry(liveBrush, null, live.BuildSmooth());
        }

        if (zoomed) dc.Pop();
    }
}
