using System.Reflection.PortableExecutable;

namespace WbPeScan;

/// <summary>单个导入 DLL 的判定结果。</summary>
public enum ImportVerdict
{
    /// <summary>Windows 系统组件，允许。</summary>
    SystemAllowed,

    /// <summary>UCRT（Windows 自带），允许。</summary>
    UcrtAllowed,

    /// <summary>
    /// VC++ 运行时 DLL 被导入，但**未随包携带** —— 目标机必须装 VC++ 可再发行组件，<b>拒绝</b>。
    /// </summary>
    /// <remarks>
    /// M0 实测修正：原规则是"导入 msvcp*/vcruntime* 一律拒绝"，**过严**。
    /// 因为"本地部署（app-local）"同样是微软支持的 VC++ 运行时分发方式——
    /// 把 <c>msvcp140.dll</c> / <c>vcruntime140.dll</c> 放在程序（或插件）自己目录下，
    /// 目标机无需安装可再发行组件即可运行。
    /// 因此正确判据是"**依赖是否可解析**"，而不是"是否出现了某类 DLL 名字"。
    /// </remarks>
    VcRuntimeNotBundled,

    /// <summary>非系统 DLL，必须随包携带（在扫描目录内找到），否则拒绝。</summary>
    ThirdParty,

    /// <summary>白名单外的 DLL，但本机 System32 中存在 —— 属操作系统组件，允许（单独列出以便审计）。</summary>
    SystemPresent,

    /// <summary>非系统 DLL 且未随包携带 —— <b>拒绝</b>。</summary>
    Missing
}

public sealed record ImportEntry(string DllName, ImportVerdict Verdict, string? ResolvedPath, bool IsDelayLoad);

public sealed record PeFileReport(string Path, bool IsPe, string? Machine, IReadOnlyList<ImportEntry> Imports, string? Error);

/// <summary>
/// PE 导入表扫描器（M0 · PoC-D）。
///
/// 目的：把"这个插件/程序在没装 VC++ 可再发行组件的机器上能不能跑"从人工判断变成机器判定。
///
/// 判定规则（白板开发总路线 §4.5 第 5 项）：
///   msvcp*.dll / vcruntime*.dll / concrt*.dll / vccorlib*.dll / api-ms-win-cpp-*  → 允许，但**必须随包携带**
///   ucrtbase.dll / api-ms-win-crt-*.dll / msvcrt.dll                              → 允许（Windows 自带）
///   Windows 系统 DLL 白名单 / 本机 System32 中存在的 DLL                          → 允许
///   其它任何 DLL                                                                   → 仅当随包携带才允许
///
/// 核心判据是「**依赖是否可解析**」，而不是"是否出现某类 DLL 名字"：
/// VC++ 运行时既可由目标机安装可再发行组件提供，也可由程序自己目录（app-local）提供，
/// 后者是微软支持的分发方式，因此在无 VC++ 可再发行组件的机器上同样可行。
///
/// 同时解析普通导入表（数据目录 1）与延迟加载导入表（数据目录 13）。
/// 局限：静态扫描看不到 LoadLibrary / NativeLibrary.Load 的运行时动态加载，
///       因此仍需配合"禁用 API 扫描 + 签名白名单"使用。
/// </summary>
public static class PeImportScanner
{
    private static readonly string[] VcRuntimePrefixes =
        ["msvcp", "vcruntime", "concrt", "vccorlib", "api-ms-win-cpp-"];

    private static readonly HashSet<string> UcrtNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ucrtbase.dll", "msvcrt.dll", "ucrtbased.dll"
    };

    private static readonly HashSet<string> SystemWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        // 核心
        "ntdll.dll", "kernel32.dll", "kernelbase.dll", "user32.dll", "gdi32.dll", "gdi32full.dll",
        "advapi32.dll", "shell32.dll", "shlwapi.dll", "ole32.dll", "oleaut32.dll", "combase.dll",
        "rpcrt4.dll", "sechost.dll", "bcrypt.dll", "crypt32.dll", "ncrypt.dll", "wintrust.dll",
        "imm32.dll", "msctf.dll", "userenv.dll", "version.dll", "psapi.dll", "dbghelp.dll",
        "winmm.dll", "ws2_32.dll", "mswsock.dll", "iphlpapi.dll", "dnsapi.dll", "winhttp.dll",
        "setupapi.dll", "cfgmgr32.dll", "powrprof.dll", "propsys.dll", "uxtheme.dll", "dwmapi.dll",
        "comctl32.dll", "comdlg32.dll", "winspool.drv", "winspool.dll", "mpr.dll", "netapi32.dll",
        "shcore.dll", "wtsapi32.dll", "d3d11.dll", "dxgi.dll", "d3d9.dll", "d2d1.dll", "dwrite.dll",
        "windowscodecs.dll", "dcomp.dll", "opengl32.dll", "gdiplus.dll", "msimg32.dll",
        "d3dcompiler_47.dll", "d3dcompiler_47_cor3.dll", "api-ms-win-core-path-l1-1-0.dll",
        "图像" // 占位，避免空集告警（不会被匹配）
    };

    private static bool IsSystemAllowed(string dll)
    {
        if (SystemWhitelist.Contains(dll)) return true;
        // api-ms-win-* 除 api-ms-win-cpp-* 外都是 Windows API Set
        return dll.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase)
               && !dll.StartsWith("api-ms-win-cpp-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUcrt(string dll)
        => UcrtNames.Contains(dll) || dll.StartsWith("api-ms-win-crt-", StringComparison.OrdinalIgnoreCase);

    private static bool IsVcRuntime(string dll)
        => VcRuntimePrefixes.Any(p => dll.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 本机 System32 中的 DLL 名单（缓存）。
    /// 手工白名单不可能穷尽 Windows 的系统组件（例如 <c>mscoree.dll</c>），
    /// 因此以"本机 System32 中确实存在"作为兜底的系统组件判据。
    /// 注意：这让判定依赖扫描机的系统，恰好符合我们要回答的问题
    /// "在这样一台机器上能不能跑"。
    /// </summary>
    private static readonly Lazy<HashSet<string>> System32Files = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dir = Environment.SystemDirectory; // C:\Windows\System32
            foreach (var f in Directory.EnumerateFiles(dir, "*.dll"))
                set.Add(Path.GetFileName(f));
        }
        catch (Exception)
        {
            // 无法枚举时保持空集，退回纯白名单判定
        }
        return set;
    });

    /// <summary>扫描目录下所有 PE 文件。</summary>
    public static IReadOnlyList<PeFileReport> ScanDirectory(string root, bool recursive = true)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(root, "*", option)
            .Where(f =>
            {
                var ext = Path.GetExtension(f);
                return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 目录内已存在的文件名，用于判定"是否随包携带"
        var present = new HashSet<string>(
            Directory.EnumerateFiles(root, "*", option).Select(Path.GetFileName).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        return files.Select(f => ScanFile(f, present)).ToList();
    }

    /// <summary>扫描单个 PE 文件。</summary>
    public static PeFileReport ScanFile(string path, ISet<string>? presentFiles = null)
    {
        var present = presentFiles ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileName(path)
        };

        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);

            var headers = pe.PEHeaders;
            if (headers.PEHeader is null)
                return new PeFileReport(path, false, null, [], "不是有效的 PE 文件");

            var machine = headers.CoffHeader.Machine.ToString();
            var imports = new List<ImportEntry>();

            foreach (var dll in ReadImports(fs, headers, delayLoad: false))
                imports.Add(Classify(dll, present, delayLoad: false, path));

            foreach (var dll in ReadImports(fs, headers, delayLoad: true))
                imports.Add(Classify(dll, present, delayLoad: true, path));

            var distinct = imports
                .GroupBy(i => (i.DllName, i.IsDelayLoad))
                .Select(g => g.First())
                .OrderBy(i => i.DllName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new PeFileReport(path, true, machine, distinct, null);
        }
        catch (Exception ex)
        {
            return new PeFileReport(path, false, null, [], $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ImportEntry Classify(string dll, ISet<string> present, bool delayLoad, string ownerPath)
    {
        // VC++ 运行时：随包携带（app-local）即可，否则目标机需要可再发行组件
        if (IsVcRuntime(dll))
        {
            return present.Contains(dll)
                ? new ImportEntry(dll, ImportVerdict.ThirdParty, dll, delayLoad)
                : new ImportEntry(dll, ImportVerdict.VcRuntimeNotBundled, null, delayLoad);
        }

        if (IsUcrt(dll))
            return new ImportEntry(dll, ImportVerdict.UcrtAllowed, null, delayLoad);

        if (IsSystemAllowed(dll))
            return new ImportEntry(dll, ImportVerdict.SystemAllowed, null, delayLoad);

        // 非白名单 DLL：先看是否随包携带
        if (present.Contains(dll))
            return new ImportEntry(dll, ImportVerdict.ThirdParty, dll, delayLoad);

        // 再看是否为本机 System32 中的系统组件
        if (System32Files.Value.Contains(dll))
            return new ImportEntry(dll, ImportVerdict.SystemPresent, null, delayLoad);

        return new ImportEntry(dll, ImportVerdict.Missing, null, delayLoad);
    }

    // ---- PE 解析：RVA → 文件偏移，再读导入描述符 ----

    private static IEnumerable<string> ReadImports(FileStream fs, PEHeaders headers, bool delayLoad)
    {
        var peHeader = headers.PEHeader;
        if (peHeader is null) yield break;

        // IMAGE_DIRECTORY_ENTRY_IMPORT = 1，IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT = 13
        var dir = delayLoad ? peHeader.DelayImportTableDirectory : peHeader.ImportTableDirectory;
        if (dir.RelativeVirtualAddress == 0 || dir.Size == 0) yield break;

        var descriptorSize = delayLoad ? 32 : 20;
        var nameFieldOffset = delayLoad ? 4 : 12;   // DelayLoad: DllNameRVA 在偏移 4；Import: Name 在偏移 12
        var count = dir.Size / descriptorSize;

        for (var i = 0; i < count; i++)
        {
            var descRva = dir.RelativeVirtualAddress + i * descriptorSize;
            var descOffset = RvaToOffset(headers, descRva);
            if (descOffset < 0) yield break;

            var desc = ReadBytes(fs, descOffset, descriptorSize);
            if (desc.Length < descriptorSize) yield break;

            // 全零描述符表示结束
            if (desc.All(b => b == 0)) yield break;

            var nameRva = BitConverter.ToUInt32(desc, nameFieldOffset);
            if (nameRva == 0) continue;

            var nameOffset = RvaToOffset(headers, (int)nameRva);
            if (nameOffset < 0) continue;

            var name = ReadAsciiZ(fs, nameOffset);
            if (!string.IsNullOrWhiteSpace(name))
                yield return name.Trim();
        }
    }

    private static int RvaToOffset(PEHeaders headers, int rva)
    {
        foreach (var s in headers.SectionHeaders)
        {
            var size = Math.Max(s.VirtualSize, s.SizeOfRawData);
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + size)
                return s.PointerToRawData + (rva - s.VirtualAddress);
        }
        return -1;
    }

    private static byte[] ReadBytes(FileStream fs, long offset, int count)
    {
        if (offset < 0 || offset >= fs.Length) return [];
        var n = (int)Math.Min(count, fs.Length - offset);
        var buf = new byte[n];
        fs.Position = offset;
        var read = 0;
        while (read < n)
        {
            var r = fs.Read(buf, read, n - read);
            if (r <= 0) break;
            read += r;
        }
        return read == n ? buf : buf[..read];
    }

    private static string ReadAsciiZ(FileStream fs, long offset, int max = 260)
    {
        var buf = ReadBytes(fs, offset, max);
        var end = Array.IndexOf(buf, (byte)0);
        if (end < 0) end = buf.Length;
        return System.Text.Encoding.ASCII.GetString(buf, 0, end);
    }
}
