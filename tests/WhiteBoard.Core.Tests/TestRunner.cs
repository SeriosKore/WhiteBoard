using System.Diagnostics;
using System.Reflection;

namespace WhiteBoard.Core.Tests;

/// <summary>
/// 极简测试运行器（零第三方依赖）。
///
/// 为什么不用 xunit：本项目 I1 要求"零第三方原生/托管依赖"，且要在离线环境可跑；
/// 自研运行器只有几十行，输出/退出码足够 CI 使用。
/// 约定：测试类中的 <c>public static void</c> 方法名以 <c>Test_</c> 开头即为用例。
/// </summary>
public static class TestRunner
{
    public static int Main(string[] args)
    {
        var filter = args.FirstOrDefault(a => !a.StartsWith('-'));
        var listOnly = args.Contains("--list");

        var types = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.IsClass && t.IsAbstract && t.IsSealed) // 静态类
            .Where(t => t.Namespace?.EndsWith(".Tests", StringComparison.Ordinal) == true)
            .OrderBy(t => t.Name)
            .ToList();

        var passed = 0;
        var failed = 0;
        var failures = new List<string>();

        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine(" WhiteBoard.Core 单元测试");
        Console.WriteLine($" 程序集：{Assembly.GetExecutingAssembly().GetName().Name}　运行时：{Environment.Version}");
        Console.WriteLine("════════════════════════════════════════════════════════════");

        foreach (var type in types)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.StartsWith("Test_", StringComparison.Ordinal))
                .Where(m => m.GetParameters().Length == 0)
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToList();

            if (methods.Count == 0) continue;
            if (filter is not null && !type.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            Console.WriteLine();
            Console.WriteLine($"── {type.Name} ──");

            foreach (var m in methods)
            {
                if (listOnly)
                {
                    Console.WriteLine($"   · {m.Name}");
                    continue;
                }

                var sw = Stopwatch.StartNew();
                try
                {
                    m.Invoke(null, null);
                    sw.Stop();
                    passed++;
                    Console.WriteLine($"   PASS  {m.Name}  ({sw.Elapsed.TotalMilliseconds:0.0} ms)");
                }
                catch (TargetInvocationException tie)
                {
                    sw.Stop();
                    failed++;
                    var inner = tie.InnerException ?? tie;
                    var msg = $"{type.Name}.{m.Name}: {inner.GetType().Name}: {inner.Message}";
                    failures.Add(msg);
                    Console.WriteLine($"   FAIL  {m.Name}");
                    Console.WriteLine($"         {inner.Message}");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    failed++;
                    failures.Add($"{type.Name}.{m.Name}: {ex.Message}");
                    Console.WriteLine($"   FAIL  {m.Name}  ({ex.GetType().Name})");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════");
        if (failures.Count > 0)
        {
            Console.WriteLine(" 失败明细：");
            foreach (var f in failures) Console.WriteLine("   · " + f);
            Console.WriteLine();
        }
        Console.WriteLine($" 通过 {passed}　失败 {failed}　总计 {passed + failed}");
        Console.WriteLine("════════════════════════════════════════════════════════════");

        return failed == 0 ? 0 : 1;
    }
}

/// <summary>断言helper。</summary>
public static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{message}　期望={expected}　实际={actual}");
    }

    public static void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new Exception($"{message}　期望={expected:0.######}　实际={actual:0.######}　容差={tolerance}");
    }

    public static TException Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new Exception($"{message}　期望 {typeof(TException).Name}，实际 {ex.GetType().Name}");
        }
        throw new Exception($"{message}　期望抛出 {typeof(TException).Name}，但没有异常");
    }
}
