using System.Text.Json;
using System.Text.Json.Serialization;
using WhiteBoard.Core.Model;
using WhiteBoard.Core.Text;

namespace WhiteBoard.Core.Storage;

/// <summary>自动保存的元信息（存在 <c>data\autosave\autosave.json</c>）。</summary>
public sealed class AutoSaveMeta
{
    [JsonPropertyName("sourcePath")] public string? SourcePath { get; set; }
    [JsonPropertyName("savedUtc")] public DateTime SavedUtc { get; set; }
    [JsonPropertyName("pageCount")] public int PageCount { get; set; }
    [JsonPropertyName("objectCount")] public int ObjectCount { get; set; }
    [JsonPropertyName("appVersion")] public string? AppVersion { get; set; }
}

/// <summary>可恢复的自动保存。</summary>
public sealed record AutoSaveInfo(
    string AutoSaveFile,
    AutoSaveMeta Meta)
{
    /// <summary>原文件是否已不存在（保存过的文件被删/被移走）。</summary>
    public bool SourceMissing => string.IsNullOrWhiteSpace(Meta.SourcePath) || !File.Exists(Meta.SourcePath);

    /// <summary>自动保存是否比原文件更新（即"有未保存的改动"）。</summary>
    public bool NewerThanSource
    {
        get
        {
            if (SourceMissing) return true;
            try
            {
                return Meta.SavedUtc > File.GetLastWriteTimeUtc(Meta.SourcePath!).AddSeconds(1);
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    public string Describe()
    {
        var src = SourceMissing ? "（原文件已不在）" : $"（原文件：{Path.GetFileName(Meta.SourcePath)}）";
        return $"{Meta.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}　" +
               $"{Meta.PageCount} 页 / {Meta.ObjectCount} 个对象{src}";
    }
}

/// <summary>
/// 自动保存与崩溃恢复（C3：全部落在 <c>&lt;exe同级&gt;\data\autosave\</c>）。
///
/// 设计要点：
/// <list type="number">
/// <item>**调用方驱动的时间判断**（<see cref="ShouldSave"/>），不内部起计时器 ——
///       Core 不依赖 UI 框架，App 用 <c>DispatcherTimer</c> 每次问一句即可，也便于单测；</item>
/// <item>**只在"有改动且距上次改动够久"时写**：避免书写过程中频繁写盘抢 IO（书写是延迟敏感的）；</item>
/// <item>**先写 .wb 再写 .json**：断电时最坏情况是"有 wb 没 meta"，
///       此时按"无自动保存"处理，不会误导用户去恢复一个不存在的内容；</item>
/// <item>**写失败绝不打断使用**：只读介质、磁盘满都只是记一条状态，不影响画板。</item>
/// </list>
/// </summary>
public sealed class AutoSaveService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly PathService _paths;

    public AutoSaveService(PathService paths)
    {
        _paths = paths;
        AutoSaveFile = Path.Combine(paths.AutosaveDir, "autosave" + WbPackage.Extension);
        MetaFile = Path.Combine(paths.AutosaveDir, "autosave.json");
    }

    /// <summary>距上次改动至少这么久才写盘（默认 25 秒）。</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>连续书写时最多拖这么久（默认 90 秒），避免一直不落盘。</summary>
    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromSeconds(90);

    public string AutoSaveFile { get; }
    public string MetaFile { get; }

    /// <summary>只读模式下自动保存不可用。</summary>
    public bool Enabled => _paths.IsWritable;

    /// <summary>最近一次写入的结果（状态栏可显示）。</summary>
    public string LastStatus { get; private set; } = "尚未自动保存";

    public DateTime LastSavedUtc { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// 是否该写盘了。调用方每次"文档有改动"时调用一次。
    /// </summary>
    /// <param name="dirtySinceUtc">
    /// 本轮"脏"开始的时间（第一次改动时记下，成功保存后清空）。
    /// <see cref="DateTime.MinValue"/> 表示当前没有未保存的改动。
    /// </param>
    /// <param name="lastChangeUtc">最近一次改动的时间。</param>
    /// <param name="nowUtc">当前时间。</param>
    public bool ShouldSave(DateTime dirtySinceUtc, DateTime lastChangeUtc, DateTime nowUtc)
    {
        if (!Enabled) return false;
        if (dirtySinceUtc == DateTime.MinValue || lastChangeUtc == DateTime.MinValue) return false;

        // ① 用户停下来了（距最后一次改动超过 MinInterval）→ 立刻落一版
        if (nowUtc - lastChangeUtc >= MinInterval) return true;

        // ② 一直在写没停过 → 也不能永远不落盘，否则一次崩溃丢掉全部内容。
        //    计时起点取"上次保存"与"本轮开始变脏"中较晚的那个。
        var streakStart = LastSavedUtc > dirtySinceUtc ? LastSavedUtc : dirtySinceUtc;
        return nowUtc - streakStart >= MaxInterval;
    }

    /// <summary>立即写一次。返回是否成功。</summary>
    public bool Save(
        WhiteboardDocument document,
        string? sourcePath,
        byte[]? previewPng = null,
        string? appVersion = null,
        DateTime? nowUtc = null)
    {
        if (!Enabled)
        {
            LastStatus = "程序文件夹不可写，自动保存已关闭";
            return false;
        }

        var now = nowUtc ?? DateTime.UtcNow;

        try
        {
            Directory.CreateDirectory(_paths.AutosaveDir);

            WbPackage.Save(document, AutoSaveFile, previewPng, appVersion);

            var meta = new AutoSaveMeta
            {
                SourcePath = sourcePath,
                SavedUtc = now,
                PageCount = document.Pages.Count,
                ObjectCount = document.Pages.Sum(p => p.Count),
                AppVersion = appVersion
            };
            File.WriteAllBytes(MetaFile, TextFileCodec.Encode(JsonSerializer.Serialize(meta, Options)));

            LastSavedUtc = now;
            LastStatus = $"已自动保存（{now.ToLocalTime():HH:mm:ss}）";
            return true;
        }
        catch (Exception ex)
        {
            LastStatus = $"自动保存失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>检查是否存在可恢复的自动保存（启动时调用）。</summary>
    public AutoSaveInfo? Inspect()
    {
        try
        {
            if (!File.Exists(AutoSaveFile) || !File.Exists(MetaFile)) return null;
            if (!WbPackage.LooksLikeWb(AutoSaveFile)) return null;

            var meta = JsonSerializer.Deserialize<AutoSaveMeta>(
                TextFileCodec.Decode(File.ReadAllBytes(MetaFile)), Options);

            return meta is null ? null : new AutoSaveInfo(AutoSaveFile, meta);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>丢弃自动保存（正常保存/用户放弃恢复后调用）。</summary>
    public void Clear()
    {
        foreach (var f in new[] { AutoSaveFile, MetaFile })
        {
            try
            {
                if (File.Exists(f)) File.Delete(f);
            }
            catch (Exception)
            {
                // 删不掉就留着；下次 Inspect 会把它当成"上一版的残留"，不会造成损坏
            }
        }
        LastSavedUtc = DateTime.MinValue;
        LastStatus = "自动保存已清空";
    }
}
