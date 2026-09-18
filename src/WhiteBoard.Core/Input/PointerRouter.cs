using WhiteBoard.Core.Geometry;

namespace WhiteBoard.Core.Input;

/// <summary>
/// 指针路由：把 笔 / 触摸 / 鼠标 三路输入统一成一致的动作流。
///
/// 解决的三个真实问题（均来自 M0 与评审决策）：
/// <list type="number">
/// <item>**合成鼠标事件**：触屏上 WPF 会先抛 Touch/Stylus、再合成 Mouse，
///       不处理就会"画两遍"。这里在真实触摸/笔出现后的宽限期内忽略鼠标事件；</item>
/// <item>**优先级**：同一时刻只让一种指针拥有交互（笔 &gt; 触摸 &gt; 鼠标）；</item>
/// <item>**双指与书写冲突**（决策 Q4）：**笔画进行中忽略第二根手指**，直到笔画结束；
///       没有笔画进行时，两指才进入缩放手势。这样书写不会被打断，也避免手掌/手指误触。</item>
/// </list>
/// </summary>
public sealed class PointerRouter
{
    private readonly Dictionary<int, PointerSample> _touches = [];

    private PointerKind? _owner;
    private int? _strokePointerId;
    private bool _gestureActive;
    private long _lastNonMouseTicks = long.MinValue;
    private long _strokeStartTicks;
    private int _strokePointCount;

    /// <summary>真实触摸/笔出现后，忽略合成鼠标事件的时长（毫秒）。</summary>
    public int MouseSuppressMs { get; init; } = 2000;

    /// <summary>手势识别至少需要的触点数。</summary>
    public int GestureTouchCount { get; init; } = 2;

    /// <summary>
    /// **手势判定窗口（毫秒）**：第二根手指在笔画开始后的这段时间内落下，
    /// 视为用户想缩放/平移 → **丢弃这点头笔画并进入手势**；超过则视为误触，忽略第二指。
    /// <para>
    /// 这是"触摸零延迟书写"与"两指缩放"之间必须做的取舍：
    /// 若第一指按下后延时等待第二指，单指书写就会有可感知的起笔延迟；
    /// 若永不丢弃笔画，则两指永远无法进入手势。取 150 ms 兼顾两者。
    /// **笔（Stylus）不参与手势，因此不受此窗口影响，始终零延迟。**
    /// </para>
    /// </summary>
    public int GestureWindowMs { get; init; } = 150;

    /// <summary>手势判定窗口内允许的最大已采点数（再多就说明已经在认真书写了）。</summary>
    public int GestureMaxPoints { get; init; } = 6;

    public PointerKind? Owner => _owner;
    public int? StrokePointerId => _strokePointerId;
    public bool IsStrokeActive => _strokePointerId is not null;
    public bool IsGestureActive => _gestureActive;
    public int ActiveTouchCount => _touches.Count;

    /// <summary>当前参与手势的触点（按 Id 升序，保证中点/距离计算稳定）。</summary>
    public IReadOnlyList<PointerSample> GestureTouches
        => _touches.Values.OrderBy(t => t.Id).ToList();

    public RouteResult Process(PointerAction action, PointerSample sample)
    {
        switch (action)
        {
            case PointerAction.Down: return OnDown(sample);
            case PointerAction.Move: return OnMove(sample);
            default: return OnUp(sample);
        }
    }

    private RouteResult OnDown(PointerSample s)
    {
        if (s.Kind != PointerKind.Mouse) _lastNonMouseTicks = s.TimestampTicks;

        // ① 合成鼠标抑制
        if (s.Kind == PointerKind.Mouse && IsMouseSuppressed(s.TimestampTicks))
            return RouteResult.Ignored(s, _owner);

        // ② 触摸
        if (s.Kind == PointerKind.Touch)
        {
            if (_gestureActive)
            {
                _touches[s.Id] = s;
                return new RouteResult(RouteDecision.UpdateGesture, s, PointerKind.Touch);
            }

            // 第二指到来：若笔画"刚开始"（时间与点数都在窗口内）→ 判定为手势，丢弃这点头笔画
            if (IsStrokeActive && _strokePointerId != s.Id)
            {
                if (IsWithinGestureWindow(s.TimestampTicks))
                {
                    _touches[s.Id] = s;
                    _gestureActive = true;
                    _strokePointerId = null;
                    _owner = PointerKind.Touch;
                    return new RouteResult(RouteDecision.StartGesture, s, PointerKind.Touch, CancelledStroke: true);
                }

                // 已经写了较久 → 保护书写，忽略第二指（决策 Q4）
                return RouteResult.Ignored(s, _owner);
            }

            _touches[s.Id] = s;

            if (_touches.Count >= GestureTouchCount)
            {
                _gestureActive = true;
                return new RouteResult(RouteDecision.StartGesture, s, PointerKind.Touch);
            }

            _strokePointerId = s.Id;
            _owner = PointerKind.Touch;
            BeginStrokeTracking(s);
            return new RouteResult(RouteDecision.BeginStroke, s, PointerKind.Touch);
        }

        // ③ 笔 / 鼠标：手势进行中不接受，且不能抢占更高优先级的 owner
        if (_gestureActive) return RouteResult.Ignored(s, _owner);
        if (_owner is { } owner && owner != s.Kind && (int)owner < (int)s.Kind)
            return RouteResult.Ignored(s, _owner);

        _strokePointerId = s.Id;
        _owner = s.Kind;
        BeginStrokeTracking(s);
        return new RouteResult(RouteDecision.BeginStroke, s, s.Kind);
    }

    private void BeginStrokeTracking(PointerSample s)
    {
        _strokeStartTicks = s.TimestampTicks;
        _strokePointCount = 1;
    }

    /// <summary>第二指是否落在"手势判定窗口"内。</summary>
    private bool IsWithinGestureWindow(long nowTicks)
    {
        if (_strokePointCount > GestureMaxPoints) return false;
        var elapsedMs = (nowTicks - _strokeStartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return elapsedMs >= 0 && elapsedMs <= GestureWindowMs;
    }

    private RouteResult OnMove(PointerSample s)
    {
        if (s.Kind == PointerKind.Touch && _touches.ContainsKey(s.Id))
            _touches[s.Id] = s;

        if (_gestureActive) return new RouteResult(RouteDecision.UpdateGesture, s, PointerKind.Touch);

        if (_strokePointerId == s.Id)
        {
            _strokePointCount++;
            return new RouteResult(RouteDecision.ContinueStroke, s, _owner);
        }

        return RouteResult.Ignored(s, _owner);
    }

    private RouteResult OnUp(PointerSample s)
    {
        if (s.Kind == PointerKind.Touch) _touches.Remove(s.Id);

        if (_gestureActive)
        {
            if (_touches.Count < GestureTouchCount)
            {
                _gestureActive = false;
                _touches.Clear();
                return new RouteResult(RouteDecision.EndGesture, s, PointerKind.Touch);
            }
            return new RouteResult(RouteDecision.UpdateGesture, s, PointerKind.Touch);
        }

        if (_strokePointerId == s.Id)
        {
            _strokePointerId = null;
            _owner = null;
            return new RouteResult(RouteDecision.EndStroke, s, s.Kind);
        }

        return RouteResult.Ignored(s, _owner);
    }

    /// <summary>是否处于"忽略合成鼠标"的宽限期内。</summary>
    public bool IsMouseSuppressed(long nowTicks)
    {
        if (_lastNonMouseTicks == long.MinValue) return false;
        var elapsedMs = (nowTicks - _lastNonMouseTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return elapsedMs >= 0 && elapsedMs < MouseSuppressMs;
    }

    /// <summary>强制复位（失焦、工具切换时调用）。</summary>
    public void Reset()
    {
        _touches.Clear();
        _owner = null;
        _strokePointerId = null;
        _gestureActive = false;
        _strokePointCount = 0;
        _strokeStartTicks = 0;
    }
}
