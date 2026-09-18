using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WhiteBoard.App.Input;
using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Input;
using WhiteBoard.Core.Model;
using WhiteBoard.Rendering;
using WhiteBoard.Rendering.Tools;

// WPF 的 System.Windows.Controls / System.Windows.Input 里也有 Page、CommandManager，
// 这里显式取 Core 的模型类型（画板页面与撤销栈）。
using Page = WhiteBoard.Core.Model.Page;
using CommandManager = WhiteBoard.Core.Commands.CommandManager;
namespace WhiteBoard.App.Rendering;

/// <summary>
/// 画布控件：把「渲染层 + 输入层 + 工具层」接在一起。
///
/// 每帧的渲染顺序：
/// <list type="number">
/// <item>清屏 + 背景；</item>
/// <item>推入视口变换（<c>screen = (world - pan) * zoom</c>）；</item>
/// <item>画已提交的文档对象（视口裁剪，几何走冻结缓存）；</item>
/// <item>画当前工具的**预览几何**（湿态）——与干笔迹同源，抬笔零跳变；</item>
/// <item>恢复变换。</item>
/// </list>
///
/// 事件处置**不在本类**：全部交给 <see cref="ToolDispatcher"/>（可离屏单测的同一段代码）。
/// 本类只负责"WPF 事件 → PointerSample"与"画"。
///
/// 桌面（无笔无触摸）也能用：**中键拖动 = 平移，滚轮 = 以光标为锚点缩放**。
/// </summary>
public sealed class CanvasHost : Control
{
    private readonly SmoothGeometryBuilder _geometry = new();
    private readonly PageRenderer _renderer;
    private readonly WpfPointerSource _pointer;
    private readonly ToolDispatcher _dispatcher = new();

    private WhiteboardDocument? _document;
    private ITool? _tool;

    private string _penColor = "#F5F5F0";
    private double _penWidth = 3;
    private SolidColorBrush _penBrush = GeometryInterop.BrushFromHex("#F5F5F0");

    private bool _panning;
    private Point _panLast;

    private readonly Pen _selectionPen;
    private readonly Brush _selectionFill;
    private readonly Pen _marqueePen;

    public CanvasHost()
    {
        _renderer = new PageRenderer(_geometry);
        ClipToBounds = true;
        Focusable = true;

        // 抗锯齿与缩放质量（M0 §六-E）：保持默认抗锯齿、禁用像素吸附
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SnapsToDevicePixels = false;
        UseLayoutRounding = false;

        // 选中框与框选轨迹：**屏幕空间**绘制，因此缩放到 50 倍时线宽也不变粗
        _selectionPen = new Pen(new SolidColorBrush(Color.FromRgb(0xEE, 0xD8, 0x58)), 1.5)
        {
            DashStyle = new DashStyle([4, 3], 0)
        };
        _selectionPen.Freeze();
        _selectionFill = new SolidColorBrush(Color.FromArgb(0x22, 0xEE, 0xD8, 0x58));
        _selectionFill.Freeze();
        _marqueePen = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0x9C, 0xDC, 0xFE)), 1.0)
        {
            DashStyle = new DashStyle([3, 3], 0)
        };
        _marqueePen.Freeze();

        _pointer = new WpfPointerSource(this);
        _pointer.PointerEvent += OnPointer;

        // 撤销/重做/删除之后，选中集合里可能有"已经不存在的对象" → 及时剔除
        Commands.Changed += OnCommandsChanged;
        Selection.Changed += InvalidateVisual;
    }

    /// <summary>当前页的选中集合（选择工具与删除/置顶等操作共用）。</summary>
    public SelectionState Selection { get; } = new();

    /// <summary>
    /// 就地文本编辑的宿主（由窗口实现）。文本工具通过它打开编辑框；
    /// 其余工具不关心，为 null 时文本工具只是不工作，不影响别的功能。
    /// </summary>
    public ITextEditHost? TextEditorHost { get; set; }

    private void OnCommandsChanged()
    {
        Selection.RemoveMissing(CurrentPage);
        InvalidateVisual();
        ContentChanged?.Invoke();
    }

    /// <summary>文档（含多页面）。</summary>
    public WhiteboardDocument? Document
    {
        get => _document;
        set
        {
            _document = value;
            _dispatcher.Reset(_tool);
            if (_document is not null) Commands.SetCurrentPage(_document.CurrentPage.Id);
            InvalidateVisual();
        }
    }

    public CommandManager Commands { get; } = new();

    public Page? CurrentPage => _document?.Pages.Count > 0 ? _document.CurrentPage : null;
    public ViewportState? Viewport => CurrentPage?.Viewport;
    public SmoothGeometryBuilder Geometry => _geometry;
    public ToolDispatcher PointerDispatcher => _dispatcher;

    /// <summary>最近一帧的渲染统计（状态栏用）。</summary>
    public FrameStats LastFrame { get; private set; }

    /// <summary>背景色（取自主题 color.txt）。</summary>
    public Color BackgroundColor
    {
        get => _renderer.BackgroundColor;
        set { _renderer.BackgroundColor = value; InvalidateVisual(); }
    }

    /// <summary>当前工具。</summary>
    public ITool? Tool => _tool;

    /// <summary>状态栏回调。</summary>
    public event Action<string>? StatusChanged;

    public void SetTool(ITool tool)
    {
        var ctx = Context();
        _dispatcher.SetTool(_tool, tool, ctx);
        _tool = tool;
        InvalidateVisual();
        StatusChanged?.Invoke($"工具：{tool.Name}");
    }

    public void Undo()
    {
        CancelPending();
        StatusChanged?.Invoke(Commands.Undo() ? "已撤销" : "没有可撤销的操作");
        InvalidateVisual();
    }

    public void Redo()
    {
        CancelPending();
        StatusChanged?.Invoke(Commands.Redo() ? "已重做" : "没有可重做的操作");
        InvalidateVisual();
    }

    /// <summary>丢弃当前未提交的中间状态（切页/切工具/平移前调用）。</summary>
    public void CancelPending() => _dispatcher.Reset(_tool);

    /// <summary>当前画笔颜色（#RRGGBB）。</summary>
    public string PenColor
    {
        get => _penColor;
        set
        {
            _penColor = value;
            _penBrush = GeometryInterop.BrushFromHex(value);
            StatusChanged?.Invoke($"颜色：{value}");
        }
    }

    /// <summary>当前笔宽（世界单位；三档 3 / 6 / 10，ADR-15）。</summary>
    public double PenWidth
    {
        get => _penWidth;
        set
        {
            _penWidth = value;
            StatusChanged?.Invoke($"笔宽：{value:0.#}");
        }
    }

    /// <summary>以画布中心为锚点缩放（工具栏用）。</summary>
    public void ZoomBy(double factor)
    {
        var vp = Viewport;
        if (vp is null) return;
        vp.ZoomAt(new PointD(ActualWidth / 2, ActualHeight / 2), vp.Zoom * factor);
        InvalidateVisual();
        StatusChanged?.Invoke($"缩放：{vp.Zoom * 100:0.#}%");
    }

    /// <summary>恢复 100% 且回到原点。</summary>
    public void ResetView()
    {
        var vp = Viewport;
        if (vp is null) return;
        vp.Zoom = 1.0;
        vp.PanX = 0;
        vp.PanY = 0;
        InvalidateVisual();
        StatusChanged?.Invoke("视图已重置（100%）");
    }

    /// <summary>当前页发生变化（切页、增删页、撤销页面操作后）。</summary>
    public event Action? PageChanged;

    /// <summary>
    /// 用户把选中的对象**拖出了画布**（例如拖到左侧页面目录的缩略图上）。
    /// 参数是拖出时指针的屏幕坐标；由窗口负责判断落在了哪个缩略图上。
    /// 返回 true 表示窗口接受了这次拖放（画布据此不再自行处理）。
    /// </summary>
    public Func<Point, bool>? SelectionDraggedOut { get; set; }

    /// <summary>切到指定页（越界自动钳制）。</summary>
    public void GoToPage(int index)
    {
        var doc = _document;
        if (doc is null || doc.Pages.Count == 0) return;

        var target = Math.Clamp(index, 0, doc.Pages.Count - 1);
        if (target == doc.CurrentPageIndex) return;

        _dispatcher.Reset(_tool);
        Selection.Clear();
        doc.CurrentPageIndex = target;
        Commands.SetCurrentPage(doc.CurrentPage.Id);
        InvalidateVisual();
        PageChanged?.Invoke();
    }

    /// <summary>上一页 / 下一页。</summary>
    public void GoToPreviousPage()
    {
        if (_document is null) return;
        GoToPage(_document.CurrentPageIndex - 1);
    }

    public void GoToNextPage()
    {
        if (_document is null) return;
        GoToPage(_document.CurrentPageIndex + 1);
    }

    /// <summary>把选中的对象搬到另一页（一条可撤销命令）。返回搬了几个。</summary>
    public int MoveSelectionToPage(int targetPageIndex)
    {
        var doc = _document;
        var page = CurrentPage;
        if (doc is null || page is null || Selection.IsEmpty) return 0;

        var target = Math.Clamp(targetPageIndex, 0, doc.Pages.Count - 1);
        if (doc.Pages[target] == page) return 0;   // 搬到自己这页：不算操作

        _dispatcher.Reset(_tool);

        var objs = Selection.Objects.ToList();
        Commands.Execute(new MoveObjectsToPageCommand(doc, page, doc.Pages[target], objs));
        Selection.Clear();
        InvalidateVisual();
        return objs.Count;
    }

    /// <summary>当前页缩略图需要刷新时触发（内容变了）。</summary>
    public event Action? ContentChanged;

    private ToolContext Context()
    {
        var page = CurrentPage ?? throw new InvalidOperationException("没有可用页面");
        return new ToolContext
        {
            Page = page,
            Viewport = page.Viewport,
            Commands = Commands,
            Geometry = _geometry,
            AllocateObjectId = () => _document?.AllocateObjectId() ?? 1,
            PenColor = _penColor,
            PenWidth = _penWidth,
            Selection = Selection,
            TextEditor = TextEditorHost
        };
    }

    // ── 选中对象的操作（删除 / 置顶 / 置底 / 全选）─────────────────────────

    /// <summary>删除选中的对象（一条可撤销命令）。返回是否真的删了东西。</summary>
    public bool DeleteSelection()
    {
        var page = CurrentPage;
        if (page is null || Selection.IsEmpty) return false;

        _dispatcher.Reset(_tool);

        var objs = Selection.Objects.ToList();
        Commands.Execute(new RemoveObjectsCommand(page, objs));
        Selection.Clear();
        InvalidateVisual();
        return true;
    }

    /// <summary>把选中的对象置顶 / 置底。多个对象时合成一条撤销项。</summary>
    public bool ChangeSelectionZOrder(bool toFront)
    {
        var page = CurrentPage;
        if (page is null || Selection.IsEmpty) return false;

        var mode = toFront ? ZOrderCommand.Mode.Front : ZOrderCommand.Mode.Back;
        var parts = new List<WhiteBoard.Core.Commands.ICommand>();

        foreach (var obj in Selection.Objects.ToList())
        {
            var cmd = new ZOrderCommand(page, obj, mode);
            cmd.Do();
            parts.Add(cmd);
        }

        Commands.PushTransaction(toFront ? "置顶" : "置底", parts);
        InvalidateVisual();
        return true;
    }

    /// <summary>全选当前页。</summary>
    public bool SelectAll()
    {
        var page = CurrentPage;
        if (page is null || page.Count == 0) return false;

        Selection.SetMany(page.Objects);
        InvalidateVisual();
        return true;
    }

    public void ClearSelection()
    {
        Selection.Clear();
        InvalidateVisual();
    }

    // ── 输入 ──────────────────────────────────────────────────────────────

    private bool OnPointer(PointerAction action, PointerSample sample)
    {
        if (CurrentPage is null) return false;

        // 选择工具拖动中、且指针已经拖出画布（例如要拖到左侧页面目录的缩略图上）：
        // 交给窗口判断落点在哪个缩略图上。
        //
        // 注意：**没落在缩略图上时不能打断这次拖动**。
        // 人拖动的真实轨迹是"先越过画布左边界，再挪到缩略图上"，
        // 越过边界那一下几乎不可能正好压在缩略图上；
        // 如果第一次没命中就取消，用户会觉得"拖到缩略图根本没用"（这条是实测抓出来的）。
        if (action == PointerAction.Move &&
            _tool is SelectTool { IsMoving: true } &&
            SelectionDraggedOut is not null &&
            IsOutsideBounds(sample))
        {
            var screen = PointToScreen(new Point(sample.X, sample.Y));
            if (SelectionDraggedOut(screen))
            {
                // 已经搬走了：收拾这次拖动的中间状态
                _dispatcher.Reset(_tool);
                InvalidateVisual();
                return true;
            }

            // 还没落在缩略图上 → 继续这次拖动（对象跟着指针走，画布会把它裁掉，无妨）
        }

        var result = _dispatcher.Dispatch(action, sample, _tool, Context(), _pointer.StylusActive);

        // 鼠标按下开始交互时**捕获鼠标**：这样指针移出画布（例如把选中的对象拖到左侧页面目录的
        // 缩略图上）时仍然能收到 Move 事件。
        //
        // 不捕获的话，指针一离开画布，WPF 就把后续事件发给光标下的那个元素，
        // 画布再也收不到 Move —— "拖到缩略图"这个手势根本走不完（实测踩过这个坑）。
        if (sample.Kind == PointerKind.Mouse)
        {
            if (action == PointerAction.Down && result.Handled) CaptureMouse();
            else if (action == PointerAction.Up && IsMouseCaptured && !_panning) ReleaseMouseCapture();
        }

        if (result.Message is { } msg) StatusChanged?.Invoke(msg);
        if (result.Outcome is DispatchOutcome.Stroke or DispatchOutcome.Gesture) InvalidateVisual();

        return result.Handled;
    }

    private bool IsOutsideBounds(PointerSample s)
        => s.X < 0 || s.Y < 0 || s.X > ActualWidth || s.Y > ActualHeight;

    // 中键平移 / 滚轮缩放：桌面鼠标也能操作（触屏走 PointerRouter 的双指手势）
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && Viewport is not null)
        {
            _panning = true;
            _panLast = e.GetPosition(this);
            CancelPending();
            CaptureMouse();
            e.Handled = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_panning && Viewport is { } vp)
        {
            var p = e.GetPosition(this);
            vp.PanByScreen(p.X - _panLast.X, p.Y - _panLast.Y);
            _panLast = p;
            InvalidateVisual();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _panning)
        {
            _panning = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Viewport is { } vp)
        {
            var anchor = e.GetPosition(this);
            var factor = Math.Pow(1.1, e.Delta / 120.0);
            vp.ZoomAt(new PointD(anchor.X, anchor.Y), vp.Zoom * factor);
            InvalidateVisual();
            StatusChanged?.Invoke($"缩放：{vp.Zoom * 100:0.#}%（{vp.PanX:0.#},{vp.PanY:0.#}）");
            e.Handled = true;
        }
        base.OnMouseWheel(e);
    }

    // ── 渲染 ──────────────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var page = CurrentPage;
        if (page is null)
        {
            dc.DrawRectangle(new SolidColorBrush(_renderer.BackgroundColor), null, new Rect(0, 0, w, h));
            return;
        }

        var vp = page.Viewport;
        LastFrame = _renderer.Render(dc, page, vp, w, h);

        // 湿态预览：与干笔迹同一视口变换、同一几何来源 → 抬笔零跳变（M0 §六-D）
        if (_tool?.BuildPreview(Context()) is { } preview)
        {
            var worldToScreen = new Matrix(vp.Zoom, 0, 0, vp.Zoom,
                -vp.PanX * vp.Zoom, -vp.PanY * vp.Zoom);
            dc.PushTransform(new MatrixTransform(worldToScreen));
            dc.DrawGeometry(_penBrush, null, preview);
            dc.Pop();
        }

        DrawSelectionOverlay(dc, page, vp);
    }

    /// <summary>
    /// 选中框与框选轨迹：**在屏幕空间绘制**。
    /// 这样线宽是恒定的 1~1.5px（缩放到 50 倍也不会变成一条粗带），
    /// 也不会因为对象被旋转/缩放而变形。
    /// </summary>
    private void DrawSelectionOverlay(DrawingContext dc, Page page, ViewportState vp)
    {
        // ① 选中对象的虚线框（带一点半透明填充，投影仪上更醒目）
        if (!Selection.IsEmpty)
        {
            foreach (var obj in Selection.Objects)
            {
                var b = obj.WorldBounds;
                var tl = vp.WorldToScreen(b.X, b.Y);
                var br = vp.WorldToScreen(b.Right, b.Bottom);
                var rect = new Rect(
                    new Point(Math.Min(tl.X, br.X), Math.Min(tl.Y, br.Y)),
                    new Point(Math.Max(tl.X, br.X), Math.Max(tl.Y, br.Y)));

                // 只有 1 个对象时才填充，多选只画框（避免整片变黄挡住内容）
                dc.DrawRectangle(Selection.Count == 1 ? _selectionFill : null, _selectionPen, rect);
            }
        }

        // ② 正在进行的框选/圈选轨迹
        if (_tool is SelectTool select && select.MarqueeScreen is { Count: >= 2 } track)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(track[0].ToWpf(), false, select.MarqueeIsLasso);
                g.PolyLineTo(track.Skip(1).Select(p => p.ToWpf()).ToList(), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, _marqueePen, geometry);
        }
    }
}
