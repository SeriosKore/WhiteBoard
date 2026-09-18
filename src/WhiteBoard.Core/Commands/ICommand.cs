namespace WhiteBoard.Core.Commands;

/// <summary>可撤销命令。</summary>
public interface ICommand
{
    /// <summary>命令名（用于调试与 UI 提示，如"撤销：擦除"）。</summary>
    string Name { get; }

    void Do();
    void Undo();
}

/// <summary>
/// 一条页面内的撤销栈（ADR：**每页一个独立栈**，撤销只作用于当前页）。
/// </summary>
public sealed class PageCommandStack
{
    private readonly List<ICommand> _undo = [];
    private readonly List<ICommand> _redo = [];

    public int PageId { get; }

    /// <summary>步数上限（与字节上限取小；S1 先按步数）。</summary>
    public int Capacity { get; init; } = 100;

    public PageCommandStack(int pageId, int capacity = 100)
    {
        PageId = pageId;
        Capacity = capacity;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    /// <summary>下一次撤销/重做的名称（用于 UI 提示）。</summary>
    public string? NextUndoName => _undo.Count > 0 ? _undo[^1].Name : null;
    public string? NextRedoName => _redo.Count > 0 ? _redo[^1].Name : null;

    /// <summary>执行并压栈。新命令会清空重做栈。</summary>
    public void Execute(ICommand command)
    {
        command.Do();
        _undo.Add(command);
        _redo.Clear();

        // 溢出时**丢弃最早的**（而非停止记录，否则用户会以为撤销坏了）
        while (_undo.Count > Capacity) _undo.RemoveAt(0);
    }

    /// <summary>把一批已执行的命令合成一条（事务化）。</summary>
    public void PushTransaction(string name, IReadOnlyList<ICommand> executed)
    {
        if (executed.Count == 0) return;
        _undo.Add(new TransactionCommand(name, executed));
        _redo.Clear();
        while (_undo.Count > Capacity) _undo.RemoveAt(0);
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        var cmd = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        cmd.Undo();
        _redo.Add(cmd);
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        var cmd = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        cmd.Do();
        _undo.Add(cmd);
        return true;
    }

    /// <summary>清空历史（如页面内容被整体替换后）。</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}

/// <summary>把多条命令当作一条（撤销时逆序回滚）。</summary>
public sealed class TransactionCommand : ICommand
{
    private readonly IReadOnlyList<ICommand> _parts;

    public TransactionCommand(string name, IReadOnlyList<ICommand> parts)
    {
        Name = name;
        _parts = parts;
    }

    public string Name { get; }

    public void Do()
    {
        foreach (var c in _parts) c.Do();
    }

    public void Undo()
    {
        for (var i = _parts.Count - 1; i >= 0; i--) _parts[i].Undo();
    }
}

/// <summary>
/// 文档级命令管理：**每页一个栈**（ADR）。
/// 切换页面时撤销/重做按钮状态随之变化；切页本身不入栈。
/// </summary>
public sealed class CommandManager
{
    private readonly Dictionary<int, PageCommandStack> _stacks = [];
    private readonly int _capacity;

    public CommandManager(int capacityPerPage = 100) => _capacity = capacityPerPage;

    /// <summary>当前页 Id（由调用方在切页时更新）。</summary>
    public int CurrentPageId { get; private set; }

    public void SetCurrentPage(int pageId)
    {
        CurrentPageId = pageId;
        _ = GetStack(pageId); // 预热，保证该页有栈
    }

    public PageCommandStack GetStack(int pageId)
    {
        if (!_stacks.TryGetValue(pageId, out var s))
        {
            s = new PageCommandStack(pageId, _capacity);
            _stacks[pageId] = s;
        }
        return s;
    }

    public PageCommandStack Current => GetStack(CurrentPageId);

    public bool CanUndo => Current.CanUndo;
    public bool CanRedo => Current.CanRedo;
    public int PageStackCount => _stacks.Count;

    /// <summary>
    /// 任何会改变文档内容或撤销栈的操作都会触发（入栈 / 撤销 / 重做 / 事务 / 清空）。
    /// UI 用它来标记"有未保存的改动"，从而驱动标题栏星号与自动保存。
    /// </summary>
    public event Action? Changed;

    public void Execute(ICommand command)
    {
        Current.Execute(command);
        Changed?.Invoke();
    }

    public bool Undo()
    {
        var ok = Current.Undo();
        if (ok) Changed?.Invoke();
        return ok;
    }

    public bool Redo()
    {
        var ok = Current.Redo();
        if (ok) Changed?.Invoke();
        return ok;
    }

    /// <summary>把一批已执行的命令合成一条（也触发变更通知）。</summary>
    public void PushTransaction(string name, IReadOnlyList<ICommand> executed)
    {
        Current.PushTransaction(name, executed);
        Changed?.Invoke();
    }

    /// <summary>清空当前页的撤销历史（不触发"内容已改"的通知——内容没变）。</summary>
    public void ClearHistory() => Current.Clear();

    /// <summary>页面被删除时丢弃其栈。</summary>
    public void DropPage(int pageId)
    {
        _stacks.Remove(pageId);
        if (CurrentPageId == pageId) CurrentPageId = 0;
    }
}
