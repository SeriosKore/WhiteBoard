using System.Windows;
using WhiteBoard.App.Diagnostics;
using WhiteBoard.Rendering;

namespace WhiteBoard.App;

public partial class App : Application
{
    /// <summary>启动参数（MainWindow 会用到 <c>--demo</c> 等）。</summary>
    public static string[] StartupArgs { get; private set; } = [];

    /// <summary><c>--demo</c>：启动时载入示例画板（人工验收用，走的是同一条生产绘制链路）。</summary>
    public static bool IsDemoRequested { get; private set; }

    /// <summary><c>--demo-select</c>：载入示例画板并预先选中两个对象（用于验收选中框的绘制）。</summary>
    public static bool IsDemoSelectRequested { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupArgs = e.Args;
        IsDemoRequested = e.Args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        IsDemoSelectRequested = e.Args.Any(a => a.Equals("--demo-select", StringComparison.OrdinalIgnoreCase));
        if (IsDemoSelectRequested) IsDemoRequested = true;

        // 免环境自检入口：WhiteBoard.exe --self-test [--report <path>]
        // 供 CI / Windows 沙箱 / 全新虚拟机做"零拷贝可运行"验证（退出码 0 = 通过）。
        if (SelfTest.IsRequested(e.Args))
        {
            var exitCode = SelfTest.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        // 渲染自检入口：WhiteBoard.exe --render-smoke --out <png> [--report <txt>]
        // 用生产链路合成一次真实绘制并渲染成 PNG + 逐区域数像素（退出码 0 = 通过）。
        if (BoardSmoke.IsRequested(e.Args))
        {
            var exitCode = BoardSmoke.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        // 文件层自检入口：WhiteBoard.exe --file-smoke [--report <txt>] [--keep <dir>]
        // 保存 → 重新打开 → 逐像素比较（证明"存了再打开还是同一张图"）。
        if (FileSmoke.IsRequested(e.Args))
        {
            var exitCode = FileSmoke.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        base.OnStartup(e);

        // 笔迹平滑方式（必须在建窗口之前设好，预览与干笔迹共用同一个静态设置）
        ApplySmoothingOption(e.Args);

        // 正常启动：显式创建主窗口（App.xaml 里没有 StartupUri，见那里的注释）。
        // 这样无界面入口（self-test / render-smoke / file-smoke）**完全不会碰 UI**，
        // 既不会闪窗口，也能在没有桌面会话的机器上跑。
        var window = new MainWindow();

        // 便于验收时检查"窄屏/不同分辨率下布局是否错位"：--size 1000x700
        if (ParseSize(e.Args) is { } size)
        {
            window.Width = size.Width;
            window.Height = size.Height;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 20;
            window.Top = 20;
        }

        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// <c>--smooth</c>：切回旧的"整条曲线拟合"笔迹（更圆润，但已经画过的部分会随新采样点轻微移动）。
    /// 默认是**不平滑**——"我画在哪里就是哪里"。加这个开关是为了能同机对比两种手感。
    /// </summary>
    private static void ApplySmoothingOption(string[] args)
    {
        var smooth = args.Any(a => a.Equals("--smooth", StringComparison.OrdinalIgnoreCase));
        SmoothGeometryBuilder.Smoothing = smooth
            ? FreehandSmoothing.FitToCurve
            : FreehandSmoothing.None;
    }

    private static (double Width, double Height)? ParseSize(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals("--size", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = args[i + 1].Split('x', 'X');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], out var w) && double.TryParse(parts[1], out var h) &&
                w >= 400 && h >= 300)
                return (w, h);
        }
        return null;
    }
}
