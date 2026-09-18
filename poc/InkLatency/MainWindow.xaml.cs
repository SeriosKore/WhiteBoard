using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WhiteBoard.Core.Storage;
using WhiteBoard.Core.Theme;

namespace WhiteBoard.Poc.InkLatency;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, IInkRoute> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly LatencyProbe _probe = new();
    private readonly List<InputPoint> _points = [];

    private IInkRoute _route;
    private int _strokeSeq;
    private bool _drawing;
    private DateTime _lastNonMouseInput = DateTime.MinValue;
    private Color _penColor = Colors.White;
    private double _penWidth = 4;
    private readonly Dictionary<string, Button> _modeButtons = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();

        _routes["baseline"] = new BaselineRoute();
        _routes["a1"] = new OverlayRoute();
        _routes["a2"] = new SingleLayerRoute();

        var initial = _routes.TryGetValue(App.InitialMode, out var r) ? r : _routes["a1"];
        _route = initial;

        BuildEnvironmentText();
        BuildModeButtons();
        BuildPalette();
        BuildWidthButtons();

        ClearButton.Click += (_, _) => { _route.ClearInk(); _probe.Reset(); };
        ExportButton.Click += (_, _) => DoExport();

        // 每帧调用探针：把"该帧开始渲染"这一刻与之前的输入配对，得到应用内延迟
        CompositionTarget.Rendering += (_, _) => _probe.NoteFrame();

        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background,
            (_, _) => RefreshStats(), Dispatcher);
        timer.Start();

        SwitchRoute(initial.Name);
    }

    private void BuildEnvironmentText()
    {
        var paths = PathService.Resolve();
        var theme = ThemeService.Load(paths);
        _penColor = theme.Preset.Colors.Count > 0 ? ParseBrush(theme.Preset.Colors[0].Value).Color : Colors.White;

        EnvText.Text =
            $"数据目录：{paths.DataRoot}　|　主题：{theme.Preset.Name}（{theme.Source}）　|　" +
            $"渲染后端：WPF（D3D 直通）　|　口径：输入抵达 → 含该笔迹的帧开始渲染（不含合成与显示）";

        if (!string.IsNullOrWhiteSpace(theme.Warning))
            EnvText.Text += "　|　⚠️ " + theme.Warning;
    }

    private void BuildModeButtons()
    {
        foreach (var name in new[] { "baseline", "a1", "a2" })
        {
            var route = _routes[name];
            var btn = new Button
            {
                Content = route.Description,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 6, 0),
                Tag = name
            };
            btn.Click += (s, _) => SwitchRoute((string)((Button)s!).Tag);
            _modeButtons[name] = btn;
            ModePanel.Children.Add(btn);
        }
    }

    private void BuildPalette()
    {
        var paths = PathService.Resolve();
        var preset = ThemeService.Load(paths).Preset;

        foreach (var c in preset.Colors)
        {
            var color = ParseBrush(c.Value).Color;
            var dot = new Border
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 6, 0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(color),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                ToolTip = $"{c.Name} {c.Value}",
                Cursor = Cursors.Hand,
                Tag = color
            };
            dot.MouseLeftButtonDown += (s, _) =>
            {
                _penColor = (Color)((Border)s!).Tag;
                _route.SetPen(_penColor, _penWidth);
            };
            ColorPanel.Children.Add(dot);
        }

        // 额外给一个黑色，便于在白底/黑板色上都能试
        var black = new Border
        {
            Width = 30, Height = 30, Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Colors.Black),
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            ToolTip = "黑 #000000", Cursor = Cursors.Hand, Tag = Colors.Black
        };
        black.MouseLeftButtonDown += (s, _) =>
        {
            _penColor = (Color)((Border)s!).Tag;
            _route.SetPen(_penColor, _penWidth);
        };
        ColorPanel.Children.Add(black);
    }

    private void BuildWidthButtons()
    {
        foreach (var (label, value) in new (string, double)[] { ("细", 2), ("中", 4), ("粗", 8) })
        {
            var line = new Border
            {
                Width = 34,
                Height = 30,
                Margin = new Thickness(0, 0, 6, 0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                ToolTip = $"{label}（{value} px）",
                Cursor = Cursors.Hand
            };
            var inner = new Border
            {
                Height = value,
                Margin = new Thickness(4, 0, 4, 0),
                CornerRadius = new CornerRadius(value / 2),
                Background = new SolidColorBrush(Colors.White),
                VerticalAlignment = VerticalAlignment.Center
            };
            line.Child = inner;
            line.MouseLeftButtonDown += (_, _) =>
            {
                _penWidth = value;
                _route.SetPen(_penColor, _penWidth);
            };
            WidthPanel.Children.Add(line);
        }
    }

    private void SwitchRoute(string name)
    {
        _route = _routes[name];
        _probe.Mode = name;

        foreach (var (key, btn) in _modeButtons)
            btn.Background = key.Equals(name, StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0x4F))
                : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));

        DetachInput();
        RouteHost.Children.Clear();
        RouteHost.Children.Add(_route.View);
        _route.SetPen(_penColor, _penWidth);
        AttachInput();
    }

    // ── 统一输入：笔 > 触摸 > 鼠标（触摸/笔会合成鼠标事件，需忽略） ──────────────

    private void AttachInput()
    {
        var v = _route.View;
        v.PreviewStylusDown += OnStylusDown;
        v.PreviewStylusMove += OnStylusMove;
        v.PreviewStylusUp += OnStylusUp;
        v.PreviewTouchDown += OnTouchDown;
        v.PreviewTouchMove += OnTouchMove;
        v.PreviewTouchUp += OnTouchUp;
        v.PreviewMouseDown += OnMouseDown;
        v.PreviewMouseMove += OnMouseMove;
        v.PreviewMouseUp += OnMouseUp;
    }

    private void DetachInput()
    {
        var v = _route.View;
        v.PreviewStylusDown -= OnStylusDown;
        v.PreviewStylusMove -= OnStylusMove;
        v.PreviewStylusUp -= OnStylusUp;
        v.PreviewTouchDown -= OnTouchDown;
        v.PreviewTouchMove -= OnTouchMove;
        v.PreviewTouchUp -= OnTouchUp;
        v.PreviewMouseDown -= OnMouseDown;
        v.PreviewMouseMove -= OnMouseMove;
        v.PreviewMouseUp -= OnMouseUp;
    }

    private void OnStylusDown(object s, StylusDownEventArgs e) { _lastNonMouseInput = DateTime.UtcNow; Begin(ToPoints(e.GetStylusPoints(_route.View)), PointerKind.Stylus); }
    private void OnStylusMove(object s, StylusEventArgs e) { _lastNonMouseInput = DateTime.UtcNow; if (_drawing) Add(ToPoints(e.GetStylusPoints(_route.View)), PointerKind.Stylus); }
    private void OnStylusUp(object s, StylusEventArgs e) { _lastNonMouseInput = DateTime.UtcNow; if (_drawing) End(); }

    private void OnTouchDown(object? s, TouchEventArgs e) { _lastNonMouseInput = DateTime.UtcNow; Begin([FromTouch(e)], PointerKind.Touch); }
    private void OnTouchMove(object? s, TouchEventArgs e) { _lastNonMouseInput = DateTime.UtcNow; if (_drawing) Add([FromTouch(e)], PointerKind.Touch); }
    private void OnTouchUp(object? s, TouchEventArgs e) { _lastNonMouseInput = DateTime.UtcNow; if (_drawing) End(); }

    private void OnMouseDown(object s, MouseButtonEventArgs e)
    {
        if (IgnoreMouse()) return;
        Begin([FromMouse(e)], PointerKind.Mouse);
    }

    private void OnMouseMove(object s, MouseEventArgs e)
    {
        if (IgnoreMouse() || !_drawing) return;
        Add([FromMouse(e)], PointerKind.Mouse);
    }

    private void OnMouseUp(object s, MouseButtonEventArgs e)
    {
        if (IgnoreMouse() || !_drawing) return;
        End();
    }

    /// <summary>触摸/笔会合成鼠标事件；2 秒内出现过真实触摸/笔输入就忽略鼠标。</summary>
    private bool IgnoreMouse()
        => (DateTime.UtcNow - _lastNonMouseInput).TotalSeconds < 2.0;

    private InputPoint FromMouse(MouseEventArgs e)
    {
        var p = e.GetPosition(_route.View);
        return new InputPoint(p.X, p.Y, 0.5f, PointerKind.Mouse);
    }

    private InputPoint FromTouch(TouchEventArgs e)
    {
        var t = e.GetTouchPoint(_route.View);
        // 触点面积作为压力的粗代理（正式实现会用接触面积分档）
        var pressure = (float)Math.Clamp(t.Size.Width / 40.0, 0.1, 1.0);
        return new InputPoint(t.Position.X, t.Position.Y, pressure, PointerKind.Touch);
    }

    private static List<InputPoint> ToPoints(StylusPointCollection pts)
    {
        var list = new List<InputPoint>(pts.Count);
        foreach (var sp in pts)
            list.Add(new InputPoint(sp.X, sp.Y, sp.PressureFactor, PointerKind.Stylus));
        return list;
    }

    private void Begin(List<InputPoint> pts, PointerKind kind)
    {
        _points.Clear();
        _points.AddRange(pts);
        _drawing = true;
        foreach (var p in pts)
        {
            _probe.NoteInput(kind, p.Pressure);
            if (!_route.DrawsOwnWetInk) _route.OnWetPoint(p);
        }
    }

    private void Add(List<InputPoint> pts, PointerKind kind)
    {
        foreach (var p in pts)
        {
            _points.Add(p);
            _probe.NoteInput(kind, p.Pressure);
            if (!_route.DrawsOwnWetInk) _route.OnWetPoint(p);
        }
    }

    private void End()
    {
        _drawing = false;
        if (_points.Count == 0) return;

        var stroke = new InkStroke
        {
            Id = ++_strokeSeq * 7919,
            Color = _penColor,
            Width = _penWidth
        };
        stroke.Points.AddRange(_points);
        _route.OnStrokeEnd(stroke);
        _points.Clear();
    }

    private void RefreshStats()
    {
        var groups = _probe.Samples.GroupBy(s => s.Mode).OrderBy(g => g.Key).ToList();
        if (groups.Count == 0)
        {
            StatsText.Text = "尚无样本　（请在下方的画布区域连续书写）";
            return;
        }

        var lines = groups.Select(g =>
        {
            var st = ProbeStats.From(g.ToList());
            return $"{g.Key,-9} {st}";
        });
        StatsText.Text = string.Join(Environment.NewLine, lines);
    }

    private void DoExport()
    {
        try
        {
            var path = _probe.ExportCsv(App.CsvPath);
            var st = _probe.Stats();
            ExportText.Text = $"已导出：{path}　（样本 {st.Count}）";
        }
        catch (Exception ex)
        {
            ExportText.Text = $"导出失败：{ex.Message}";
        }
    }

    private static SolidColorBrush ParseBrush(string hex)
    {
        var (a, r, g, b) = ColorMath.ParseHex(hex);
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }
}
