using System.IO.Compression;
using System.Text.Json;
using WhiteBoard.Core.Model;

namespace WhiteBoard.Core.Storage;

/// <summary>画板文件读取失败。<see cref="Message"/> 是**给用户看的中文说明**，不是堆栈。</summary>
public sealed class WbLoadException : Exception
{
    public WbLoadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>一次加载的结果：文档 + 非致命警告（未知图元、页码重复等）。</summary>
public sealed record WbLoadResult(WhiteboardDocument Document, IReadOnlyList<string> Warnings)
{
    public bool HasWarnings => Warnings.Count > 0;
    public string WarningText => string.Join("；", Warnings);
}

/// <summary>包内条目名（常量集中在这里，避免读写两侧写错字）。</summary>
public static class WbEntries
{
    public const string Format = "format.json";
    public const string Document = "document.json";
    public const string Preview = "preview.png";
}

/// <summary>
/// <c>.wb</c> 画板包（ZIP）的读写。
///
/// 三条来自需求与约束的硬要求：
/// <list type="number">
/// <item><b>原子写</b>（C3 的可靠性面）：先写同目录临时文件，再 <c>File.Replace</c> 原子替换。
///       断电/崩溃/磁盘满 只会留下临时文件，**绝不会把老文件写坏**；</item>
/// <item><b>不写注册表、不写程序目录外</b>：本类只操作调用方给的路径；
///       调用方（用户显式保存除外）保证路径在 exe 同级目录内；</item>
/// <item><b>损坏可解释</b>：不是 ZIP、缺条目、JSON 坏了、版本太高 —— 都给出中文原因，
///       而不是抛一个 <c>InvalidDataException</c> 让 UI 显示英文堆栈。</item>
/// </list>
/// </summary>
public static class WbPackage
{
    /// <summary>扩展名（含点，小写）。</summary>
    public const string Extension = ".wb";

    /// <summary>确保路径带 <c>.wb</c> 后缀。</summary>
    public static string EnsureExtension(string path)
        => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? path : path + Extension;

    /// <summary>
    /// 保存（原子写）。
    /// </summary>
    /// <param name="document">要保存的文档。</param>
    /// <param name="path">目标路径（用户显式保存时可位于任意位置）。</param>
    /// <param name="previewPng">可选的第 1 页缩略图 PNG 字节（Core 不依赖 WPF，由渲染层提供）。</param>
    /// <param name="appVersion">写入包内的程序版本，便于排障。</param>
    /// <param name="indented">是否缩进 JSON（排障用；默认压缩以减小体积）。</param>
    public static void Save(
        WhiteboardDocument document,
        string path,
        byte[]? previewPng = null,
        string? appVersion = null,
        bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("保存路径不能为空", nameof(path));

        var full = Path.GetFullPath(EnsureExtension(path));
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var dto = WbFormat.ToDto(document, appVersion);
        var options = indented ? WbFormat.JsonOptionsIndented : WbFormat.JsonOptions;

        // 临时文件必须与目标**同目录**（同卷）才能原子替换
        var tmp = full + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var formatEntry = zip.CreateEntry(WbEntries.Format, CompressionLevel.Optimal);
                using (var s = formatEntry.Open())
                {
                    // 只写"标识 + 版本 + 生成信息"，不含文档内容（便于不解压就能判定文件类型）
                    var head = new WbFormat.FormatDto
                    {
                        Magic = WbFormat.Magic,
                        SchemaVersion = WbFormat.CurrentSchemaVersion,
                        AppVersion = appVersion,
                        CreatedUtc = dto.CreatedUtc,
                        Document = new WbFormat.DocumentDto { Id = dto.Document.Id }
                    };
                    JsonSerializer.Serialize(s, new
                    {
                        magic = head.Magic,
                        schemaVersion = head.SchemaVersion,
                        appVersion = head.AppVersion,
                        createdUtc = head.CreatedUtc,
                        documentId = head.Document.Id,
                        pageCount = document.Pages.Count,
                        objectCount = document.Pages.Sum(p => p.Count)
                    }, options);
                }

                var docEntry = zip.CreateEntry(WbEntries.Document, CompressionLevel.Optimal);
                using (var s = docEntry.Open())
                    JsonSerializer.Serialize(s, dto, options);

                if (previewPng is { Length: > 0 })
                {
                    var pe = zip.CreateEntry(WbEntries.Preview, CompressionLevel.NoCompression);
                    using var ps = pe.Open();
                    ps.Write(previewPng, 0, previewPng.Length);
                }
            }

            if (File.Exists(full)) File.Replace(tmp, full, null, ignoreMetadataErrors: true);
            else File.Move(tmp, full);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>加载。<paramref name="path"/> 可以是带或不带 <c>.wb</c> 的路径。</summary>
    public static WbLoadResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空", nameof(path));

        var full = ResolveExisting(path);
        if (full is null) throw new WbLoadException($"找不到文件：{path}");

        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(full);
        }
        catch (InvalidDataException ex)
        {
            throw new WbLoadException("这不是有效的 .wb 画板文件（无法作为压缩包打开，文件可能已损坏）", ex);
        }
        catch (IOException ex)
        {
            throw new WbLoadException($"无法读取文件（可能被其他程序占用）：{ex.Message}", ex);
        }

        using (zip)
        {
            // ① 校验标识（有 format.json 就校验；老/精简包没有也不致命）
            var formatEntry = zip.GetEntry(WbEntries.Format);
            if (formatEntry is not null)
            {
                using var fs = formatEntry.Open();
                var head = JsonSerializer.Deserialize<WbFormat.FormatDto>(fs, WbFormat.JsonOptions);
                if (head is null)
                    throw new WbLoadException("画板文件的头信息无法解析（文件可能已损坏）");

                if (!string.Equals(head.Magic, WbFormat.Magic, StringComparison.Ordinal))
                    throw new WbLoadException($"这不是白板画板文件（标识为 \"{head.Magic}\"）");

                if (head.SchemaVersion > WbFormat.CurrentSchemaVersion)
                    throw new WbLoadException(
                        $"画板文件由更新版本的程序创建（格式 v{head.SchemaVersion}），" +
                        $"当前程序最高支持 v{WbFormat.CurrentSchemaVersion}。请升级程序后再打开。");
            }

            // ② 文档本体
            var docEntry = zip.GetEntry(WbEntries.Document)
                ?? throw new WbLoadException("画板文件缺少 document.json（文件可能已损坏或不完整）");

            WbFormat.FormatDto? dto;
            try
            {
                using var ds = docEntry.Open();
                dto = JsonSerializer.Deserialize<WbFormat.FormatDto>(ds, WbFormat.JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new WbLoadException($"画板文件内容已损坏，无法解析：{ex.Message}", ex);
            }

            if (dto is null) throw new WbLoadException("画板文件内容为空或格式不正确");

            var (document, warnings) = WbFormat.FromDto(dto);
            return new WbLoadResult(document, warnings);
        }
    }

    /// <summary>读取包内缩略图（可能没有；用于"最近文件"列表）。</summary>
    public static byte[]? TryReadPreview(string path)
    {
        try
        {
            var full = ResolveExisting(path);
            if (full is null) return null;

            using var zip = ZipFile.OpenRead(full);
            var entry = zip.GetEntry(WbEntries.Preview);
            if (entry is null) return null;

            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>不解压读取包摘要（用于"最近文件"与排障）。</summary>
    public static (int SchemaVersion, int PageCount, int ObjectCount, DateTime? CreatedUtc)? TryReadSummary(string path)
    {
        try
        {
            var full = ResolveExisting(path);
            if (full is null) return null;

            using var zip = ZipFile.OpenRead(full);
            var entry = zip.GetEntry(WbEntries.Format);
            if (entry is null) return null;

            using var s = entry.Open();
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;

            var version = root.TryGetProperty("schemaVersion", out var v) ? v.GetInt32() : 0;
            var pages = root.TryGetProperty("pageCount", out var p) ? p.GetInt32() : 0;
            var objects = root.TryGetProperty("objectCount", out var o) ? o.GetInt32() : 0;
            DateTime? created = root.TryGetProperty("createdUtc", out var c) &&
                                DateTime.TryParse(c.GetString(), out var dt) ? dt : null;

            return (version, pages, objects, created);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>此路径是否像一个白板文件（只看头，不解析内容）。</summary>
    public static bool LooksLikeWb(string path)
    {
        try
        {
            var full = ResolveExisting(path);
            if (full is null) return false;

            using var zip = ZipFile.OpenRead(full);
            var entry = zip.GetEntry(WbEntries.Format);
            if (entry is null) return zip.GetEntry(WbEntries.Document) is not null;

            using var s = entry.Open();
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.TryGetProperty("magic", out var m) &&
                   string.Equals(m.GetString(), WbFormat.Magic, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? ResolveExisting(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full)) return full;

        var withExt = Path.GetFullPath(EnsureExtension(path));
        return File.Exists(withExt) ? withExt : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 临时文件删不掉不影响正确性；下次保存会生成新的临时名
        }
    }
}
