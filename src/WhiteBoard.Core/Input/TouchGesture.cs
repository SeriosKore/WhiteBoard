using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Core.Input;

/// <summary>
/// 双指缩放/平移手势。
///
/// 用法：<c>Begin</c> 时记录起始两指的中点与距离、以及起始视口；
/// 之后每次 <c>ApplyTo</c> 把当前两指状态映射到视口上——
/// **起始中点下的世界坐标始终跟随当前中点**，即"缩放中心跟随两指中点"（原需求）。
/// </summary>
public sealed class TouchGestureRecognizer
{
    private bool _active;
    private PointD _startMid;
    private double _startDistance;
    private ViewportState? _startViewport;

    public bool IsActive => _active;
    public PointD StartMid => _startMid;
    public double StartDistance => _startDistance;

    /// <summary>开始手势（至少两指）。</summary>
    public bool Begin(IReadOnlyList<PointerSample> touches, ViewportState viewport)
    {
        if (touches.Count < 2) return false;

        _startMid = Midpoint(touches);
        _startDistance = Math.Max(Distance(touches), 1e-6);
        _startViewport = viewport.Clone();
        _active = true;
        return true;
    }

    /// <summary>把当前两指状态应用到视口。</summary>
    public void ApplyTo(ViewportState viewport, IReadOnlyList<PointerSample> touches)
    {
        if (!_active || _startViewport is null || touches.Count < 2) return;

        var mid = Midpoint(touches);
        var distance = Math.Max(Distance(touches), 1e-6);
        var factor = distance / _startDistance;

        var newZoom = Math.Clamp(_startViewport.Zoom * factor, ViewportState.MinZoom, ViewportState.MaxZoom);
        viewport.Zoom = newZoom;

        // 起始中点对应的世界坐标，应保持在当前中点之下
        var world = _startViewport.ScreenToWorld(_startMid);
        viewport.PanX = world.X - mid.X / newZoom;
        viewport.PanY = world.Y - mid.Y / newZoom;
    }

    public void End() => _active = false;

    public static PointD Midpoint(IReadOnlyList<PointerSample> touches)
    {
        if (touches.Count == 0) return new PointD(0, 0);
        if (touches.Count == 1) return touches[0].Position;
        return new PointD((touches[0].X + touches[1].X) / 2, (touches[0].Y + touches[1].Y) / 2);
    }

    public static double Distance(IReadOnlyList<PointerSample> touches)
    {
        if (touches.Count < 2) return 0;
        var dx = touches[0].X - touches[1].X;
        var dy = touches[0].Y - touches[1].Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// 拖动阈值：位移超过阈值才算"拖动"，否则视为点击（决策 D2：阈值 4 px）。
/// 用于避免手抖把"点选"变成"拖动"。
/// </summary>
public sealed class DragTracker
{
    public double ThresholdPx { get; init; } = 4.0;

    private PointD _origin;
    private bool _started;

    public bool IsDragging { get; private set; }
    public PointD Origin => _origin;
    public PointD Current { get; private set; }

    /// <summary>按下时调用。</summary>
    public void Begin(PointD screenPoint)
    {
        _origin = screenPoint;
        Current = screenPoint;
        _started = true;
        IsDragging = false;
    }

    /// <summary>移动时调用；返回本次是否**刚刚越过**阈值（用于触发拖动起始）。</summary>
    public bool Update(PointD screenPoint)
    {
        if (!_started) return false;
        Current = screenPoint;

        if (!IsDragging)
        {
            var d = _origin.DistanceTo(screenPoint);
            if (d >= ThresholdPx)
            {
                IsDragging = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>屏幕位移（仅在拖动中有效）。</summary>
    public PointD Delta => new(Current.X - _origin.X, Current.Y - _origin.Y);

    public void End()
    {
        _started = false;
        IsDragging = false;
    }
}
