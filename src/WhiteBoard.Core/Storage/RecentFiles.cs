using System.Text.Json;
using System.Text.Json.Serialization;
using WhiteBoard.Core.Text;

namespace WhiteBoard.Core.Storage;

/// <summary>一条最近文件记录。</summary>
public sealed record RecentEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("lastOpenUtc")] DateTime LastOpenUtc,
    [property: JsonPropertyName("pageCount")] int PageCount,
    [property: JsonPropertyName("objectCount")] int ObjectCount);

/// <summary>
/// 最近打开的画板列表（落在 <c>&lt;exe同级&gt;\data\recent.json</c>）。
///
/// 三条规矩：
/// <list type="bullet">
/// <item>**不写注册表**（C2）——Windows 的"最近文档"恰恰是写在注册表里的，所以这里自己做；</item>
/// <item>**只记路径，不复制文件**（C3）——用户显式保存的画板可以在任意位置，
///       程序不搬运它，只记路径；文件不在了就自动从列表里剔除；</item>
/// <item>**编码安全**——用 <see cref="TextFileCodec"/> 写 UTF-8 BOM，
///       路径里有中文/日文/emoji 都不会乱码（PowerShell 5.1 的坑也是同一个原因）。</item>
/// </list>
/// </summary>
public sealed class RecentFiles
{
    public const int MaxEntries = 10;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _file;

    public RecentFiles(string file) => _file = file;

    public static RecentFiles For(PathService paths) => new(paths.RecentFile);

    public string FilePath => _file;

    /// <summary>读取列表（文件不存在/损坏时返回空列表，绝不抛异常）。</summary>
    public IReadOnlyList<RecentEntry> Load()
    {
        try
        {
            if (!File.Exists(_file)) return [];

            var bytes = File.ReadAllBytes(_file);
            var json = TextFileCodec.Decode(bytes);
            var list = JsonSerializer.Deserialize<List<RecentEntry>>(json, Options) ?? [];
            return list.OrderByDescending(e => e.LastOpenUtc).Take(MaxEntries).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>读取并剔除已经不存在的文件（返回真正可用的条目）。</summary>
    public IReadOnlyList<RecentEntry> LoadExisting()
    {
        var all = Load();
        var alive = all.Where(e => File.Exists(e.Path)).ToList();

        if (alive.Count != all.Count) TryWrite(alive);
        return alive;
    }

    /// <summary>把一条记录置顶（已存在则更新时间与统计，不重复）。</summary>
    public void Add(string path, int pageCount, int objectCount, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var full = Path.GetFullPath(path);
        var list = Load()
            .Where(e => !string.Equals(Path.GetFullPath(e.Path), full, StringComparison.OrdinalIgnoreCase))
            .ToList();

        list.Insert(0, new RecentEntry(
            full,
            Path.GetFileNameWithoutExtension(full),
            nowUtc ?? DateTime.UtcNow,
            pageCount,
            objectCount));

        TryWrite(list.Take(MaxEntries).ToList());
    }

    public void Remove(string path)
    {
        var full = Path.GetFullPath(path);
        var list = Load()
            .Where(e => !string.Equals(Path.GetFullPath(e.Path), full, StringComparison.OrdinalIgnoreCase))
            .ToList();
        TryWrite(list);
    }

    public void Clear() => TryWrite([]);

    private void TryWrite(IReadOnlyList<RecentEntry> list)
    {
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(list, Options);
            File.WriteAllBytes(_file, TextFileCodec.Encode(json));
        }
        catch (Exception)
        {
            // 只读介质 / 被占用：最近文件写不进去不影响画板使用，静默忽略
        }
    }
}
