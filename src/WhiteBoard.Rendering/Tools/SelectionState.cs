using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Rendering.Tools;

/// <summary>
/// 当前页的选中集合。
///
/// 刻意放在**工具之外**（由画布持有并在 <see cref="ToolContext"/> 里传给工具）：
/// 选中态不是工具的私有状态——工具切换（画笔→橡皮→选择）之后，用户仍然期望
/// "刚才选中的那几个"还在，撤销/删除按钮也仍然作用在它们身上。
///
/// 约定：**选中集合只包含当前页面的对象**。切页、或对象被删除后，
/// 必须调用 <see cref="RemoveMissing"/> 把已不在页面里的对象剔掉——
/// 否则会出现"选中的东西已经不存在了，但选择框还画在那里"。
/// </summary>
public sealed class SelectionState
{
    private readonly HashSet<ShapeObject> _set = [];

    /// <summary>选中集合变化（画布据此重绘、UI 据此刷新按钮可用状态）。</summary>
    public event Action? Changed;

    public int Count => _set.Count;
    public bool IsEmpty => _set.Count == 0;

    /// <summary>选中的对象（顺序不保证）。</summary>
    public IReadOnlyCollection<ShapeObject> Objects => _set;

    /// <summary>选中对象的数量（与 <see cref="Count"/> 相同，名字更贴近调用处语义）。</summary>
    public int SelectionCount => _set.Count;

    public bool Contains(ShapeObject obj) => _set.Contains(obj);

    /// <summary>单选：清空后只选这一个。</summary>
    public void Set(ShapeObject obj)
    {
        if (_set.Count == 1 && _set.Contains(obj)) return;
        _set.Clear();
        _set.Add(obj);
        Changed?.Invoke();
    }

    public void SetMany(IEnumerable<ShapeObject> objects)
    {
        _set.Clear();
        foreach (var o in objects) _set.Add(o);
        Changed?.Invoke();
    }

    public void Add(ShapeObject obj)
    {
        if (_set.Add(obj)) Changed?.Invoke();
    }

    public void AddMany(IEnumerable<ShapeObject> objects)
    {
        var added = false;
        foreach (var o in objects) added |= _set.Add(o);
        if (added) Changed?.Invoke();
    }

    /// <summary>多选切换（Ctrl/Shift 点击）。</summary>
    public void Toggle(ShapeObject obj)
    {
        if (!_set.Remove(obj)) _set.Add(obj);
        Changed?.Invoke();
    }

    public void Remove(ShapeObject obj)
    {
        if (_set.Remove(obj)) Changed?.Invoke();
    }

    public void Clear()
    {
        if (_set.Count == 0) return;
        _set.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// 剔除已经不在该页里的对象（被删除、或"添加"被撤销）。
    /// 应在每次命令执行后与切页时调用。
    /// </summary>
    public bool RemoveMissing(Page? page)
    {
        if (_set.Count == 0) return false;

        bool removed;
        if (page is null)
        {
            _set.Clear();
            removed = true;
        }
        else
        {
            removed = _set.RemoveWhere(o => !page.Objects.Contains(o)) > 0;
        }

        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>当前选中对象的世界包围盒并集（用于状态栏提示与整体操作）。</summary>
    public RectD WorldBounds()
    {
        var r = RectD.Empty;
        foreach (var o in _set) r = RectD.Union(r, o.WorldBounds);
        return r;
    }
}
