using System.Windows.Media;
using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Input;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Rendering.Tools;

/// <summary>
/// 橡皮：**遮罩保留 + 延迟烘焙**（ADR-18）。
///
/// 与"把笔迹切碎成多段"的做法相比，这里只往命中的对象上追加擦除圆：
/// <list type="bullet">
/// <item>一笔仍然是**一个对象**——选中/撤销/移动的粒度不变，撤销仍是"回退这几次擦除"；</item>
/// <item>实测几何重算代价 1.07 ms（最坏），而切碎方案会为 200~1200 点的笔迹产生 11~25 个对象；</item>
/// <item>擦到面积归零的对象会被删除，且**随同一条命令一起撤销**（需求的"结果为空则删除"）。</item>
/// </list>
///
/// 手掌接触（ADR-11 / 决策 D3）：橡皮工具下 <see cref="PalmActsAsEraser"/> 为 true，
/// 掌拒逻辑不再拦截手掌，而是按接触面积估算**大号橡皮**直径（60~420 px）。
/// </summary>
public sealed class EraserTool : ITool
{
    private readonly SmoothGeometryBuilder _geometry;
    private readonly HitTester _hits;

    private EraseCommand? _active;
    private PointD _lastWorld;
    private bool _erasing;
    private double _lastDiameterPx;

    public EraserTool(SmoothGeometryBuilder geometry)
    {
        _geometry = geometry;
        _hits = new HitTester(geometry);
    }

    public string Name => "橡皮";

    /// <summary>橡皮工具下，手掌接触转为大号橡皮（ADR-11）。</summary>
    public bool PalmActsAsEraser => true;

    /// <summary>手指/鼠标时的默认橡皮直径（屏幕像素）。</summary>
    public double DiameterPx { get; set; } = 60;

    /// <summary>笔（Stylus）橡皮直径：比手指小得多，便于精细擦除。</summary>
    public double StylusDiameterPx { get; set; } = 24;

    /// <summary>本次擦除命中的对象数（诊断用）。</summary>
    public int LastErasedCount { get; private set; }

    /// <summary>最后一次用的屏幕直径（手掌会放大，状态栏可显示）。</summary>
    public double LastDiameterPx => _lastDiameterPx;

    public void OnActivated(ToolContext ctx) => Cancel();
    public void OnDeactivated(ToolContext ctx) => Cancel();

    public bool OnPointer(PointerAction action, PointerSample sample, ToolContext ctx)
    {
        switch (action)
        {
            case PointerAction.Down:
                _active = new EraseCommand(ctx.Page);
                _erasing = true;
                _lastWorld = ctx.ScreenToWorld(sample.Position);
                Apply(ctx, sample, _lastWorld);
                return true;

            case PointerAction.Move:
                if (!_erasing) return false;
                // 沿两采样点之间**插值补点**，避免快速拖动时"漏擦"（出现虚线状残留）
                Stroke(ctx, sample);
                return true;

            case PointerAction.Up:
                if (!_erasing) return false;
                Stroke(ctx, sample);
                Commit(ctx);
                return true;

            default:
                return false;
        }
    }

    /// <summary>在上一位置与当前位置之间按半径步进补点。</summary>
    private void Stroke(ToolContext ctx, PointerSample sample)
    {
        _lastDiameterPx = DiameterFor(sample);
        var worldRadius = _lastDiameterPx / 2 / Math.Max(ctx.Viewport.Zoom, 1e-6);
        var current = ctx.ScreenToWorld(sample.Position);

        var distance = _lastWorld.DistanceTo(current);
        var step = Math.Max(worldRadius * 0.5, 1e-6);
        var steps = (int)Math.Min(512, Math.Ceiling(distance / step));

        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var p = new PointD(_lastWorld.X + (current.X - _lastWorld.X) * t,
                               _lastWorld.Y + (current.Y - _lastWorld.Y) * t);
            Apply(ctx, sample, p);
        }

        if (steps == 0) Apply(ctx, sample, current);
        _lastWorld = current;
    }

    private void Apply(ToolContext ctx, PointerSample sample, PointD worldCenter)
    {
        if (_active is null) return;

        var diameter = DiameterFor(sample);
        var radiusWorld = diameter / 2 / Math.Max(ctx.Viewport.Zoom, 1e-6);

        foreach (var obj in ctx.Page.Objects.ToList())
        {
            if (!obj.IsErasable) continue;

            // 粗筛：擦除圆与对象世界包围盒不相交就直接跳过
            var b = obj.WorldBounds;
            if (worldCenter.X + radiusWorld < b.X || worldCenter.X - radiusWorld > b.Right ||
                worldCenter.Y + radiusWorld < b.Y || worldCenter.Y - radiusWorld > b.Bottom)
                continue;

            // 精确筛：圆心到对象几何的距离超过半径 → 不命中（避免无意义地追加遮罩）
            if (!NearGeometry(obj, worldCenter, radiusWorld)) continue;

            var local = EraserCircle.FromWorld(worldCenter, radiusWorld, obj.LocalToWorld);
            if (!_active.Record(obj, local)) continue;

            LastErasedCount++;

            // 擦空即删（撤销时随命令一起恢复）。只在真的追加了遮罩后重算几何，
            // 否则会为了"检查是否擦空"白白重建几何。
            if (_geometry.GetLocalGeometry(obj).GetArea() <= 0.5)
                _active.MarkEmptied(obj);
        }
    }

    /// <summary>圆心是否落在几何上（含半径容差）。</summary>
    private bool NearGeometry(ShapeObject obj, PointD worldCenter, double worldRadius)
    {
        var local = obj.LocalToWorld.Invert().Transform(worldCenter);
        var geo = _geometry.GetLocalGeometry(obj);

        try
        {
            if (geo.FillContains(new System.Windows.Point(local.X, local.Y), worldRadius, ToleranceType.Absolute))
                return true;
        }
        catch (Exception)
        {
        }

        try
        {
            return geo.StrokeContains(
                new Pen(Brushes.Black, Math.Max(worldRadius * 2, 1)),
                new System.Windows.Point(local.X, local.Y),
                worldRadius,
                ToleranceType.Absolute);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>按指针类型决定屏幕直径：手掌 &gt; 手指 &gt; 笔。</summary>
    private double DiameterFor(PointerSample s)
    {
        if (s.Kind == PointerKind.Stylus) return StylusDiameterPx;

        // 触摸：按接触宽度估算（手掌会显著更宽）
        if (s.Kind == PointerKind.Touch && s.ContactWidth > 0)
        {
            var d = s.ContactWidth * 2.2;
            return Math.Clamp(d, DiameterPx, TouchAreaEstimator.MaxDiameter);
        }

        return DiameterPx;
    }

    private void Commit(ToolContext ctx)
    {
        _erasing = false;
        LastErasedCount = 0;

        // 遮罩在 Record 时已写入（这样橡皮是即时可见的），
        // 这里通过 Execute 触发 EraseCommand.Apply()（删除被擦空的对象）并入撤销栈。
        // CommandBase 的 Capture 只跑一次且 EraseCommand.Capture 为空实现，
        // 因此不会覆盖 Record 阶段收集的信息。
        if (_active is { HasEffect: true } cmd) ctx.Commands.Execute(cmd);

        _active = null;
    }

    public void Cancel()
    {
        _erasing = false;
        _active = null;
    }

    /// <summary>橡皮没有预览几何（擦除是即时生效的）。</summary>
    public Geometry? BuildPreview(ToolContext ctx) => null;
}
