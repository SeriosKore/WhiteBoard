using System.Windows.Media;
using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Input;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Rendering.Tools;

/// <summary>
/// 拖拽式形状工具基类（直线/矩形/椭圆）。
/// 按下记起点、拖动显示橡皮筋预览、抬起提交为对象（一条命令入栈）。
/// 预览几何与提交后的干笔迹同源（都是"中心线加宽成轮廓再填充"），因此抬笔不跳变。
/// </summary>
public abstract class DragShapeTool : ITool
{
    private PointD _startWorld;
    private PointD _currentWorld;
    private bool _dragging;

    public abstract string Name { get; }
    public bool PalmActsAsEraser => false;

    /// <summary>最小拖拽距离（世界单位）；小于此值视为误触、不生成对象。</summary>
    public double MinDragWorld { get; init; } = 2.0;

    public void OnActivated(ToolContext ctx) => Reset();
    public void OnDeactivated(ToolContext ctx) => Reset();

    public bool OnPointer(PointerAction action, PointerSample sample, ToolContext ctx)
    {
        var world = ctx.ScreenToWorld(sample.Position);

        switch (action)
        {
            case PointerAction.Down:
                _startWorld = world;
                _currentWorld = world;
                _dragging = true;
                return true;

            case PointerAction.Move:
                if (!_dragging) return false;
                _currentWorld = world;
                return true;

            case PointerAction.Up:
                if (!_dragging) return false;
                _currentWorld = world;
                _dragging = false;
                Commit(ctx);
                return true;

            default:
                return false;
        }
    }

    private void Commit(ToolContext ctx)
    {
        if (_startWorld.DistanceTo(_currentWorld) < MinDragWorld)
        {
            Reset();
            return;
        }

        var obj = CreateObject(ctx.AllocateObjectId(), _startWorld, _currentWorld, ctx.PenColor, ctx.PenWidth);
        ctx.Commands.Execute(new AddObjectCommand(ctx.Page, obj));
        Reset();
    }

    /// <summary>由起止世界坐标创建具体对象。</summary>
    protected abstract ShapeObject CreateObject(int id, PointD start, PointD end, string color, double penWidth);

    /// <summary>预览中心线（世界坐标）。</summary>
    protected abstract IReadOnlyList<PointD> PreviewOutline(PointD start, PointD end);

    protected virtual bool PreviewClosed => false;

    public void Reset()
    {
        _dragging = false;
    }

    public void Cancel() => Reset();

    public Geometry? BuildPreview(ToolContext ctx)
    {
        if (!_dragging) return null;
        var pts = PreviewOutline(_startWorld, _currentWorld);
        return pts.Count < 2 ? null : SmoothGeometryBuilder.BuildLiveOutlineGeometry(pts, ctx.PenWidth, PreviewClosed);
    }
}

/// <summary>直线工具。</summary>
public sealed class LineTool : DragShapeTool
{
    public override string Name => "直线";

    protected override ShapeObject CreateObject(int id, PointD start, PointD end, string color, double penWidth)
        => LineObject.FromWorldPoints(id, start, end, color, penWidth);

    protected override IReadOnlyList<PointD> PreviewOutline(PointD start, PointD end) => [start, end];
}

/// <summary>矩形工具。</summary>
public sealed class RectTool : DragShapeTool
{
    public override string Name => "矩形";
    protected override bool PreviewClosed => true;

    protected override ShapeObject CreateObject(int id, PointD start, PointD end, string color, double penWidth)
        => RectObject.FromWorldCorners(id, start, end, color, penWidth);

    protected override IReadOnlyList<PointD> PreviewOutline(PointD start, PointD end)
        => [start, new PointD(end.X, start.Y), end, new PointD(start.X, end.Y)];
}

/// <summary>椭圆工具。</summary>
public sealed class EllipseTool : DragShapeTool
{
    public override string Name => "椭圆";
    protected override bool PreviewClosed => true;

    protected override ShapeObject CreateObject(int id, PointD start, PointD end, string color, double penWidth)
        => EllipseObject.FromWorldCorners(id, start, end, color, penWidth);

    /// <summary>
    /// 预览用**内接椭圆**的多边形近似（36 边形）——提交后由渲染层用"外椭圆−内椭圆"生成精确圆环，
    /// 预览只需视觉上接近。
    /// </summary>
    protected override IReadOnlyList<PointD> PreviewOutline(PointD start, PointD end)
    {
        const int segments = 36;
        var cx = (start.X + end.X) / 2;
        var cy = (start.Y + end.Y) / 2;
        var rx = Math.Abs(end.X - start.X) / 2;
        var ry = Math.Abs(end.Y - start.Y) / 2;

        var pts = new List<PointD>(segments);
        for (var i = 0; i < segments; i++)
        {
            var a = i * 2 * Math.PI / segments;
            pts.Add(new PointD(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a)));
        }
        return pts;
    }
}
