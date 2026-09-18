using WhiteBoard.Core.Geometry;

namespace WhiteBoard.Core.Model;

/// <summary>一个页面：独立的对象集合 + 独立的视口状态。</summary>
public sealed class Page
{
    private int _nextZ;

    public required int Id { get; init; }

    /// <summary>按 ZIndex 升序渲染；列表顺序不保证等于 Z 顺序，取用时排序。</summary>
    public List<ShapeObject> Objects { get; } = [];

    public ViewportState Viewport { get; } = new();

    /// <summary>页面背景类型（S1 只做纯色；S2 扩方格纸等）。</summary>
    public string BackgroundType { get; set; } = "solid";

    public int Count => Objects.Count;

    public Page Add(ShapeObject obj)
    {
        if (obj.ZIndex == 0) obj.ZIndex = ++_nextZ;
        else _nextZ = Math.Max(_nextZ, obj.ZIndex);
        Objects.Add(obj);
        return this;
    }

    public bool Remove(ShapeObject obj) => Objects.Remove(obj);

    public ShapeObject? FindById(int id) => Objects.FirstOrDefault(o => o.Id == id);

    /// <summary>按 ZIndex 升序（渲染顺序）。</summary>
    public IEnumerable<ShapeObject> InRenderOrder() => Objects.OrderBy(o => o.ZIndex);

    public int NextZ() => ++_nextZ;

    /// <summary>置顶：取最大 Z + 1。</summary>
    public void BringToFront(ShapeObject obj)
    {
        obj.ZIndex = NextZ();
    }

    /// <summary>置底：取最小 Z - 1。</summary>
    public void SendToBack(ShapeObject obj)
    {
        var min = Objects.Count == 0 ? 1 : Objects.Min(o => o.ZIndex);
        obj.ZIndex = min - 1;
    }

    /// <summary>所有对象的世界外接矩形（内容边界，用于导出）。</summary>
    public RectD ContentBounds()
    {
        var r = RectD.Empty;
        foreach (var o in Objects) r = RectD.Union(r, o.WorldBounds);
        return r;
    }

    /// <summary>视口裁剪：只返回与可见区域相交的对象（按渲染顺序）。</summary>
    public IEnumerable<ShapeObject> VisibleObjects(RectD visibleWorld)
        => InRenderOrder().Where(o => o.WorldBounds.IntersectsWith(visibleWorld));

    /// <summary>用已有对象重建内部 Z 计数器（加载文档后调用）。</summary>
    public void RebuildZCounter()
    {
        _nextZ = Objects.Count == 0 ? 0 : Objects.Max(o => o.ZIndex);
    }
}

/// <summary>整个文档：多页面。</summary>
public sealed class WhiteboardDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public required int Id { get; init; }

    /// <summary>页面顺序即显示顺序。</summary>
    public List<Page> Pages { get; } = [];

    private int _currentIndex;

    public int CurrentPageIndex
    {
        get => Pages.Count == 0 ? 0 : Math.Clamp(_currentIndex, 0, Pages.Count - 1);
        set => _currentIndex = Pages.Count == 0 ? 0 : Math.Clamp(value, 0, Pages.Count - 1);
    }

    public Page CurrentPage => Pages[CurrentPageIndex];

    public int NextPageId { get; private set; } = 1;
    public int NextObjectId { get; private set; } = 1;

    public int AllocatePageId() => NextPageId++;
    public int AllocateObjectId() => NextObjectId++;

    /// <summary>确保至少有一页（文档约束：始终 ≥1 页）。</summary>
    public Page EnsureAtLeastOnePage()
    {
        if (Pages.Count == 0) Pages.Add(new Page { Id = AllocatePageId() });
        return Pages[0];
    }

    public Page AddPage(int? index = null)
    {
        var page = new Page { Id = AllocatePageId() };
        if (index is null || index < 0 || index >= Pages.Count) Pages.Add(page);
        else Pages.Insert(index.Value, page);
        return page;
    }

    /// <summary>删除页面。删最后一页时自动补一个空白页（文档约束）。</summary>
    public bool RemovePage(Page page)
    {
        var idx = Pages.IndexOf(page);
        if (idx < 0) return false;

        Pages.RemoveAt(idx);
        if (Pages.Count == 0) Pages.Add(new Page { Id = AllocatePageId() });

        if (_currentIndex >= Pages.Count) _currentIndex = Pages.Count - 1;
        return true;
    }

    /// <summary>复制页面（新 Id、对象深拷贝、视口复制）。</summary>
    public Page DuplicatePage(Page source)
    {
        var copy = new Page
        {
            Id = AllocatePageId(),
            BackgroundType = source.BackgroundType
        };
        copy.Viewport.Zoom = source.Viewport.Zoom;
        copy.Viewport.PanX = source.Viewport.PanX;
        copy.Viewport.PanY = source.Viewport.PanY;

        foreach (var o in source.InRenderOrder())
            copy.Add(o.Clone(AllocateObjectId()));

        Pages.Insert(Pages.IndexOf(source) + 1, copy);
        return copy;
    }

    /// <summary>重建 Id 计数器（加载后调用，避免新对象与旧 Id 冲突）。</summary>
    public void RebuildIdCounters()
    {
        NextPageId = Pages.Count == 0 ? 1 : Pages.Max(p => p.Id) + 1;
        NextObjectId = Pages.SelectMany(p => p.Objects).Select(o => o.Id).DefaultIfEmpty(0).Max() + 1;
        foreach (var p in Pages) p.RebuildZCounter();
    }
}
