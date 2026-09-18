using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using WhiteBoard.Core.Input;

namespace WhiteBoard.App.Input;

/// <summary>
/// WPF 输入适配：把 <c>StylusDown/Move/Up</c>、<c>TouchDown/Move/Up</c>、<c>Mouse*</c>
/// 统一转成 <see cref="PointerSample"/> 喂给 <see cref="PointerRouter"/>。
///
/// 这里只做"翻译"，不做决策 —— 优先级、合成鼠标抑制、双指与书写冲突全部在
/// <see cref="PointerRouter"/>（Core，已被单元测试覆盖）。
/// </summary>
public sealed class WpfPointerSource
{
    private readonly PointerRouter _router = new();

    /// <summary>由一个 WPF 元素接管输入。</summary>
    public WpfPointerSource(FrameworkElement element)
    {
        Element = element;
        element.PreviewStylusDown += OnStylusDown;
        element.PreviewStylusMove += OnStylusMove;
        element.PreviewStylusUp += OnStylusUp;
        element.PreviewTouchDown += OnTouchDown;
        element.PreviewTouchMove += OnTouchMove;
        element.PreviewTouchUp += OnTouchUp;
        element.PreviewMouseDown += OnMouseDown;
        element.PreviewMouseMove += OnMouseMove;
        element.PreviewMouseUp += OnMouseUp;
    }

    public FrameworkElement Element { get; }
    public PointerRouter Router => _router;

    /// <summary>是否已观察到笔/触摸（用于掌拒判断）。</summary>
    public bool StylusActive { get; private set; }

    /// <summary>指针事件回调：<c>(action, sample)</c>。返回值表示是否已消费。</summary>
    public event Func<PointerAction, PointerSample, bool>? PointerEvent;

    public void Detach()
    {
        Element.PreviewStylusDown -= OnStylusDown;
        Element.PreviewStylusMove -= OnStylusMove;
        Element.PreviewStylusUp -= OnStylusUp;
        Element.PreviewTouchDown -= OnTouchDown;
        Element.PreviewTouchMove -= OnTouchMove;
        Element.PreviewTouchUp -= OnTouchUp;
        Element.PreviewMouseDown -= OnMouseDown;
        Element.PreviewMouseMove -= OnMouseMove;
        Element.PreviewMouseUp -= OnMouseUp;
    }

    private static long Now => Stopwatch.GetTimestamp();

    private void Dispatch(PointerAction action, PointerSample sample)
        => PointerEvent?.Invoke(action, sample);

    private PointerSample FromStylus(StylusEventArgs e, int id)
    {
        var p = e.GetPosition(Element);
        float pressure = 0.5f;
        try
        {
            foreach (var sp in e.GetStylusPoints(Element))
            {
                pressure = sp.PressureFactor;
                break;
            }
        }
        catch (Exception)
        {
        }
        return new PointerSample(id, PointerKind.Stylus, p.X, p.Y, pressure, 0, Now);
    }

    private PointerSample FromTouch(TouchEventArgs e)
    {
        var t = e.GetTouchPoint(Element);
        // 触点面积作为压力/手掌判定的粗代理（正式实现可接入更细的接触几何）
        var width = t.Size.Width;
        var pressure = (float)Math.Clamp(width / 40.0, 0.1, 1.0);
        return new PointerSample(t.TouchDevice.Id, PointerKind.Touch, t.Position.X, t.Position.Y,
            pressure, width, Now);
    }

    private PointerSample FromMouse(MouseEventArgs e)
    {
        var p = e.GetPosition(Element);
        return new PointerSample(0, PointerKind.Mouse, p.X, p.Y, 0.5f, 0, Now);
    }

    private void OnStylusDown(object? s, StylusDownEventArgs e)
    {
        StylusActive = true;
        Dispatch(PointerAction.Down, FromStylus(e, e.StylusDevice?.Id ?? 0));
        e.Handled = true;
    }

    private void OnStylusMove(object? s, StylusEventArgs e)
    {
        StylusActive = true;
        Dispatch(PointerAction.Move, FromStylus(e, e.StylusDevice?.Id ?? 0));
        e.Handled = true;
    }

    private void OnStylusUp(object? s, StylusEventArgs e)
    {
        Dispatch(PointerAction.Up, FromStylus(e, e.StylusDevice?.Id ?? 0));
        StylusActive = false;
        e.Handled = true;
    }

    private void OnTouchDown(object? s, TouchEventArgs e)
    {
        Dispatch(PointerAction.Down, FromTouch(e));
        e.Handled = true;
    }

    private void OnTouchMove(object? s, TouchEventArgs e)
    {
        Dispatch(PointerAction.Move, FromTouch(e));
        e.Handled = true;
    }

    private void OnTouchUp(object? s, TouchEventArgs e)
    {
        Dispatch(PointerAction.Up, FromTouch(e));
        e.Handled = true;
    }

    private void OnMouseDown(object? s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        Dispatch(PointerAction.Down, FromMouse(e));
        e.Handled = true;
    }

    private void OnMouseMove(object? s, MouseEventArgs e) => Dispatch(PointerAction.Move, FromMouse(e));

    private void OnMouseUp(object? s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        Dispatch(PointerAction.Up, FromMouse(e));
        e.Handled = true;
    }
}
