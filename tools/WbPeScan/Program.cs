using System.Text;
using System.Text.Json;
using WbPeScan;

// wbpescan <目录或文件> [--json <报告路径>] [--quiet]
// 退出码：0 = 未发现 VC++ 运行时依赖且所有非系统 DLL 均随包携带；1 = 存在问题；2 = 用法错误

if (args.Length == 0)
{
    Console.Error.WriteLine("用法: wbpescan <目录或文件> [--json <报告路径>] [--quiet]");
    return 2;
}

var target = args[0];
var jsonPath = GetValue(args, "--json");
var quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));

if (!File.Exists(target) && !Directory.Exists(target))
{
    Console.Error.WriteLine($"路径不存在: {target}");
    return 2;
}

Console.OutputEncoding = Encoding.UTF8;

List<PeFileReport> reports;
string scannedRoot;

if (Directory.Exists(target))
{
    scannedRoot = Path.GetFullPath(target);
    reports = PeImportScanner.ScanDirectory(scannedRoot).ToList();
}
else
{
    scannedRoot = Path.GetDirectoryName(Path.GetFullPath(target))!;
    var present = new HashSet<string>(
        Directory.EnumerateFiles(scannedRoot, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).OfType<string>(),
        StringComparer.OrdinalIgnoreCase);
    reports = [PeImportScanner.ScanFile(target, present)];
}

var vcHits = new List<(string File, ImportEntry Entry)>();
var missingHits = new List<(string File, ImportEntry Entry)>();
var thirdPartyHits = new List<(string File, ImportEntry Entry)>();
var systemPresentHits = new List<(string File, ImportEntry Entry)>();
var ucrtHits = new List<(string File, ImportEntry Entry)>();
var notPe = new List<PeFileReport>();

foreach (var r in reports)
{
    if (!r.IsPe) { notPe.Add(r); continue; }

    foreach (var imp in r.Imports)
    {
        var name = Path.GetFileName(r.Path);
        switch (imp.Verdict)
        {
            case ImportVerdict.VcRuntimeNotBundled:
                vcHits.Add((name, imp));
                break;
            case ImportVerdict.Missing:
                missingHits.Add((name, imp));
                break;
            case ImportVerdict.ThirdParty:
                thirdPartyHits.Add((name, imp));
                break;
            case ImportVerdict.SystemPresent:
                systemPresentHits.Add((name, imp));
                break;
            case ImportVerdict.UcrtAllowed:
                ucrtHits.Add((name, imp));
                break;
        }
    }
}

var sb = new StringBuilder();
sb.AppendLine("════════════════════════════════════════════════════════════════");
sb.AppendLine(" PE 导入表扫描报告（M0 · PoC-D）");
sb.AppendLine("════════════════════════════════════════════════════════════════");
sb.AppendLine($"扫描根目录 : {scannedRoot}");
sb.AppendLine($"扫描时间   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
sb.AppendLine($"PE 文件数  : {reports.Count(r => r.IsPe)}（另有 {notPe.Count} 个非 PE 文件被跳过）");
sb.AppendLine();

sb.AppendLine($"【1】VC++ 运行时依赖但未随包携带 : {(vcHits.Count == 0 ? "无 ✅" : $"{vcHits.Count} 处 ❌")}");
if (vcHits.Count > 0)
{
    sb.AppendLine("      说明：这些 DLL 既未随包携带，目标机也没有 → 需随包附带（app-local 部署）或安装 VC++ 可再发行组件");
    foreach (var (file, e) in vcHits.Take(40))
        sb.AppendLine($"      {file}  →  {e.DllName}{(e.IsDelayLoad ? "（延迟加载）" : "")}");
    if (vcHits.Count > 40) sb.AppendLine($"      …其余 {vcHits.Count - 40} 处省略");
}
sb.AppendLine();

sb.AppendLine($"【2】未随包携带的非系统 DLL : {(missingHits.Count == 0 ? "无 ✅" : $"{missingHits.Count} 处 ❌")}");
if (missingHits.Count > 0)
{
    foreach (var (file, e) in missingHits.Take(40))
        sb.AppendLine($"      {file}  →  {e.DllName}{(e.IsDelayLoad ? "（延迟加载）" : "")}");
    if (missingHits.Count > 40) sb.AppendLine($"      …其余 {missingHits.Count - 40} 处省略");
}
sb.AppendLine();

sb.AppendLine($"【3】随包携带的非系统 DLL : {thirdPartyHits.Count} 处");
if (!quiet)
{
    foreach (var (file, e) in thirdPartyHits.Take(40))
        sb.AppendLine($"      {file}  →  {e.DllName}");
    if (thirdPartyHits.Count > 40) sb.AppendLine($"      …其余 {thirdPartyHits.Count - 40} 处省略");
}
sb.AppendLine();

var sysPresentDistinct = systemPresentHits.Select(h => h.Entry.DllName)
    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
sb.AppendLine($"【3b】本机 System32 中的系统组件 : {systemPresentHits.Count} 处 / {sysPresentDistinct.Count} 个不同 DLL（允许）");
if (sysPresentDistinct.Count > 0)
    sb.AppendLine($"      {string.Join(", ", sysPresentDistinct.Take(30))}{(sysPresentDistinct.Count > 30 ? " …" : "")}");
sb.AppendLine();

var ucrtDistinct = ucrtHits.Select(h => h.Entry.DllName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
sb.AppendLine($"【3c】UCRT / msvcrt 导入 : {ucrtHits.Count} 处 / {ucrtDistinct} 个不同 DLL（Windows 自带，允许）");
sb.AppendLine();

// 各文件的导入清单（便于人工核对）
if (!quiet)
{
    sb.AppendLine("【4】逐文件导入清单");
    foreach (var r in reports.Where(r => r.IsPe).OrderBy(r => Path.GetFileName(r.Path)))
    {
        var name = Path.GetFileName(r.Path);
        var imports = r.Imports.Count == 0 ? "（无导入表）" : string.Join(", ", r.Imports.Select(i => i.DllName));
        sb.AppendLine($"      {name} [{r.Machine}] : {imports}");
    }
    sb.AppendLine();
}

var pass = vcHits.Count == 0 && missingHits.Count == 0;
sb.AppendLine("════════════════════════════════════════════════════════════════");
sb.AppendLine(pass
    ? "结论：未发现 VC++ 运行时依赖，且所有非系统 DLL 均已随包携带 ✅"
    : "结论：存在依赖风险，目标机（未装 VC++ 可再发行组件）上可能无法启动 ❌");
sb.AppendLine("════════════════════════════════════════════════════════════════");

Console.WriteLine(sb.ToString());

if (!string.IsNullOrWhiteSpace(jsonPath))
{
    var payload = new
    {
        scannedRoot,
        scannedAt = DateTime.Now.ToString("s"),
        peFileCount = reports.Count(r => r.IsPe),
        notPeCount = notPe.Count,
        passed = pass,
        vcRuntimeNotBundled = vcHits.Select(h => new { file = h.File, dll = h.Entry.DllName, delayLoad = h.Entry.IsDelayLoad }),
        missingDependencies = missingHits.Select(h => new { file = h.File, dll = h.Entry.DllName, delayLoad = h.Entry.IsDelayLoad }),
        bundledThirdParty = thirdPartyHits.Select(h => new { file = h.File, dll = h.Entry.DllName }),
        system32Present = systemPresentHits.Select(h => new { file = h.File, dll = h.Entry.DllName }),
        files = reports.Where(r => r.IsPe).Select(r => new
        {
            file = Path.GetFileName(r.Path),
            machine = r.Machine,
            imports = r.Imports.Select(i => new { dll = i.DllName, verdict = i.Verdict.ToString(), delayLoad = i.IsDelayLoad })
        })
    };
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"JSON 报告已写入：{jsonPath}");
}

return pass ? 0 : 1;

static string? GetValue(string[] a, string name)
{
    for (var i = 0; i < a.Length - 1; i++)
        if (a[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
    return null;
}
