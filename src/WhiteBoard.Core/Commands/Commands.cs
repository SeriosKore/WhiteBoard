using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Core.Commands;

/// <summary>
/// 命令基类：解决"**构造时机 vs 执行时机**"的经典陷阱。
///
/// 问题：若在构造函数里就把"目标状态"算好，而多条命令是**先全部构造、再依次执行**
/// （事务化场景），后面的命令会基于**过期的状态**算出错误目标。
/// 实测表现：`Move(+10,0)` 与 `Move(0,+20)` 先后执行后，X 被第二条命令覆盖回 0。
///
/// 约定：子类在 <see cref="CaptureOnce"/> 里**只捕获一次**（首次 Do 时），
/// 之后 Do/Undo 都基于已捕获的 from/to，保证重做幂等。
/// </summary>
public abstract class CommandBase : ICommand
{
    private bool _captured;

    public abstract string Name { get; }

    /// <summary>首次执行时捕获前置状态（只调用一次）。</summary>
    protected abstract void Capture();

    /// <summary>应用"目标状态"。</summary>
    protected abstract void Apply();

    /// <summary>还原"前置状态"。</summary>
    protected abstract void Revert();

    public void Do()
    {
        if (!_captured)
        {
            Capture();
            _captured = true;
        }
        Apply();
    }

    public void Undo() => Revert();
}

/// <summary>向页面添加对象。</summary>
public sealed class AddObjectCommand : CommandBase
{
    private readonly Page _page;
    private readonly ShapeObject _obj;
    private int _assignedZ;

    public AddObjectCommand(Page page, ShapeObject obj)
    {
        _page = page;
        _obj = obj;
    }

    public override string Name => "添加";

    protected override void Capture() => _assignedZ = _obj.ZIndex;

    protected override void Apply()
    {
        if (_page.Objects.Contains(_obj)) return;
        _obj.ZIndex = _assignedZ;
        _page.Add(_obj);
        _assignedZ = _obj.ZIndex;   // Add 可能分配了新 Z
    }

    protected override void Revert() => _page.Remove(_obj);
}

/// <summary>删除若干对象（记录 Z 以便原样恢复）。</summary>
public sealed class RemoveObjectsCommand : CommandBase
{
    private readonly Page _page;
    private readonly List<ShapeObject> _objs;
    private readonly Dictionary<int, int> _zSnapshot = [];

    public RemoveObjectsCommand(Page page, IEnumerable<ShapeObject> objects)
    {
        _page = page;
        _objs = objects.ToList();
    }

    public override string Name => _objs.Count > 1 ? $"删除 {_objs.Count} 个对象" : "删除";

    protected override void Capture()
    {
        _zSnapshot.Clear();
        foreach (var o in _objs) _zSnapshot[o.Id] = o.ZIndex;
    }

    protected override void Apply()
    {
        foreach (var o in _objs) _page.Remove(o);
    }

    protected override void Revert()
    {
        foreach (var o in _objs)
        {
            if (_zSnapshot.TryGetValue(o.Id, out var z)) o.ZIndex = z;
            if (!_page.Objects.Contains(o)) _page.Objects.Add(o);
        }
    }
}

/// <summary>按位移移动若干对象。</summary>
public sealed class MoveObjectsCommand : CommandBase
{
    private readonly List<(ShapeObject Obj, double Dx, double Dy)> _deltas;
    private readonly List<(double X, double Y)> _from = [];
    private readonly List<(double X, double Y)> _to = [];

    public MoveObjectsCommand(IEnumerable<(ShapeObject Obj, double Dx, double Dy)> moves)
        => _deltas = moves.ToList();

    public override string Name => _deltas.Count > 1 ? $"移动 {_deltas.Count} 个对象" : "移动";

    protected override void Capture()
    {
        _from.Clear();
        _to.Clear();
        // 在**首次执行时**基于当前真实位置算目标，避免事务中后序命令用到过期状态
        foreach (var (obj, dx, dy) in _deltas)
        {
            _from.Add((obj.X, obj.Y));
            _to.Add((obj.X + dx, obj.Y + dy));
        }
    }

    protected override void Apply()
    {
        for (var i = 0; i < _deltas.Count; i++)
        {
            _deltas[i].Obj.X = _to[i].X;
            _deltas[i].Obj.Y = _to[i].Y;
        }
    }

    protected override void Revert()
    {
        for (var i = 0; i < _deltas.Count; i++)
        {
            _deltas[i].Obj.X = _from[i].X;
            _deltas[i].Obj.Y = _from[i].Y;
        }
    }
}

/// <summary>
/// 擦除命令：向对象追加**擦除遮罩**（ADR-18：遮罩保留，不碎片化）。
/// 撤销 = 回退遮罩，**不是**恢复被切碎的对象。
/// </summary>
public sealed class EraseCommand : CommandBase
{
    private readonly List<(ShapeObject Obj, EraserCircle Circle)> _applied = [];
    private readonly List<ShapeObject> _emptied = [];
    private readonly Page _page;

    public EraseCommand(Page page) => _page = page;

    public override string Name => "擦除";

    public int AppliedCount => _applied.Count;

    /// <summary>记录一次遮罩追加；重复圆会被忽略。</summary>
    public bool Record(ShapeObject obj, EraserCircle localCircle)
    {
        if (!obj.IsErasable) return false;
        if (!obj.AddErasure(localCircle)) return false;
        _applied.Add((obj, localCircle));
        return true;
    }

    /// <summary>对象被完全擦除（渲染几何为空）时标记为待删除。</summary>
    public void MarkEmptied(ShapeObject obj)
    {
        if (!_emptied.Contains(obj)) _emptied.Add(obj);
    }

    public bool HasEffect => _applied.Count > 0 || _emptied.Count > 0;

    protected override void Capture() { /* 遮罩已在 Record 时写入，无需捕获 */ }

    protected override void Apply()
    {
        foreach (var o in _emptied) _page.Remove(o);
    }

    protected override void Revert()
    {
        foreach (var o in _emptied)
            if (!_page.Objects.Contains(o)) _page.Objects.Add(o);

        // 逆序移除本命令添加的遮罩
        for (var i = _applied.Count - 1; i >= 0; i--)
        {
            var (obj, circle) = _applied[i];
            for (var k = obj.Erasures.Count - 1; k >= 0; k--)
            {
                var e = obj.Erasures[k];
                if (Math.Abs(e.X - circle.X) < 1e-9 &&
                    Math.Abs(e.Y - circle.Y) < 1e-9 &&
                    Math.Abs(e.Radius - circle.Radius) < 1e-9)
                {
                    obj.Erasures.RemoveAt(k);
                    break;
                }
            }
        }
    }
}

/// <summary>清空当前页（可撤销）。</summary>
public sealed class ClearPageCommand : CommandBase
{
    private readonly Page _page;
    private List<ShapeObject> _snapshot = [];

    public ClearPageCommand(Page page) => _page = page;

    public override string Name => "清空页面";

    protected override void Capture() => _snapshot = _page.Objects.ToList();

    protected override void Apply() => _page.Objects.Clear();

    protected override void Revert() => _page.Objects.AddRange(_snapshot);
}

/// <summary>置顶 / 置底。</summary>
public sealed class ZOrderCommand : CommandBase
{
    public enum Mode { Front, Back }

    private readonly Page _page;
    private readonly ShapeObject _obj;
    private readonly Mode _mode;
    private int _oldZ;
    private int _newZ;

    public ZOrderCommand(Page page, ShapeObject obj, Mode mode)
    {
        _page = page;
        _obj = obj;
        _mode = mode;
    }

    public override string Name => _mode == Mode.Front ? "置顶" : "置底";

    protected override void Capture() => _oldZ = _obj.ZIndex;

    protected override void Apply()
    {
        if (_mode == Mode.Front) _page.BringToFront(_obj);
        else _page.SendToBack(_obj);
        _newZ = _obj.ZIndex;
    }

    protected override void Revert() => _obj.ZIndex = _oldZ;
}

/// <summary>克隆（深拷贝 + 偏移 + 新 Id）。</summary>
public sealed class CloneCommand : CommandBase
{
    private readonly Page _page;
    private readonly List<ShapeObject> _clones = [];
    private readonly List<int> _assignedZ = [];

    public CloneCommand(Page page, IEnumerable<ShapeObject> originals, Func<int> idAllocator, double offset = 16)
    {
        _page = page;
        foreach (var o in originals)
        {
            var c = o.Clone(idAllocator());
            c.X += offset;
            c.Y += offset;
            _clones.Add(c);
        }
    }

    public override string Name => _clones.Count > 1 ? $"克隆 {_clones.Count} 个对象" : "克隆";

    public IReadOnlyList<ShapeObject> Clones => _clones;

    protected override void Capture()
    {
        _assignedZ.Clear();
        foreach (var c in _clones) _assignedZ.Add(c.ZIndex);
    }

    protected override void Apply()
    {
        for (var i = 0; i < _clones.Count; i++)
        {
            if (_page.Objects.Contains(_clones[i])) continue;
            _clones[i].ZIndex = _assignedZ[i];
            _page.Add(_clones[i]);
            _assignedZ[i] = _clones[i].ZIndex;
        }
    }

    protected override void Revert()
    {
        foreach (var c in _clones) _page.Remove(c);
    }
}

/// <summary>调整笔宽（针对选中对象）。</summary>
public sealed class SetPenWidthCommand : CommandBase
{
    private readonly List<(FreehandObject Obj, double New)> _items;
    private readonly List<double> _old = [];

    public SetPenWidthCommand(IEnumerable<FreehandObject> objs, double newWidth)
        => _items = objs.Select(o => (o, newWidth)).ToList();

    public override string Name => "笔宽";

    protected override void Capture()
    {
        _old.Clear();
        foreach (var (obj, _) in _items) _old.Add(obj.PenWidth);
    }

    protected override void Apply()
    {
        foreach (var (obj, nw) in _items) obj.PenWidth = nw;
    }

    protected override void Revert()
    {
        for (var i = 0; i < _items.Count; i++) _items[i].Obj.PenWidth = _old[i];
    }
}
