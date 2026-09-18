using WhiteBoard.Core.Geometry;

namespace WhiteBoard.Core.Input;

/// <summary>指针类型。</summary>
public enum PointerKind
{
    /// <summary>电磁笔/电容笔（最高优先级）。</summary>
    Stylus = 0,

    /// <summary>手指触摸。</summary>
    Touch = 1,

    /// <summary>鼠标（最低优先级）。</summary>
    Mouse = 2
}

public enum PointerAction { Down, Move, Up }

/// <summary>
/// 一次指针采样（**屏幕坐标**；由调用方完成 WPF → 本结构的转换）。
/// 全部为纯数据，便于在无窗口环境下做单元测试。
/// </summary>
public readonly record struct PointerSample(
    int Id,
    PointerKind Kind,
    double X,
    double Y,
    float Pressure,          // 0..1（鼠标恒为 0.5；触摸用接触面积折算）
    double ContactWidth,     // 触点宽度（触摸面积代理；笔/鼠标为 0）
    long TimestampTicks)     // Stopwatch.GetTimestamp()
{
    public PointD Position => new(X, Y);

    /// <summary>压力归一化（0.01~1，避免 0 导致笔宽为 0）。</summary>
    public float SafePressure => Pressure <= 0 ? 0.5f : Math.Clamp(Pressure, 0.01f, 1f);
}

/// <summary>路由决策。</summary>
public enum RouteDecision
{
    /// <summary>忽略（合成鼠标事件、低优先级指针、手势期间的笔/鼠标等）。</summary>
    Ignore,

    BeginStroke,
    ContinueStroke,
    EndStroke,

    StartGesture,
    UpdateGesture,
    EndGesture
}

/// <summary>
/// 路由结果。
/// <para>
/// <see cref="CancelledStroke"/> 为 true 时表示：**当前正在进行的笔画应被丢弃**，
/// 因为第二根手指及时到来、判定为手势（见 <see cref="PointerRouter.GestureWindowMs"/>）。
/// </para>
/// </summary>
public readonly record struct RouteResult(
    RouteDecision Decision,
    PointerSample Sample,
    PointerKind? Owner,
    bool CancelledStroke = false)
{
    public bool IsStroke => Decision is RouteDecision.BeginStroke or RouteDecision.ContinueStroke or RouteDecision.EndStroke;
    public bool IsGesture => Decision is RouteDecision.StartGesture or RouteDecision.UpdateGesture or RouteDecision.EndGesture;
    public static RouteResult Ignored(PointerSample s, PointerKind? owner = null) => new(RouteDecision.Ignore, s, owner);
}
