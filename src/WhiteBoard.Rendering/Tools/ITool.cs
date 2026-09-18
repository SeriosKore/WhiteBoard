using System.Windows.Media;
using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Input;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Rendering.Tools;

/// <summary>工具运行所需的上下文（由画布提供）。</summary>
public sealed class ToolContext
{
    public required Page Page { get; init; }
    public required ViewportState Viewport { get; init; }
    public required CommandManager Commands { get; init; }
    public required SmoothGeometryBuilder Geometry { get; init; }
    public required Func<int> AllocateObjectId { get; init; }

    /// <summary>当前画笔颜色（#RRGGBB）。</summary>
    public string PenColor { get; set; } = "#F5F5F0";

    /// <summary>当前笔宽（世界单位；三档 细3 / 中6 / 粗10）。</summary>
    public double PenWidth { get; set; } = 3;

    /// <summary>橡皮默认直径（屏幕像素）。</summary>
    public double EraserDiameterPx { get; set; } = 60;

    /// <summary>
    /// 当前页的选中集合。放在上下文里（而不是工具内部）是因为选中态要跨工具切换存活：
    /// 用户选了几个对象、切去橡皮擦一下、再切回来，选中不该丢。
    /// </summary>
    public SelectionState Selection { get; init; } = new();

    /// <summary>
    /// 就地文本编辑的宿主（UI 层提供）。为 null 时文本工具无法工作，
    /// 但其余工具完全不受影响——这也让文本工具能在离屏测试里被单独验证。
    /// </summary>
    public ITextEditHost? TextEditor { get; init; }

    public PointD ScreenToWorld(PointD screen) => Viewport.ScreenToWorld(screen);
    public PointD WorldToScreen(PointD world) => Viewport.WorldToScreen(world);
}

/// <summary>
/// 工具接口。所有坐标转换在画布侧完成，工具只面对**屏幕坐标的 PointerSample**
/// 与上下文里的视口，因此可以离屏单测（无需窗口）。
/// </summary>
public interface ITool
{
    string Name { get; }

    void OnActivated(ToolContext ctx);
    void OnDeactivated(ToolContext ctx);

    /// <summary>
    /// 丢弃当前未提交的中间状态（第二指到来判定为手势、失焦、切页时调用）。
    /// 已提交的对象不受影响。
    /// </summary>
    void Cancel();

    /// <summary>处理一次指针事件；返回该事件是否被消费。</summary>
    bool OnPointer(PointerAction action, PointerSample sample, ToolContext ctx);

    /// <summary>
    /// 未提交的预览几何（**世界坐标**），可为 null。
    /// 画布每帧调用它绘制"湿态"——注意必须与提交后的干笔迹同源，否则抬笔会跳变（M0 §六-D）。
    /// </summary>
    Geometry? BuildPreview(ToolContext ctx);

    /// <summary>该工具当前是否把"手掌接触"当作大号橡皮（决策 D3）。</summary>
    bool PalmActsAsEraser { get; }
}
