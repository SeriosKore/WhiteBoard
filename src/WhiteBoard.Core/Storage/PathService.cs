namespace WhiteBoard.Core.Storage;

/// <summary>数据目录不可写时的处理方式。</summary>
public enum DataRootState
{
    /// <summary>正常：程序文件夹可写，数据落在 exe 同级 data\。</summary>
    Writable,

    /// <summary>只读模式：不可写，禁用自动保存与配置写入（不静默降级到系统位置）。</summary>
    ReadOnly
}

/// <summary>
/// 路径服务（总路线 C3 + I2）。
///
/// 硬性规则：
///   1. 数据目录**固定**为 &lt;exe 同级&gt;\data\，**没有回退链**；
///   2. 禁止任何盘符字面量或绝对路径假设，一律基于 AppContext.BaseDirectory 解析；
///   3. 程序产生的所有数据文件必须落在 exe 同级目录或其子目录内；
///      唯一例外是"用户显式保存/导出"的目标路径（由调用方传入，不经过本服务）。
/// </summary>
public sealed class PathService
{
    public const string DataFolderName = "data";

    private PathService(string baseDirectory, string dataRoot, DataRootState state)
    {
        BaseDirectory = baseDirectory;
        DataRoot = dataRoot;
        State = state;
    }

    /// <summary>exe 所在目录（程序文件夹根）。</summary>
    public string BaseDirectory { get; }

    /// <summary>唯一可写数据根目录。</summary>
    public string DataRoot { get; }

    /// <summary>当前状态：可写 / 只读。</summary>
    public DataRootState State { get; }

    public bool IsWritable => State == DataRootState.Writable;

    // ---- 数据根下的固定子目录 ----
    public string SettingsFile => Combine(DataRoot, "settings.json");
    public string RecentFile => Combine(DataRoot, "recent.json");
    public string DocumentsDir => Ensure(Combine(DataRoot, "documents"));
    public string TemplatesDir => Ensure(Combine(DataRoot, "templates"));
    public string AutosaveDir => Ensure(Combine(DataRoot, "autosave"));
    public string LogsDir => Ensure(Combine(DataRoot, "logs"));
    public string ThemeDir => Ensure(Combine(DataRoot, "theme"));
    public string ThemeFile => Combine(ThemeDir, "color.txt");
    public string PluginsDir => Ensure(Combine(BaseDirectory, "ext"));

    /// <summary>
    /// 解析数据目录。默认以 <see cref="AppContext.BaseDirectory"/> 为基准；
    /// 测试时可传入自定义基准目录。
    /// </summary>
    public static PathService Resolve(string? baseDirectory = null)
    {
        var baseDir = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var dataRoot = Path.Combine(baseDir, DataFolderName);
        var state = ProbeWritable(dataRoot) ? DataRootState.Writable : DataRootState.ReadOnly;
        return new PathService(baseDir, dataRoot, state);
    }

    /// <summary>
    /// 探测目录是否真的可写（创建目录 + 写一个探针文件再删掉）。
    /// 只看 ACL 不够可靠：只读介质、被安全软件拦截、配额用尽都会在写入时才暴露。
    /// </summary>
    public static bool ProbeWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".wb-write-probe");
            File.WriteAllBytes(probe, [0x00]);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>只读模式下给用户看的中文提示。</summary>
    public const string ReadOnlyMessage =
        "程序文件夹不可写，请把整个 WhiteBoard 文件夹复制到可写位置后重试。";

    /// <summary>
    /// 把用户/插件传入的相对路径安全地解析到数据根下，拒绝目录穿越。
    /// </summary>
    public string ResolveUnderDataRoot(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("路径不能为空", nameof(relativePath));

        if (Path.IsPathRooted(relativePath))
            throw new UnauthorizedAccessException($"只允许相对路径：{relativePath}");

        var full = Path.GetFullPath(Path.Combine(DataRoot, relativePath));
        var root = Path.GetFullPath(DataRoot);

        // 必须落在 data\ 内（含自身）
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"路径越出数据目录：{relativePath}");
        }

        return full;
    }

    /// <summary>默认保存路径（用户未指定时）：data\documents\yyyyMMdd_HHmmss.wb</summary>
    public string DefaultDocumentPath(DateTime? now = null)
    {
        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd_HHmmss");
        return Path.Combine(DocumentsDir, stamp + ".wb");
    }

    private string Ensure(string dir)
    {
        if (IsWritable)
        {
            try { Directory.CreateDirectory(dir); } catch (Exception) { /* 只读介质下忽略 */ }
        }
        return dir;
    }

    private static string Combine(string a, string b) => Path.Combine(a, b);
}
