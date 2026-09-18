using System.Windows;

namespace WhiteBoard.Poc.InkLatency;

public partial class App : Application
{
    /// <summary>初始渲染路线：baseline / a1 / a2</summary>
    public static string InitialMode { get; private set; } = "a1";

    /// <summary>可选：导出 CSV 的路径（默认写到 &lt;exe同级&gt;\data\latency\）</summary>
    public static string? CsvPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        for (var i = 0; i < e.Args.Length; i++)
        {
            var a = e.Args[i];
            if (a.Equals("--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
                InitialMode = e.Args[++i].ToLowerInvariant();
            else if (a.Equals("--csv", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
                CsvPath = e.Args[++i];
            else if (a.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase))
                InitialMode = a["--mode=".Length..].ToLowerInvariant();
            else if (a.StartsWith("--csv=", StringComparison.OrdinalIgnoreCase))
                CsvPath = a["--csv=".Length..];
        }

        base.OnStartup(e);

        // 自动化冒烟自检：离屏跑三条路线，不开窗口
        if (SelfCheck.IsRequested(e.Args))
        {
            Shutdown(SelfCheck.Run(e.Args));
            return;
        }

        new MainWindow().Show();
    }
}
