using System.Windows.Media;
using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Input;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Rendering.Tools;

/// <summary>选择方式（决策 D2：鼠标用框选，触屏用圈选更好按）。</summary>
public enum SelectionMode
{
    /// <summary>框选：拖出一个矩形，碰到即选中。</summary>
    Rectangle,

    /// <summary>圈选（套索）：沿手指轨迹围一圈，圈到的选中。</summary>
    Lasso
}

/// <summary>
/// 选择工具：点选 / 框选 / 圈选 / 拖动移动。
///
/// 关键取舍与陷阱：
/// <list type="number">
/// <item><b>点选取最上面的</b>（Z 序最大）：用户看到的是最上层，点到的也必须是它；</item>
/// <item><b>区分"点击"与"拖动"用 4px 阈值</b>（决策 D2）：手抖不应该变成移动；
///       阈值判定在**屏幕坐标**上做，缩放到 10 倍时也不会变得"一点就移"；</item>
/// <item><b>拖动时先实时改对象、抬手再补一条命令</b>——但 <see cref="MoveObjectsCommand"/>
///       是在**首次执行时**才捕获起始位置的，实时改过之后它捕获到的就是"已经移动过的位置"，
///       于是撤销会失效。这里采用"抬手时先还原到原位、再执行命令"的做法：
///       中间这一步在同一帧内完成，用户看不到，但撤销/重做都正确；</item>
/// <item><b>被擦掉的部分点不中</b>（<see cref="HitTester"/> 在差集后的几何上判定），
///       与用户看到的一致。</item>
/// </list>
/// </summary>
public sealed class SelectTool : ITool
{
    private enum Phase { Idle, Pressed, Marquee, Moving }

    private readonly HitTester _hits;

    private Phase _phase = Phase.Idle;
    private PointD _pressScreen;
    private PointD _pressWorld;
    private PointD _lastMoveWorld;

    private readonly List<PointD> _marqueeWorld = [];
    private readonly List<PointD> _marqueeScreen = [];

    /// <summary>拖动开始时记下每个被移动对象的原始位置（抬手时用它还原）。</summary>
    private readonly List<(ShapeObject Obj, double X, double Y)> _moving = [];

    private bool _moveCommitted;

    public SelectTool(SmoothGeometryBuilder geometry)
    {
        _hits = new HitTester(geometry);
    }

    public string Name => "选择";

    /// <summary>选择工具下手掌不擦除（擦除是橡皮工具的职责）。</summary>
    public bool PalmActsAsEraser => false;

    /// <summary>框选还是圈选（工具栏开关，触屏上尤其重要）。</summary>
    public SelectionMode Mode { get; set; } = SelectionMode.Rectangle;

    /// <summary>多选开关（鼠标按住 Ctrl/Shift，或工具栏"多选"按钮常开）。</summary>
    public bool Additive { get; set; }

    /// <summary>点击与拖动的分界（屏幕像素，决策 D2）。</summary>
    public double DragThresholdPx { get; init; } = 4.0;

    /// <summary>框选至少拖出这么大才算框选（屏幕像素），否则视为"点空白处取消选择"。</summary>
    public double MarqueeMinPx { get; init; } = 6.0;

    /// <summary>正在进行的框选/圈选轨迹（**屏幕坐标**，供画布画浮层）；没有则为 null。</summary>
    public IReadOnlyList<PointD>? MarqueeScreen
        => _phase == Phase.Marquee && _marqueeScreen.Count >= 2 ? _marqueeScreen : null;

    /// <summary>轨迹是否闭合成圈（圈选为 true，框选为 false）。</summary>
    public bool MarqueeIsLasso => Mode == SelectionMode.Lasso;

    /// <summary>正在拖动移动（画布可据此改光标）。</summary>
    public bool IsMoving => _phase == Phase.Moving;

    public void OnActivated(ToolContext ctx) => Cancel();

    public void OnDeactivated(ToolContext ctx) => Cancel();

    /// <summary>丢弃未提交的中间状态：拖动中的对象还原到原位，框选轨迹清空。</summary>
    public void Cancel()
    {
        if (_phase == Phase.Moving && !_moveCommitted) RestoreMoving();
        _moving.Clear();
        _marqueeWorld.Clear();
        _marqueeScreen.Clear();
        _phase = Phase.Idle;
    }

    public bool OnPointer(PointerAction action, PointerSample sample, ToolContext ctx)
    {
        switch (action)
        {
            case PointerAction.Down:
                OnDown(sample, ctx);
                return true;

            case PointerAction.Move:
                return OnMove(sample, ctx);

            case PointerAction.Up:
                return OnUp(sample, ctx);

            default:
                return false;
        }
    }

    private void OnDown(PointerSample s, ToolContext ctx)
    {
        _pressScreen = s.Position;
        _pressWorld = ctx.ScreenToWorld(s.Position);
        _lastMoveWorld = _pressWorld;
        _moveCommitted = false;
        _moving.Clear();
        _marqueeWorld.Clear();
        _marqueeScreen.Clear();

        var hit = _hits.HitTestTopmost(ctx.Page, _pressWorld);
        var selection = ctx.Selection;

        if (hit is null)
        {
            // 点空白：非多选就直接清空（"点空处取消选择"是所有人的肌肉记忆）
            if (!Additive) selection.Clear();
            _phase = Phase.Marquee;
            _marqueeWorld.Add(_pressWorld);
            _marqueeScreen.Add(s.Position);
            return;
        }

        if (Additive)
        {
            selection.Toggle(hit);
        }
        else if (!selection.Contains(hit))
        {
            selection.Set(hit);
        }

        if (selection.Contains(hit))
        {
            // 命中且已选中 → 允许拖动移动（先只记录，真正越过阈值才动）
            _phase = Phase.Pressed;
            foreach (var o in selection.Objects) _moving.Add((o, o.X, o.Y));
        }
        else
        {
            // 多选切换把它取消了 → 这一次点击只做"取消选中"
            _phase = Phase.Idle;
        }
    }

    private bool OnMove(PointerSample s, ToolContext ctx)
    {
        switch (_phase)
        {
            case Phase.Pressed:
            {
                // 越过阈值才算拖动（阈值用屏幕坐标，避免缩放影响手感）
                if (_pressScreen.DistanceTo(s.Position) < DragThresholdPx) return true;
                _phase = Phase.Moving;
                goto case Phase.Moving;
            }

            case Phase.Moving:
            {
                var world = ctx.ScreenToWorld(s.Position);
                var dx = world.X - _lastMoveWorld.X;
                var dy = world.Y - _lastMoveWorld.Y;
                if (dx != 0 || dy != 0)
                {
                    foreach (var (obj, _, _) in _moving)
                    {
                        obj.X += dx;
                        obj.Y += dy;
                    }
                    _lastMoveWorld = world;
                }
                return true;
            }

            case Phase.Marquee:
                AppendMarqueePoint(s, ctx);
                return true;

            default:
                return false;
        }
    }

    private bool OnUp(PointerSample s, ToolContext ctx)
    {
        var phase = _phase;
        _phase = Phase.Idle;

        switch (phase)
        {
            case Phase.Pressed:
                // 只是点了一下（没越过阈值）：选中已在 Down 时完成
                _moving.Clear();
                return true;

            case Phase.Moving:
                CommitMove(ctx, s);
                return true;

            case Phase.Marquee:
                CommitMarquee(s, ctx);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// 提交移动：**先把对象还原到原位**，再执行命令。
    /// 这样 <see cref="MoveObjectsCommand"/> 在首次执行时捕获到的就是真实起点，
    /// 撤销才能回到拖动前的位置（详见类注释）。
    /// </summary>
    private void CommitMove(ToolContext ctx, PointerSample s)
    {
        if (_moving.Count == 0) return;

        var world = ctx.ScreenToWorld(s.Position);
        var totalDx = world.X - _pressWorld.X;
        var totalDy = world.Y - _pressWorld.Y;

        // 阈值内的小位移不记为一次移动（手抖不该产生撤销项）
        if (_pressScreen.DistanceTo(s.Position) < DragThresholdPx || (totalDx == 0 && totalDy == 0))
        {
            RestoreMoving();
            _moving.Clear();
            return;
        }

        RestoreMoving();

        var moves = _moving.Select(m => (m.Obj, totalDx, totalDy)).ToList();
        _moveCommitted = true;
        ctx.Commands.Execute(new MoveObjectsCommand(moves));
        _moving.Clear();
    }

    private void RestoreMoving()
    {
        foreach (var (obj, x, y) in _moving)
        {
            obj.X = x;
            obj.Y = y;
        }
    }

    private void CommitMarquee(PointerSample s, ToolContext ctx)
    {
        // 抬手位置**必须计入轨迹**：用户看到的框是拖到手指抬起处的，
        // 少了这个点，选中的范围会比看到的框小一圈（实测会漏掉框内的对象）。
        AppendMarqueePoint(s, ctx);

        if (_marqueeScreen.Count < 2 || !IsMarqueeBigEnough())
        {
            _marqueeWorld.Clear();
            _marqueeScreen.Clear();
            return;
        }

        IEnumerable<ShapeObject> hits = Mode == SelectionMode.Lasso
            ? _hits.HitTestLasso(ctx.Page, _marqueeWorld)
            : _hits.HitTestRect(ctx.Page, RectD.FromPoints(_marqueeWorld));

        var list = hits.ToList();

        if (Additive) ctx.Selection.AddMany(list);
        else ctx.Selection.SetMany(list);

        _marqueeWorld.Clear();
        _marqueeScreen.Clear();
    }

    /// <summary>
    /// 判断这次拖动够不够格算"框选"。
    /// 用**轨迹包围盒的尺寸**而不是"起点到终点的距离"：
    /// 圈选是闭合的，起点终点几乎重合，用距离判定会把整个圈当成误触丢掉。
    /// </summary>
    private bool IsMarqueeBigEnough()
    {
        if (_marqueeScreen.Count < 2) return false;
        var b = RectD.FromPoints(_marqueeScreen);
        return Math.Max(b.Width, b.Height) >= MarqueeMinPx;
    }

    /// <summary>追加一个轨迹点（带 3px 抽稀，避免轨迹点爆炸）。</summary>
    private void AppendMarqueePoint(PointerSample s, ToolContext ctx)
    {
        if (_marqueeScreen.Count > 0 && _marqueeScreen[^1].DistanceTo(s.Position) < 3) return;

        _marqueeScreen.Add(s.Position);
        _marqueeWorld.Add(ctx.ScreenToWorld(s.Position));
    }

    /// <summary>选择工具不画"湿态笔迹"，浮层由画布负责（见 <see cref="MarqueeScreen"/>）。</summary>
    public Geometry? BuildPreview(ToolContext ctx) => null;
}
