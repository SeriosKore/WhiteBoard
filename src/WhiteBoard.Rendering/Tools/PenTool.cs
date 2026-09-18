using System.Windows.Media;
using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Input;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Rendering.Tools;

/// <summary>
/// 自由画笔。
///
/// 关键设计（M0 §六-D 实测结论）：
/// <list type="bullet">
/// <item>书写过程中**每帧重建**几何（`BuildLiveFreehandGeometry`），与干笔迹同源 → 抬笔零跳变；</item>
/// <item>实测每帧重建代价 0.11~0.83 ms（60Hz 帧预算的 0.6%~5%），无需节流；</item>
/// <item>抬笔时把累积点一次提交为 <see cref="FreehandObject"/>，并作为**一条命令**入当前页的撤销栈。</item>
/// </list>
/// </summary>
public sealed class PenTool : ITool
{
    private readonly List<InkPoint> _points = [];
    private bool _drawing;

    public string Name => "画笔";
    public bool PalmActsAsEraser => false;

    public int PointCount => _points.Count;
    public bool IsDrawing => _drawing;

    public void OnActivated(ToolContext ctx) => Cancel();
    public void OnDeactivated(ToolContext ctx) => Cancel();

    public bool OnPointer(PointerAction action, PointerSample sample, ToolContext ctx)
    {
        switch (action)
        {
            case PointerAction.Down:
                _points.Clear();
                _drawing = true;
                AddPoint(sample, ctx);
                return true;

            case PointerAction.Move:
                if (!_drawing) return false;
                AddPoint(sample, ctx);
                return true;

            case PointerAction.Up:
                if (!_drawing) return false;
                AddPoint(sample, ctx);
                Commit(ctx);
                return true;

            default:
                return false;
        }
    }

    private void AddPoint(PointerSample s, ToolContext ctx)
    {
        var world = ctx.ScreenToWorld(s.Position);
        _points.Add(new InkPoint(world.X, world.Y, s.SafePressure));
    }

    private void Commit(ToolContext ctx)
    {
        _drawing = false;

        if (_points.Count < 1)
        {
            _points.Clear();
            return;
        }

        var obj = FreehandObject.FromWorldPoints(ctx.AllocateObjectId(), _points, ctx.PenColor, ctx.PenWidth);
        ctx.Commands.Execute(new AddObjectCommand(ctx.Page, obj));
        _points.Clear();
    }

    /// <summary>丢弃当前笔画（第二指到来判定为手势时调用）。</summary>
    public void Cancel()
    {
        _drawing = false;
        _points.Clear();
    }

    public Geometry? BuildPreview(ToolContext ctx)
        => _drawing && _points.Count > 0
            ? SmoothGeometryBuilder.BuildLiveFreehandGeometry(_points, ctx.PenWidth)
            : null;
}
