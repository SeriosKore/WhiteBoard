using WhiteBoard.Core.Model;

namespace WhiteBoard.Core.Commands;

/// <summary>
/// 页面级命令（新建/复制/删除/换序/跨页移动对象）。
///
/// 为什么不复活"每页独立撤销栈"：这些操作改的是**文档结构**，不属于任何一页。
/// 但它们又必须可撤销——老师误删一页会丢掉整页内容，这比误删一笔严重得多。
/// 因此把最后一条页面操作单独拿出来，由 UI 提供「撤销」入口（状态栏提示 + 按钮），
/// 而 Ctrl+Z 仍然只作用于**当前页**（保持 ADR 的每页独立栈语义，避免两套历史互相打架）。
///
/// 共同的不变量：
/// <list type="bullet">
/// <item>页面对象**只创建一次**并在 Undo/Redo 之间复用 —— 这样"新建页 → 在上面画 → 撤销 → 重做"
///       能把画的内容一起带回来，而不是给一个空页；</item>
/// <item>**文档至少 1 页**：删除最后一页必须由调用方拦下（UI 会禁用该按钮），
///       命令本身拒绝执行，而不是"悄悄补一个空白页"（那会让撤销无法还原）；</item>
/// <item>页序与当前页索引都要能被准确还原。</item>
/// </list>
/// </summary>
public abstract class DocumentCommandBase : CommandBase
{
    protected readonly WhiteboardDocument Document;

    protected DocumentCommandBase(WhiteboardDocument document) => Document = document;

    /// <summary>本命令是否改变了"当前页"。UI 据此决定要不要把画布切到新页。</summary>
    public bool ChangesCurrentPage { get; protected set; }
}

/// <summary>新建空白页（默认插到末尾；可指定索引）。</summary>
public sealed class AddPageCommand : DocumentCommandBase
{
    private readonly int? _index;
    private Page? _page;
    private int _insertedAt = -1;
    private int _oldCurrentIndex;

    public AddPageCommand(WhiteboardDocument document, int? index = null) : base(document)
    {
        _index = index;
        ChangesCurrentPage = true;
    }

    public override string Name => "新建页";

    /// <summary>被创建的那一页（Apply 之前为 null）。</summary>
    public Page? Page => _page;

    protected override void Capture() => _oldCurrentIndex = Document.CurrentPageIndex;

    protected override void Apply()
    {
        _page ??= new Page { Id = Document.AllocatePageId() };

        var at = _index ?? Document.Pages.Count;
        at = Math.Clamp(at, 0, Document.Pages.Count);

        Document.Pages.Insert(at, _page);
        _insertedAt = at;
        Document.CurrentPageIndex = at;
    }

    protected override void Revert()
    {
        if (_insertedAt < 0 || _insertedAt >= Document.Pages.Count) return;

        Document.Pages.RemoveAt(_insertedAt);
        if (Document.Pages.Count > 0)
            Document.CurrentPageIndex = Math.Clamp(_oldCurrentIndex, 0, Document.Pages.Count - 1);
        _insertedAt = -1;
    }
}

/// <summary>复制某一页（深拷贝对象 + 复制视口），插在源页之后。</summary>
public sealed class DuplicatePageCommand : DocumentCommandBase
{
    private readonly Page _source;
    private Page? _copy;
    private int _insertedAt = -1;
    private int _oldCurrentIndex;

    public DuplicatePageCommand(WhiteboardDocument document, Page source) : base(document)
    {
        _source = source;
        ChangesCurrentPage = true;
    }

    public override string Name => "复制页";

    public Page? Copy => _copy;

    protected override void Capture() => _oldCurrentIndex = Document.CurrentPageIndex;

    protected override void Apply()
    {
        if (_copy is null)
        {
            _copy = new Page
            {
                Id = Document.AllocatePageId(),
                BackgroundType = _source.BackgroundType
            };
            _copy.Viewport.Zoom = _source.Viewport.Zoom;
            _copy.Viewport.PanX = _source.Viewport.PanX;
            _copy.Viewport.PanY = _source.Viewport.PanY;

            foreach (var o in _source.InRenderOrder())
                _copy.Add(o.Clone(Document.AllocateObjectId()));
        }

        var at = Math.Clamp(Document.Pages.IndexOf(_source) + 1, 0, Document.Pages.Count);
        Document.Pages.Insert(at, _copy);
        _insertedAt = at;
        Document.CurrentPageIndex = at;
    }

    protected override void Revert()
    {
        if (_insertedAt < 0 || _insertedAt >= Document.Pages.Count) return;

        Document.Pages.RemoveAt(_insertedAt);
        if (Document.Pages.Count > 0)
            Document.CurrentPageIndex = Math.Clamp(_oldCurrentIndex, 0, Document.Pages.Count - 1);
        _insertedAt = -1;
    }
}

/// <summary>
/// 删除一页。**拒绝删除最后一页**（文档约束：始终 ≥1 页），
/// 由调用方在 UI 上拦（按钮禁用），命令本身也再挡一次。
/// </summary>
public sealed class RemovePageCommand : DocumentCommandBase
{
    private readonly Page _page;
    private int _removedAt = -1;
    private int _oldCurrentIndex;
    private bool _refused;

    public RemovePageCommand(WhiteboardDocument document, Page page) : base(document)
    {
        _page = page;
        ChangesCurrentPage = true;
    }

    public override string Name => "删除页";

    /// <summary>是否因为"只剩一页"而被拒绝（UI 可据此提示用户）。</summary>
    public bool Refused => _refused;

    /// <summary>被删掉的页（撤销时原样放回，内容一起回来）。</summary>
    public Page Page => _page;

    protected override void Capture() => _oldCurrentIndex = Document.CurrentPageIndex;

    protected override void Apply()
    {
        if (Document.Pages.Count <= 1)
        {
            _refused = true;
            return;
        }

        _removedAt = Document.Pages.IndexOf(_page);
        if (_removedAt < 0) return;

        Document.Pages.RemoveAt(_removedAt);

        // 关键：删的是**前面的页**时，后面所有页的下标都要前移一位，
        // 当前页索引也必须跟着前移，否则用户会"突然跳到另一页"（内容看起来凭空换了）。
        //   删在当前位置之前 → 当前页前移一位
        //   删的就是当前页   → 停在同一格（显示原来的下一页）
        //   删在当前位置之后 → 当前页索引不变
        var newIndex = _removedAt < _oldCurrentIndex ? _oldCurrentIndex - 1 : _oldCurrentIndex;
        Document.CurrentPageIndex = Math.Clamp(newIndex, 0, Document.Pages.Count - 1);
    }

    protected override void Revert()
    {
        if (_removedAt < 0) return;

        Document.Pages.Insert(Math.Clamp(_removedAt, 0, Document.Pages.Count), _page);
        Document.CurrentPageIndex = Math.Clamp(_oldCurrentIndex, 0, Document.Pages.Count - 1);
        _removedAt = -1;
        _refused = false;
    }
}

/// <summary>调整页序（把某一页移到新位置）。</summary>
public sealed class MovePageCommand : DocumentCommandBase
{
    private readonly Page _page;
    private readonly int _toIndex;
    private int _fromIndex = -1;
    private int _oldCurrentIndex;

    public MovePageCommand(WhiteboardDocument document, Page page, int toIndex) : base(document)
    {
        _page = page;
        _toIndex = toIndex;
        ChangesCurrentPage = false;
    }

    public override string Name => "调整页序";

    protected override void Capture() => _oldCurrentIndex = Document.CurrentPageIndex;

    protected override void Apply()
    {
        _fromIndex = Document.Pages.IndexOf(_page);
        if (_fromIndex < 0) return;

        var to = Math.Clamp(_toIndex, 0, Document.Pages.Count - 1);
        if (to == _fromIndex) return;

        Document.Pages.RemoveAt(_fromIndex);
        Document.Pages.Insert(to, _page);
        Document.CurrentPageIndex = _oldCurrentIndex;
    }

    protected override void Revert()
    {
        if (_fromIndex < 0) return;

        var at = Document.Pages.IndexOf(_page);
        if (at >= 0) Document.Pages.RemoveAt(at);
        Document.Pages.Insert(Math.Clamp(_fromIndex, 0, Document.Pages.Count), _page);
        Document.CurrentPageIndex = Math.Clamp(_oldCurrentIndex, 0, Document.Pages.Count - 1);
    }
}

/// <summary>
/// 追加导入：把**另一份画板**的所有页追加到当前文档末尾。
///
/// 这是"两个老师的素材合并成一个课件"的最小可用形态。三个必须做对的地方：
/// <list type="number">
/// <item>**Id 全部重分配**：页 Id 与对象 Id 都必须取自目标文档的分配器，
///       否则会与已有内容撞号（撞号的后果是渲染缓存按 Id 索引时张冠李戴）；</item>
/// <item>**深拷贝**：源文档对象是"别的文件"，不能被这次导入改动，也不能在导入后被改到；</item>
/// <item>**一次可撤销**：导错了要能整体退掉，而不是一页一页删。</item>
/// </list>
/// 页在目标文档里**按原顺序**追加到末尾，追加完自动切到第一张导入的页（用户能立刻看到结果）。
/// </summary>
public sealed class AppendDocumentCommand : DocumentCommandBase
{
    private readonly WhiteboardDocument _source;
    private readonly List<Page> _added = [];
    private int _oldCurrentIndex;

    public AppendDocumentCommand(WhiteboardDocument document, WhiteboardDocument source)
        : base(document)
    {
        _source = source;
        ChangesCurrentPage = true;
    }

    public override string Name => $"追加导入 {_added.Count} 页";

    /// <summary>被追加进来的页（Apply 之前为空）。</summary>
    public IReadOnlyList<Page> AddedPages => _added;

    /// <summary>源文档没有页时无事可做，UI 可据此提示。</summary>
    public bool NothingToDo => _source.Pages.Count == 0;

    protected override void Capture() => _oldCurrentIndex = Document.CurrentPageIndex;

    protected override void Apply()
    {
        if (_added.Count == 0)
        {
            if (_source.Pages.Count == 0) return;

            foreach (var src in _source.Pages)
            {
                var page = new Page
                {
                    Id = Document.AllocatePageId(),
                    BackgroundType = src.BackgroundType
                };
                page.Viewport.Zoom = src.Viewport.Zoom;
                page.Viewport.PanX = src.Viewport.PanX;
                page.Viewport.PanY = src.Viewport.PanY;

                foreach (var o in src.InRenderOrder())
                    page.Add(o.Clone(Document.AllocateObjectId()));

                _added.Add(page);
            }
        }

        if (_added.Count == 0) return;

        Document.Pages.AddRange(_added);
        Document.CurrentPageIndex = Document.Pages.Count - _added.Count;
    }

    protected override void Revert()
    {
        foreach (var p in _added) Document.Pages.Remove(p);

        if (Document.Pages.Count > 0)
            Document.CurrentPageIndex = Math.Clamp(_oldCurrentIndex, 0, Document.Pages.Count - 1);
    }
}

///
/// 跨页移动对象。
///
/// 两份页共享同一个世界坐标系，因此 **X/Y 原样保留**——老师在 A 页写在右下角，
/// 搬到 B 页后仍在右下角，位置不会莫名跳走。
/// 目标页里的 Z 序重新排到最上层（否则可能被目标页已有内容盖住，看起来像"搬丢了"），
/// 撤销时把原 Z 序一并还原。
/// </summary>
public sealed class MoveObjectsToPageCommand : DocumentCommandBase
{
    private readonly Page _from;
    private readonly Page _to;
    private readonly List<ShapeObject> _objects;

    private readonly List<int> _oldZ = [];
    private readonly List<int> _newZ = [];

    public MoveObjectsToPageCommand(WhiteboardDocument document, Page from, Page to, IEnumerable<ShapeObject> objects)
        : base(document)
    {
        _from = from;
        _to = to;
        _objects = objects.ToList();
        ChangesCurrentPage = false;
    }

    public override string Name => _objects.Count > 1 ? $"移动 {_objects.Count} 个对象到另一页" : "移动到另一页";

    public int Count => _objects.Count;

    protected override void Capture() { /* Z 序在 Apply 里第一次执行时确定 */ }

    protected override void Apply()
    {
        if (_newZ.Count == 0)
        {
            foreach (var o in _objects)
            {
                _oldZ.Add(o.ZIndex);
                _newZ.Add(_to.NextZ());
            }
        }

        for (var i = 0; i < _objects.Count; i++)
        {
            var o = _objects[i];
            _from.Remove(o);
            o.ZIndex = _newZ[i];
            if (!_to.Objects.Contains(o)) _to.Add(o);
        }
    }

    protected override void Revert()
    {
        for (var i = _objects.Count - 1; i >= 0; i--)
        {
            var o = _objects[i];
            _to.Remove(o);
            if (i < _oldZ.Count) o.ZIndex = _oldZ[i];
            if (!_from.Objects.Contains(o)) _from.Objects.Add(o);
        }
    }
}
