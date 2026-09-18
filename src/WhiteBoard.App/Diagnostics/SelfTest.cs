using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WhiteBoard.Core.Storage;
using WhiteBoard.Core.Text;
using WhiteBoard.Core.Theme;

namespace WhiteBoard.App.Diagnostics;

/// <summary>
/// 免环境自检（M0 交付物之一，S3 的插件自检会复用它）。
///
/// 用法：
///   WhiteBoard.exe --self-test [--report &lt;path&gt;] [--all-plugins]
/// 退出码：0 = 全部通过；非 0 = 有失败项。
///
/// 目的：让"程序在干净环境里能否零拷贝跑起来"变成一条命令 + 一个退出码，
/// 从而可以放进 CI 与 Windows 沙箱/全新虚拟机里自动判定。
/// </summary>
public static class SelfTest
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    /// <summary>
    /// 本程序是 GUI 子系统（WinExe），从命令行启动时没有控制台，
    /// <c>Console.WriteLine</c> 会写进虚空；同时 PowerShell 用 <c>&amp;</c> 调用 WinExe 不会等待、
    /// 也拿不到退出码。这里挂到父进程控制台，让 <c>--self-test</c> 的输出与退出码都能被脚本捕获。
    /// （CI/脚本侧仍建议用 <c>Start-Process -Wait -PassThru</c> 以确保等待。）
    /// </summary>
    internal static void TryAttachParentConsole()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess)) return;
            var stdout = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(stdout);
            var stderr = new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true };
            Console.SetError(stderr);
        }
        catch (Exception)
        {
            // 挂不上就算了，报告文件仍然会写
        }
    }

    public static bool IsRequested(string[] args)
        => args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        TryAttachParentConsole();

        var reportPath = GetArgValue(args, "--report");
        var lines = new List<string>();
        var failures = 0;

        void Check(string name, Func<(bool ok, string detail)> probe)
        {
            try
            {
                var (ok, detail) = probe();
                lines.Add($"{(ok ? "PASS" : "FAIL")}\t{name}\t{detail}");
                if (!ok) failures++;
            }
            catch (Exception ex)
            {
                lines.Add($"FAIL\t{name}\t异常：{ex.GetType().Name}: {ex.Message}");
                failures++;
            }
        }

        lines.Add($"WhiteBoard 自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        lines.Add($"进程位数：{(Environment.Is64BitProcess ? "x64" : "x86")}");
        lines.Add($"运行时：{Environment.Version}");
        lines.Add($"exe 目录：{AppContext.BaseDirectory}");
        lines.Add("");

        // 1) 数据目录：必须在 exe 同级目录内，且可写（C3）
        Check("数据目录位置", () =>
        {
            var paths = PathService.Resolve();
            var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
            var inside = paths.DataRoot.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase);
            return (inside, paths.DataRoot);
        });

        Check("数据目录可写", () =>
        {
            var paths = PathService.Resolve();
            if (!paths.IsWritable)
                return (false, $"不可写（预期在可写位置运行）：{paths.DataRoot}");
            return (true, "可写");
        });

        // 2) 编码往返：UTF-8 带 BOM 写入，读取回来后中文必须一致
        Check("文本编码往返（UTF-8）", () =>
        {
            const string sample = "白板编码测试：黑板绿 / 白笔 / 黄笔 / 红笔 / 蓝笔";
            var bytes = TextFileCodec.Encode(sample);
            var decoded = TextFileCodec.Decode(bytes);
            return (decoded == sample, decoded == sample ? "往返一致" : $"不一致：{decoded}");
        });

        // 3) 编码回退：模拟用户用记事本存成 ANSI(GBK) 的文件，必须仍能正确读出
        Check("文本编码回退（ANSI/GBK）", () =>
        {
            const string sample = "另存为 ANSI 后仍应可读：白板主题";
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            byte[] gbk;
            try { gbk = Encoding.GetEncoding(936).GetBytes(sample); }
            catch (Exception) { return (true, "本机无 936 代码页，跳过"); }

            var decoded = TextFileCodec.Decode(gbk);
            return (decoded == sample, decoded == sample ? "GBK 回退正确" : $"回退失败：{decoded}");
        });

        // 4) 路径穿越防护
        Check("路径穿越防护", () =>
        {
            var paths = PathService.Resolve();
            try
            {
                paths.ResolveUnderDataRoot(@"..\..\escape.json");
                return (false, "未能拒绝 ..\\..\\escape.json");
            }
            catch (UnauthorizedAccessException)
            {
                return (true, "已拒绝越界路径");
            }
        });

        // 5) 主题色：内置默认可解析、色值合法、对比度达标
        Check("主题色 · 内置默认可加载", () =>
        {
            var paths = PathService.Resolve();
            var r = ThemeService.Load(paths);
            var n = r.Preset.Colors.Count;
            return (n > 0, $"预设「{r.Preset.Name}」{n} 色，来源={r.Source}");
        });

        Check("主题色 · 与背景的对比度 ≥3:1", () =>
        {
            var paths = PathService.Resolve();
            var preset = ThemeService.Load(paths).Preset;
            var bad = preset.Colors
                .Select(c => (c.Name, Ratio: ColorMath.ContrastRatio(c.Value, preset.Background)))
                .Where(x => x.Ratio < 3.0)
                .ToList();
            return (bad.Count == 0,
                bad.Count == 0
                    ? "全部达标"
                    : "低于下限：" + string.Join("、", bad.Select(x => $"{x.Name} {x.Ratio:0.0}:1")));
        });

        Check("主题色 · 坏文件回退内置默认", () =>
        {
            try
            {
                ThemeService.Parse("{ 这不是合法 JSON ");
                return (false, "坏 JSON 未被拒绝");
            }
            catch (Exception)
            {
                return (true, "已正确拒绝并会回退默认");
            }
        });

        // 6) 发布目录中不得出现第三方原生库（I1）—— 由 WbPeScan 做完整审计，这里只做快速抽查
        Check("无第三方原生库（快速抽查）", () =>
        {
            var known = new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" };
            var found = known
                .Where(k => File.Exists(Path.Combine(AppContext.BaseDirectory, k)))
                .ToArray();
            return (found.Length == 0, found.Length == 0 ? "未发现 VC++ 运行时" : $"发现：{string.Join(", ", found)}");
        });

        lines.Add("");
        lines.Add($"结果：{(failures == 0 ? "全部通过" : $"{failures} 项失败")}");

        var text = string.Join(Environment.NewLine, lines);
        Console.WriteLine(text);
        Debug.WriteLine(text);

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            try { TextFileCodec.WriteAllText(Path.GetFullPath(reportPath), text); }
            catch (Exception ex) { Console.Error.WriteLine($"写报告失败：{ex.Message}"); }
        }

        Environment.ExitCode = failures == 0 ? 0 : 1;
        return Environment.ExitCode;
    }

    private static string? GetArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
