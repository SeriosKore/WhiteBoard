using System.Windows.Media;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Input;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Rendering.Tools;

/// <summary>
/// 就地文本编辑的宿主（由 UI 层实现）。
///
/// 为什么做成接口而不是直接在工具里编辑：
/// 评审 T5 已经定了路线——**编辑时叠加一个真正的 <c>TextBox</c>**。
/// 那个 `TextBox` 是 WPF 控件，属于 UI 层；而 <see cref="TextTool"/> 是可离屏单测的纯逻辑。
/// 中间用这个接口隔开，两边都能各自测试：工具只管"该在哪儿开始编辑"，
/// 宿主负责"把 TextBox 摆到那儿、收 IME、提交成对象"。
/// </summary>
public interface ITextEditHost
{
    /// <summary>是否正在编辑（画布据此抑制其他工具动作）。</summary>
    bool IsEditing { get; }

    /// <summary>
    /// 开始就地编辑。<paramref name="existing"/> 为 null 表示**新建**一段文字，
    /// 否则是在**修改**已有文本对象。
    /// </summary>
    /// <param name="worldPoint">点击处的世界坐标（新建时作为文本框左上角）。</param>
    void BeginTextEdit(PointD worldPoint, TextObject? existing);

    /// <summary>提交当前编辑（切工具、保存、导出前都应调用，避免留下半截编辑状态）。</summary>
    void CommitTextEdit();

    /// <summary>放弃当前编辑（Esc）。</summary>
    void CancelTextEdit();
}

/// <summary>
/// 文本工具：在画布上点一下即可就地输入（S1 只做静态文本、不支持旋转）。
///
/// 交互约定：
/// <list type="bullet">
/// <item>点空白处 → 新建一段文字，**点击位置就是文本框左上角**；</item>
/// <item>点已有文字 → 修改它（而不是叠一段新的上去）；</item>
/// <item>提交/取消由宿主负责（Enter 换行、Ctrl+Enter 或点击别处提交、Esc 取消）；</item>
/// <item>文本**不参与橡皮擦除**（基线要求）：要删文字用选择工具 + Del。</item>
/// </list>
/// </summary>
public sealed class TextTool : ITool
{
    private readonly HitTester _hits;

    public TextTool(SmoothGeometryBuilder geometry)
    {
        _hits = new HitTester(geometry);
    }

    public string Name => "文本";

    /// <summary>文本参与掌拒（手掌不该在画布上打字）。</summary>
    public bool PalmActsAsEraser => false;

    /// <summary>新建文字用的字号（世界单位）。</summary>
    public double FontSize { get; set; } = TextObject.MediumFontSize;

    /// <summary>字体族名。</summary>
    public string FontFamily { get; set; } = TextObject.DefaultFontFamily;

    public bool Bold { get; set; }
    public bool Italic { get; set; }

    /// <summary>最近一次请求编辑的位置（诊断与测试用）。</summary>
    public PointD? LastRequestedPoint { get; private set; }

    public void OnActivated(ToolContext ctx)
    {
        LastRequestedPoint = null;
    }

    public void OnDeactivated(ToolContext ctx)
    {
        // 切走工具时把正在编辑的文字提交掉，避免"编辑框跟着工具跑了但内容没落库"
        ctx.TextEditor?.CommitTextEdit();
        LastRequestedPoint = null;
    }

    public void Cancel()
    {
        LastRequestedPoint = null;
    }

    public bool OnPointer(PointerAction action, PointerSample sample, ToolContext ctx)
    {
        if (action != PointerAction.Down) return false;

        var host = ctx.TextEditor;
        if (host is null) return false;

        var world = ctx.ScreenToWorld(sample.Position);
        LastRequestedPoint = world;

        // 点在已有文字上 → 改它；否则在那一点新建
        var hit = _hits.HitTestTopmost(ctx.Page, world) as TextObject;
        host.BeginTextEdit(world, hit);
        return true;
    }

    /// <summary>文本没有湿态预览（编辑过程由 TextBox 现场呈现）。</summary>
    public Geometry? BuildPreview(ToolContext ctx) => null;
}
