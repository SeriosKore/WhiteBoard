using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using WhiteBoard.Core.Storage;
using WhiteBoard.Core.Text;

namespace WhiteBoard.Poc.InkLatency;

public enum PointerKind { Mouse, Touch, Stylus }

public readonly record struct LatencySample(
    int Index,
    string Mode,
    double LatencyMs,
    int PointsInBatch,
    double Pressure,
    PointerKind Kind,
    double ElapsedSec);

/// <summary>
/// 笔迹延迟探针（M0 · PoC-A 的核心仪表）。
///
/// 口径（依 M0 决策 D1/D2）：
///   **应用内延迟 = 输入事件抵达（Stopwatch） → 含该笔迹的帧开始渲染（CompositionTarget.Rendering 回调进入时刻）**
///
/// 它**不包含**合成提交与面板扫描输出，因此绝对数值偏乐观——这一点在文档里已声明。
/// 真正关键的是**相对指标**：本程序在同一台机器上用同一套探针分别测「裸 InkCanvas 基线」与
/// 「我们的分层实现」，两者的差值说明"分层是否引入了额外延迟"。
/// </summary>
public sealed class LatencyProbe
{
    private readonly List<LatencySample> _samples = [];
    private long _pendingInputTicks = -1;
    private int _pendingPoints;
    private double _pendingPressure;
    private PointerKind _pendingKind;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public string Mode { get; set; } = "unknown";

    /// <summary>输入事件抵达时调用。同一帧内的多个输入点合并为一批。</summary>
    public void NoteInput(PointerKind kind, double pressure)
    {
        if (_pendingInputTicks < 0)
        {
            _pendingInputTicks = Stopwatch.GetTimestamp();
            _pendingKind = kind;
        }
        _pendingPoints++;
        _pendingPressure = Math.Max(_pendingPressure, pressure);
    }

    /// <summary>每帧开始时调用（挂在 CompositionTarget.Rendering 上）。</summary>
    public void NoteFrame()
    {
        if (_pendingInputTicks < 0) return;

        var now = Stopwatch.GetTimestamp();
        var ms = (now - _pendingInputTicks) * 1000.0 / Stopwatch.Frequency;

        _samples.Add(new LatencySample(
            _samples.Count + 1,
            Mode,
            Math.Round(ms, 3),
            _pendingPoints,
            Math.Round(_pendingPressure, 3),
            _pendingKind,
            Math.Round(_clock.Elapsed.TotalSeconds, 3)));

        _pendingInputTicks = -1;
        _pendingPoints = 0;
        _pendingPressure = 0;
    }

    public IReadOnlyList<LatencySample> Samples => _samples;

    public bool HasPending => _pendingInputTicks >= 0;

    public void Reset() => _samples.Clear();

    public ProbeStats Stats() => ProbeStats.From(_samples);

    /// <summary>把样本写 CSV。默认写到 &lt;exe同级&gt;\data\latency\（符合 C3：程序数据不出程序文件夹）。</summary>
    public string ExportCsv(string? path = null)
    {
        var paths = PathService.Resolve();
        var dir = Path.Combine(paths.DataRoot, "latency");
        Directory.CreateDirectory(dir);

        path ??= Path.Combine(dir,
            $"{Mode}-{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("# WhiteBoard M0 · PoC-A 笔迹延迟样本");
        sb.AppendLine($"# 模式={Mode}");
        sb.AppendLine($"# 口径=输入事件抵达 → 含该笔迹的帧开始渲染（不含合成提交与显示）");
        sb.AppendLine($"# 时间={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"# 机器={Environment.MachineName}  进程位数={(Environment.Is64BitProcess ? "x64" : "x86")}");
        sb.AppendLine("index,mode,latency_ms,points_in_batch,pressure,kind,elapsed_sec");
        foreach (var s in _samples)
        {
            sb.Append(s.Index).Append(',')
              .Append(s.Mode).Append(',')
              .Append(s.LatencyMs.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(s.PointsInBatch).Append(',')
              .Append(s.Pressure.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(s.Kind).Append(',')
              .Append(s.ElapsedSec.ToString("0.###", CultureInfo.InvariantCulture))
              .AppendLine();
        }

        var stats = Stats();
        sb.AppendLine();
        sb.AppendLine($"# 样本数={stats.Count}");
        sb.AppendLine($"# 平均={stats.Avg:0.###} ms  中位={stats.P50:0.###} ms  P95={stats.P95:0.###} ms  最大={stats.Max:0.###} ms  最小={stats.Min:0.###} ms");

        TextFileCodec.WriteAllText(path, sb.ToString());
        return path;
    }
}

public readonly record struct ProbeStats(
    int Count, double Avg, double P50, double P95, double Min, double Max)
{
    public static ProbeStats From(IReadOnlyList<LatencySample> samples)
    {
        if (samples.Count == 0) return new ProbeStats(0, 0, 0, 0, 0, 0);

        var values = samples.Select(s => s.LatencyMs).OrderBy(v => v).ToArray();
        double Pct(double p)
        {
            if (values.Length == 1) return values[0];
            var idx = (values.Length - 1) * p;
            var lo = (int)Math.Floor(idx);
            var hi = (int)Math.Ceiling(idx);
            return values[lo] + (values[hi] - values[lo]) * (idx - lo);
        }

        return new ProbeStats(
            values.Length,
            Math.Round(values.Average(), 2),
            Math.Round(Pct(0.50), 2),
            Math.Round(Pct(0.95), 2),
            Math.Round(values[0], 2),
            Math.Round(values[^1], 2));
    }

    public override string ToString()
        => Count == 0
            ? "尚无样本"
            : $"样本 {Count}　平均 {Avg:0.0} ms　中位 {P50:0.0} ms　P95 {P95:0.0} ms　最大 {Max:0.0} ms";
}
