using System.IO;
using System.Windows.Media;
using WhiteBoard.Core.Model;
using WhiteBoard.Core.Storage;
using WhiteBoard.Rendering;
using WhiteBoard.Rendering.Export;

namespace WhiteBoard.App.Services;

/// <summary>一次"打开/新建/保存"的结果，供 UI 决定怎么提示。</summary>
public sealed record SessionActionResult(bool Ok, string Message, bool HasWarnings = false);

/// <summary>
/// 文档会话：把"当前文档 + 文件路径 + 脏标记 + 最近文件 + 自动保存"收在一处。
///
/// 这样做的原因：这些状态**必须一起变**。比如保存成功后要同时做四件事
/// （更新路径、清脏标记、写最近文件、清自动保存），散落在 UI 事件处理里
/// 迟早会漏掉一项，表现就是"明明存过了，关窗口还问你要不要保存"。
///
/// 本类只做逻辑，不弹对话框：路径由调用方（UI）选好后传进来，出错信息回传给 UI 显示。
/// </summary>
public sealed class DocumentSession
{
    private readonly PathService _paths;
    private readonly RecentFiles _recent;

    private DateTime _dirtySince = DateTime.MinValue;
    private DateTime _lastChange = DateTime.MinValue;

    public DocumentSession(PathService paths, string? appVersion = null)
    {
        _paths = paths;
        AppVersion = appVersion;
        _recent = RecentFiles.For(paths);
        AutoSave = new AutoSaveService(paths);
    }

    public string? AppVersion { get; }

    /// <summary>当前文档。</summary>
    public WhiteboardDocument Document { get; private set; } = NewDocument();

    /// <summary>当前文件路径；null = 从未保存过。</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>是否有未保存的改动。</summary>
    public bool IsDirty => _dirtySince != DateTime.MinValue;

    /// <summary>自动保存服务。</summary>
    public AutoSaveService AutoSave { get; }

    /// <summary>最近文件列表。</summary>
    public RecentFiles Recent => _recent;

    /// <summary>状态变化通知（标题栏刷新用）。</summary>
    public event Action? StateChanged;

    /// <summary>标题栏文本，如 <c>未命名 * — 白板</c>。</summary>
    public string DisplayName => CurrentPath is null
        ? (IsDirty ? "未命名 *" : "未命名")
        : Path.GetFileNameWithoutExtension(CurrentPath) + (IsDirty ? " *" : "");

    public static WhiteboardDocument NewDocument()
    {
        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();
        return doc;
    }

    /// <summary>标记"内容已改"（由 <c>CommandManager.Changed</c> 驱动）。</summary>
    public void MarkDirty(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (_dirtySince == DateTime.MinValue) _dirtySince = now;
        _lastChange = now;
        StateChanged?.Invoke();
    }

    /// <summary>换一份文档（新建/打开/恢复后调用）。</summary>
    public void Adopt(WhiteboardDocument document, string? path)
    {
        Document = document;
        CurrentPath = path;
        _dirtySince = DateTime.MinValue;
        _lastChange = DateTime.MinValue;
        StateChanged?.Invoke();
    }

    /// <summary>新建空白文档（不涉及文件系统）。</summary>
    public void New()
    {
        Adopt(NewDocument(), null);
        AutoSave.Clear();
    }

    /// <summary>
    /// 保存到指定路径（路径由 UI 选择；未指定则用当前路径）。
    /// 成功后：更新路径、清脏标记、写最近文件、清自动保存。
    /// </summary>
    public SessionActionResult Save(string? path = null, int previewMaxSide = 320)
    {
        var target = path ?? CurrentPath;
        if (string.IsNullOrWhiteSpace(target))
            return new SessionActionResult(false, "尚未指定保存位置");

        try
        {
            var preview = TryBuildPreviewPng(previewMaxSide);
            WbPackage.Save(Document, target, preview, AppVersion);

            var full = Path.GetFullPath(WbPackage.EnsureExtension(target));
            CurrentPath = full;
            _dirtySince = DateTime.MinValue;
            _lastChange = DateTime.MinValue;

            _recent.Add(full, Document.Pages.Count, Document.Pages.Sum(p => p.Count));
            AutoSave.Clear();

            StateChanged?.Invoke();
            return new SessionActionResult(true, $"已保存：{full}");
        }
        catch (Exception ex)
        {
            return new SessionActionResult(false, $"保存失败：{ex.Message}");
        }
    }

    /// <summary>打开文件。</summary>
    public SessionActionResult Open(string path)
    {
        try
        {
            var result = WbPackage.Load(path);
            Adopt(result.Document, Path.GetFullPath(path));

            _recent.Add(CurrentPath!, Document.Pages.Count, Document.Pages.Sum(p => p.Count));
            AutoSave.Clear();

            var msg = $"已打开：{CurrentPath}　（{Document.Pages.Count} 页，" +
                      $"{Document.Pages.Sum(p => p.Count)} 个对象）";
            if (result.HasWarnings) msg += "　⚠️ " + result.WarningText;

            return new SessionActionResult(true, msg, result.HasWarnings);
        }
        catch (WbLoadException ex)
        {
            return new SessionActionResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            return new SessionActionResult(false, $"打开失败：{ex.Message}");
        }
    }

    /// <summary>从自动保存恢复。</summary>
    public SessionActionResult RecoverFromAutoSave(AutoSaveInfo info)
    {
        var r = Open(info.AutoSaveFile);

        if (r.Ok)
        {
            // 恢复出来的内容**仍然是脏的**：它还没被保存回原文件
            CurrentPath = info.SourceMissing ? null : info.Meta.SourcePath;
            MarkDirty();
            AutoSave.Clear();
        }

        return r;
    }

    /// <summary>
    /// 检查启动时可恢复的自动保存。返回 null 表示无事可做。
    /// </summary>
    public AutoSaveInfo? PendingRecovery()
    {
        var info = AutoSave.Inspect();
        if (info is null) return null;

        // 自动保存不比原文件新 → 说明正常保存过，直接清掉，别打扰用户
        if (!info.NewerThanSource)
        {
            AutoSave.Clear();
            return null;
        }

        return info;
    }

    /// <summary>自动保存一次（由 UI 的定时器驱动）。</summary>
    public bool AutoSaveNow(DateTime nowUtc)
    {
        if (!IsDirty) return false;
        if (!AutoSave.ShouldSave(_dirtySince, _lastChange, nowUtc)) return false;

        return AutoSave.Save(Document, CurrentPath, TryBuildPreviewPng(200), AppVersion, nowUtc);
    }

    /// <summary>默认保存路径（data\documents\时间戳.wb）。</summary>
    public string DefaultSavePath() => _paths.DefaultDocumentPath();

    /// <summary>默认导出目录（data\export\画板名\）。</summary>
    public string DefaultExportDir()
    {
        var name = PngExporter.SanitizeFileName(
            CurrentPath is null ? "未命名" : Path.GetFileNameWithoutExtension(CurrentPath));
        return Path.Combine(_paths.DataRoot, "export", name);
    }

    /// <summary>导出当前页 PNG。</summary>
    public SessionActionResult ExportCurrentPage(
        Page page, SmoothGeometryBuilder geometry, string path, PngExportOptions? options = null)
    {
        try
        {
            var r = PngExporter.SavePage(page, geometry, path, options);
            return new SessionActionResult(true,
                $"已导出：{path}（{r.PixelWidth}×{r.PixelHeight}，{r.Bytes.Length / 1024} KB）　{r.Note}");
        }
        catch (Exception ex)
        {
            return new SessionActionResult(false, $"导出失败：{ex.Message}");
        }
    }

    /// <summary>批量导出所有页。</summary>
    public SessionActionResult ExportAllPages(
        SmoothGeometryBuilder geometry, string directory, PngExportOptions? options = null)
    {
        try
        {
            var name = CurrentPath is null ? "白板" : Path.GetFileNameWithoutExtension(CurrentPath);
            var files = PngExporter.ExportAllPages(Document, geometry, directory, name, options);
            return new SessionActionResult(true,
                $"已导出 {files.Count} 张：{directory}");
        }
        catch (Exception ex)
        {
            return new SessionActionResult(false, $"批量导出失败：{ex.Message}");
        }
    }

    /// <summary>为第 1 页生成缩略图（写入 .wb 包，供资源管理器/最近文件预览）。</summary>
    private byte[]? TryBuildPreviewPng(int maxSide)
    {
        try
        {
            if (Document.Pages.Count == 0) return null;

            var thumb = new ThumbnailRenderer(_previewGeometry) { MaxSide = maxSide, MarginWorld = 8 };
            return thumb.RenderPngBytes(Document.Pages[0]);
        }
        catch (Exception)
        {
            // 缩略图只是锦上添花，失败不影响保存
            return null;
        }
    }

    private readonly SmoothGeometryBuilder _previewGeometry = new();
}
