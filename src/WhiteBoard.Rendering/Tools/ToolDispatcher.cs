using WhiteBoard.Core.Input;

namespace WhiteBoard.Rendering.Tools;

/// <summary>一次指针事件的处置结果。</summary>
public enum DispatchOutcome
{
    /// <summary>被忽略（合成鼠标事件、低优先级指针、手势期间的笔等）。</summary>
    Ignored,

    /// <summary>交给了工具（落笔/走笔/抬笔）。</summary>
    Stroke,

    /// <summary>手势（缩放/平移）。</summary>
    Gesture,

    /// <summary>被掌拒（决策 D3）。</summary>
    PalmRejected
}

public readonly record struct DispatchResult(DispatchOutcome Outcome, bool Handled, string? Message);

/// <summary>
/// 指针事件 → 工具 的**完整处置链**：掌拒 → <see cref="PointerRouter"/> 路由 → 手势/工具。
///
/// 这一层刻意不依赖 WPF：输入来自 <see cref="PointerSample"/>（纯数据），
/// 因此同一条链路既能被 <c>CanvasHost</c> 驱动，也能在单元测试里被合成事件驱动——
/// 保证"测试通过"与"真的能画"是同一段代码（M0 评审要求）。
/// </summary>
public sealed class ToolDispatcher
{
    private readonly PointerRouter _router = new();
    private readonly TouchGestureRecognizer _gesture = new();
    private readonly PalmRejector _palm = new();

    public PointerRouter Router => _router;
    public PalmRejector Palm => _palm;
    public bool IsGestureActive => _gesture.IsActive;
    public bool IsStrokeActive => _router.IsStrokeActive;

    /// <summary>被掌拒的次数（诊断用）。</summary>
    public int PalmRejectCount { get; private set; }

    /// <summary>被"手势接管"丢弃的起笔次数（ADR-19，诊断用）。</summary>
    public int CancelledStrokeCount { get; private set; }

    /// <summary>切换工具：同步掌拒策略，并丢弃上一个工具的未提交状态。</summary>
    public void SetTool(ITool? previous, ITool? next, ToolContext ctx)
    {
        previous?.OnDeactivated(ctx);
        _palm.PalmActsAsEraser = next?.PalmActsAsEraser ?? false;
        Reset(next);
        next?.OnActivated(ctx);
    }

    /// <summary>丢弃全部未提交状态（切页、失焦、中键平移时调用）。</summary>
    public void Reset(ITool? tool)
    {
        tool?.Cancel();
        _router.Reset();
        _gesture.End();
    }

    /// <summary>处理一次指针事件。所有视口变更（手势缩放/平移）都在这里发生。</summary>
    public DispatchResult Dispatch(
        PointerAction action, PointerSample sample, ITool? tool, ToolContext ctx, bool stylusActive)
    {
        // ① 掌拒 / 手掌擦除（决策 D3、ADR-11）
        if (action == PointerAction.Down && _palm.ShouldReject(sample, stylusActive))
        {
            PalmRejectCount++;
            return new DispatchResult(DispatchOutcome.PalmRejected, true, "已拒绝手掌接触");
        }

        // ② 路由（优先级、合成鼠标抑制、双指与书写冲突）
        var route = _router.Process(action, sample);

        switch (route.Decision)
        {
            case RouteDecision.Ignore:
                return new DispatchResult(DispatchOutcome.Ignored, true, null);

            case RouteDecision.StartGesture:
                if (route.CancelledStroke)
                {
                    // 第二指及时到来 → 丢弃这点头笔画（ADR-19）
                    tool?.Cancel();
                    CancelledStrokeCount++;
                }
                _gesture.Begin(_router.GestureTouches, ctx.Viewport);
                return new DispatchResult(DispatchOutcome.Gesture, true,
                    route.CancelledStroke ? "手势接管，已丢弃起笔" : "手势：缩放/平移");

            case RouteDecision.UpdateGesture:
                _gesture.ApplyTo(ctx.Viewport, _router.GestureTouches);
                return new DispatchResult(DispatchOutcome.Gesture, true, null);

            case RouteDecision.EndGesture:
                _gesture.End();
                return new DispatchResult(DispatchOutcome.Gesture, true, "手势结束");
        }

        // ③ BeginStroke / ContinueStroke / EndStroke → 交给工具
        if (tool is null) return new DispatchResult(DispatchOutcome.Ignored, false, null);

        var handled = tool.OnPointer(action, sample, ctx);
        var message = action switch
        {
            PointerAction.Down when handled => $"落笔（{sample.Kind}，压力 {sample.SafePressure:0.00}）",
            PointerAction.Up when handled => $"抬笔；本页对象数 {ctx.Page.Count}",
            _ => null
        };
        return new DispatchResult(handled ? DispatchOutcome.Stroke : DispatchOutcome.Ignored, handled, message);
    }

    /// <summary>合成事件辅助：把一个屏幕点序列当作一次抬落笔（测试与脚本录制用）。</summary>
    public static int Replay(
        ToolDispatcher dispatcher, ITool? tool, ToolContext ctx,
        IReadOnlyList<PointerSample> samples, bool stylusActive = false)
    {
        var handled = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var action = i == 0 ? PointerAction.Down
                : i == samples.Count - 1 ? PointerAction.Up
                : PointerAction.Move;

            if (dispatcher.Dispatch(action, samples[i], tool, ctx, stylusActive).Handled) handled++;
        }
        return handled;
    }
}
