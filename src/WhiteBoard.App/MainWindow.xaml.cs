using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WhiteBoard.App.Diagnostics;
using WhiteBoard.App.Rendering;
using WhiteBoard.App.Services;
using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;
using WhiteBoard.Core.Storage;
using WhiteBoard.Core.Theme;
using WhiteBoard.Rendering;
using WhiteBoard.Rendering.Export;
using WhiteBoard.Rendering.Tools;
using Microsoft.Win32;

// WPF 的 System.Windows.Controls 里也有 SelectionMode（给 ListBox 用），这里取工具层的那个
using SelectionMode = WhiteBoard.Rendering.Tools.SelectionMode;

// 同理，System.Windows.Input.ICommand（给按键绑定用）与 Core 的可撤销命令同名
using CoreCommand = WhiteBoard.Core.Commands.ICommand;

namespace WhiteBoard.App;

/// <summary>
/// 主窗口：文件栏 + 工具栏 + 画布 + 状态栏。
///
/// 已经打通的完整链路：
/// 指针事件 → <c>PointerRouter</c> 路由 → 工具（画笔/直线/矩形/椭圆/橡皮）
/// → 命令入当前页撤销栈 → <c>PageRenderer</c> 矢量重绘
/// → <c>WbPackage</c> 原子保存 / <c>PngExporter</c> 导出。
/// </summary>
public partial class MainWindow : Window, ITextEditHost
{
    private const string AppVersion = "1.5.0";

    private readonly PathService _paths;
    private readonly ThemeLoadResult _theme;
    private readonly DocumentSession _session;

    private readonly PenTool _penTool = new();
    private readonly LineTool _lineTool = new();
    private readonly RectTool _rectTool = new();
    private readonly EllipseTool _ellipseTool = new();
    private readonly EraserTool _eraserTool;
    private readonly SelectTool _selectTool;
    private readonly TextTool _textTool;

    /// <summary>就地文本编辑的当前状态（null = 没在编辑）。</summary>
    private TextObject? _editingText;
    private bool _editingIsNew;

    /// <summary>新建文字时点击处的世界坐标（= 文本框左上角）。</summary>
    private PointD _pendingTextWorld;

    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    /// <summary>缩略图刷新节流：书写过程中不要每落一笔都重渲染所有缩略图。</summary>
    private readonly DispatcherTimer _thumbnailTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

    private readonly ThumbnailRenderer _thumbnails;
    private readonly List<Image> _thumbImages = [];
    private readonly List<Border> _thumbFrames = [];

    /// <summary>最近一次页面操作（新建/复制/删除/换序），可在状态栏一键撤销。</summary>
    private CoreCommand? _lastPageCommand;

    private bool _fullScreen;
    private WindowState _preFullScreenState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();

        _paths = PathService.Resolve();
        _theme = ThemeService.Load(_paths);
        _session = new DocumentSession(_paths, AppVersion);

        // 橡皮与画布共用几何构建器（复用冻结缓存）
        _eraserTool = new EraserTool(Board.Geometry);
        _selectTool = new SelectTool(Board.Geometry);
        _textTool = new TextTool(Board.Geometry);
        Board.TextEditorHost = this;
        _thumbnails = new ThumbnailRenderer(Board.Geometry) { Background = ToColor(_theme.Preset.Background) };

        Board.Document = _session.Document;
        Board.BackgroundColor = ToColor(_theme.Preset.Background);
        Board.StatusChanged += OnBoardStatus;
        Board.Selection.Changed += OnSelectionChanged;
        Board.PageChanged += OnPageChanged;
        Board.ContentChanged += OnCanvasContentChanged;
        Board.SelectionDraggedOut = OnSelectionDraggedOut;
        Board.SetTool(_penTool);

        // 任何命令入栈/撤销/重做都会走到这里 → 标记脏 + 刷新标题
        Board.Commands.Changed += () => _session.MarkDirty();
        Board.Commands.Changed += UpdateUndoButtons;
        _session.StateChanged += UpdateTitle;

        ApplyPenDefaults();
        BuildColorPalette();
        UpdateToolButtons();
        UpdateTitle();
        ShowEnvironment();
        ShowTheme();
        UpdateUndoButtons();
        RebuildPageList();

        _autoSaveTimer.Tick += OnAutoSaveTick;
        _autoSaveTimer.Start();

        _thumbnailTimer.Tick += OnThumbnailTick;

        RegisterShortcuts();

        if (App.IsDemoRequested) LoadDemoBoard();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>
    /// 快捷键。
    ///
    /// 分成两类，原因是 WPF 的 <see cref="KeyGesture"/> **不允许**"无修饰键 + 字母/数字"这类组合
    /// （它会抛 <c>NotSupportedException：KeyGesture 尚不支持 None+D1</c>），
    /// 所以：
    /// <list type="bullet">
    /// <item>带 Ctrl 的组合走 <see cref="InputBindings"/>（不依赖焦点，最稳）；</item>
    /// <item>裸键（F11、数字选工具）在 <see cref="OnKeyDown"/> 里处理，
    ///       并避开"焦点在文本框里打字"的场景。</item>
    /// </list>
    /// </summary>
    private void RegisterShortcuts()
    {
        void Bind(Key key, ModifierKeys mods, Action action)
            => InputBindings.Add(new KeyBinding(new RelayCommand(action), key, mods));

        Bind(Key.Z, ModifierKeys.Control, DoUndo);
        Bind(Key.Y, ModifierKeys.Control, DoRedo);
        Bind(Key.Z, ModifierKeys.Control | ModifierKeys.Shift, DoRedo);

        Bind(Key.N, ModifierKeys.Control, NewBoard);
        Bind(Key.O, ModifierKeys.Control, OpenBoard);
        Bind(Key.S, ModifierKeys.Control, SaveBoard);
        Bind(Key.S, ModifierKeys.Control | ModifierKeys.Shift, SaveBoardAs);

        Bind(Key.E, ModifierKeys.Control, ExportCurrentPage);
        Bind(Key.E, ModifierKeys.Control | ModifierKeys.Shift, ExportAllPages);
        Bind(Key.OemPlus, ModifierKeys.Control, () => Board.ZoomBy(1.25));
        Bind(Key.OemMinus, ModifierKeys.Control, () => Board.ZoomBy(1 / 1.25));
        Bind(Key.D0, ModifierKeys.Control, Board.ResetView);
        Bind(Key.A, ModifierKeys.Control, SelectAllShortcut);
    }

    private void SelectAllShortcut() => OnSelectAll(this, new RoutedEventArgs());

    /// <summary>裸键快捷键（见 <see cref="RegisterShortcuts"/> 的说明）。</summary>
    private bool HandleBareKey(Key key)
    {
        // 焦点在可输入控件里时不抢键（S1 暂无文本框，这里为 S2 的文本工具预留）
        if (Keyboard.FocusedElement is TextBoxBase) return false;

        switch (key)
        {
            case Key.F11:
                ToggleFullScreen();
                return true;

            case Key.Delete:
            case Key.Back:
                // 焦点在按钮上时 Del 不该触发删除（否则"点过按钮顺手按 Del"会误删）
                if (Keyboard.FocusedElement is ButtonBase) return false;
                DeleteSelection();
                return true;

            case Key.PageUp:
                Board.GoToPreviousPage();
                return true;

            case Key.PageDown:
                Board.GoToNextPage();
                return true;

            case Key.D1:
            case Key.NumPad1:
                SelectTool(_penTool);
                return true;
            case Key.D2:
            case Key.NumPad2:
                SelectTool(_lineTool);
                return true;
            case Key.D3:
            case Key.NumPad3:
                SelectTool(_rectTool);
                return true;
            case Key.D4:
            case Key.NumPad4:
                SelectTool(_ellipseTool);
                return true;
            case Key.D5:
            case Key.NumPad5:
                SelectTool(_eraserTool);
                return true;
            case Key.D6:
            case Key.NumPad6:
            case Key.S:
                SelectTool(_selectTool);
                return true;

            case Key.D7:
            case Key.NumPad7:
            case Key.T:
                SelectTool(_textTool);
                return true;

            default:
                return false;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Board.Focus();

        // 崩溃/断电后重启：若自动保存比原文件新，问一句是否恢复
        var pending = _session.PendingRecovery();
        if (pending is null) return;

        var answer = MessageBox.Show(this,
            $"发现上次未保存的内容：\n\n{pending.Describe()}\n\n是否恢复？\n" +
            "（选择「否」将丢弃这部分内容）",
            "白板 · 恢复未保存的内容",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Yes)
        {
            var r = _session.RecoverFromAutoSave(pending);
            AdoptDocument(r.Ok ? "已从自动保存恢复" : r.Message);
        }
        else
        {
            _session.AutoSave.Clear();
            StatusText.Text = "已丢弃上次未保存的内容";
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CommitTextEdit();   // 关窗口前先把正在输入的文字落库

        if (!_session.IsDirty) return;

        var answer = MessageBox.Show(this,
            $"「{_session.DisplayName}」有未保存的改动，是否保存？",
            "白板 · 关闭",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (answer == MessageBoxResult.No) return;

        var r = SaveWithDialogIfNeeded();
        if (!r.Ok)
        {
            MessageBox.Show(this, r.Message, "白板 · 保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Cancel = true;   // 保存失败就别关，否则内容丢了
        }
    }

    /// <summary>每 5 秒问一次"该自动保存吗"（真正的时间判断在 Core，可单测）。</summary>
    private void OnAutoSaveTick(object? sender, EventArgs e)
    {
        if (!_session.IsDirty) return;
        if (!_session.AutoSaveNow(DateTime.UtcNow)) return;

        StatusText.Text = _session.AutoSave.LastStatus +
                          $"　│　{_session.AutoSave.AutoSaveFile}";
    }

    // ── 文件 ──────────────────────────────────────────────────────────────

    private void OnNew(object s, RoutedEventArgs e) => NewBoard();

    private void NewBoard()
    {
        if (!ConfirmDiscardIfDirty()) return;
        CommitTextEdit();


        Board.CancelPending();
        _session.New();
        Board.Document = _session.Document;
        Board.Geometry.Clear();
        AdoptDocument("已新建空白画板");
    }

    private void OnOpen(object s, RoutedEventArgs e) => OpenBoard();

    private void OpenBoard()
    {
        if (!ConfirmDiscardIfDirty()) return;
        CommitTextEdit();


        var dlg = new OpenFileDialog
        {
            Title = "打开画板",
            Filter = "白板画板 (*.wb)|*.wb|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(_paths.DocumentsDir) ? _paths.DocumentsDir : _paths.BaseDirectory,
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) != true) return;

        var r = _session.Open(dlg.FileName);
        AdoptDocument(r.Message, r.Ok);
        if (!r.Ok) MessageBox.Show(this, r.Message, "白板 · 打开失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>
    /// 追加导入：把另一份画板的所有页并到当前文档末尾（合并两个老师的素材）。
    /// 走页面级命令，因此可在状态栏整体撤销；导入**不会**改动源文件。
    /// </summary>
    private void OnAppendImport(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "追加导入另一份画板（页会加到当前文档末尾）",
            Filter = "白板画板 (*.wb)|*.wb|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(_paths.DocumentsDir) ? _paths.DocumentsDir : _paths.BaseDirectory,
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) != true) return;

        WbLoadResult loaded;
        try
        {
            loaded = WbPackage.Load(dlg.FileName);
        }
        catch (WbLoadException ex)
        {
            MessageBox.Show(this, ex.Message, "白板 · 追加导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var cmd = new AppendDocumentCommand(_session.Document, loaded.Document);
        if (cmd.NothingToDo)
        {
            StatusText.Text = "那份画板里没有任何页面，没什么可导入的";
            return;
        }

        var pages = loaded.Document.Pages.Count;
        var objects = loaded.Document.Pages.Sum(p => p.Count);

        RunPageCommand(cmd);

        StatusText.Text = $"已从「{Path.GetFileName(dlg.FileName)}」追加 {pages} 页 / {objects} 个对象" +
                          $"（共 {_session.Document.Pages.Count} 页，可撤销）";
        if (loaded.HasWarnings) WarnText.Text = "⚠️ " + loaded.WarningText;
    }

    private void OnSave(object s, RoutedEventArgs e) => SaveBoard();
    private void SaveBoard()
    {
        CommitTextEdit();   // 半截编辑内容不能丢，先落库

        var r = SaveWithDialogIfNeeded();
        StatusText.Text = r.Message;
        if (!r.Ok && r.Message != "已取消保存")
            MessageBox.Show(this, r.Message, "白板 · 保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnSaveAs(object s, RoutedEventArgs e) => SaveBoardAs();

    private void SaveBoardAs()
    {
        CommitTextEdit();

        var dlg = SaveFileDialogForSave();
        if (dlg.ShowDialog(this) != true) return;

        var r = _session.Save(dlg.FileName);
        StatusText.Text = r.Message;
        UpdateTitle();
        if (!r.Ok) MessageBox.Show(this, r.Message, "白板 · 保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>有当前路径就直接存，否则弹"另存为"。</summary>
    private SessionActionResult SaveWithDialogIfNeeded()
    {
        if (_session.CurrentPath is null)
        {
            var dlg = SaveFileDialogForSave();
            if (dlg.ShowDialog(this) != true) return new SessionActionResult(false, "已取消保存");
            var r0 = _session.Save(dlg.FileName);
            UpdateTitle();
            return r0;
        }

        var r = _session.Save();
        UpdateTitle();
        return r;
    }

    private SaveFileDialog SaveFileDialogForSave()
    {
        var name = _session.CurrentPath is null
            ? DateTime.Now.ToString("yyyyMMdd_HHmmss")
            : Path.GetFileNameWithoutExtension(_session.CurrentPath);

        return new SaveFileDialog
        {
            Title = "保存画板",
            Filter = "白板画板 (*.wb)|*.wb",
            DefaultExt = WbPackage.Extension,
            FileName = name,
            InitialDirectory = _session.CurrentPath is not null
                ? Path.GetDirectoryName(_session.CurrentPath)!
                : (Directory.Exists(_paths.DocumentsDir) ? _paths.DocumentsDir : _paths.BaseDirectory),
            AddExtension = true,
            OverwritePrompt = true
        };
    }

    private void OnRecent(object s, RoutedEventArgs e)
    {
        var entries = _session.Recent.LoadExisting();
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
            Foreground = Brushes.White,
            PlacementTarget = BtnRecent,
            Placement = PlacementMode.Bottom
        };

        if (entries.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "（还没有最近打开的画板）",
                IsEnabled = false,
                Foreground = Brushes.Gray
            });
        }
        else
        {
            foreach (var entry in entries)
            {
                var item = new MenuItem
                {
                    Header = $"{entry.Name}　({entry.PageCount} 页 · {entry.ObjectCount} 对象)　{entry.LastOpenUtc.ToLocalTime():MM-dd HH:mm}",
                    ToolTip = entry.Path,
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
                    Tag = entry.Path
                };
                item.Click += (_, _) => OpenRecent((string)item.Tag);
                menu.Items.Add(item);
            }
        }

        menu.IsOpen = true;
    }

    private void OpenRecent(string path)
    {
        if (!ConfirmDiscardIfDirty()) return;

        var r = _session.Open(path);
        if (!r.Ok)
        {
            MessageBox.Show(this, r.Message + "\n\n该文件可能已被移动或删除，将把它从最近列表中移除。",
                "白板 · 打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            _session.Recent.Remove(path);
            return;
        }

        AdoptDocument(r.Message, true);
    }

    private void OnExportPng(object s, RoutedEventArgs e) => ExportCurrentPage();

    private void ExportCurrentPage()
    {
        CommitTextEdit();   // 导出要包含正在输入的文字

        var page = Board.CurrentPage;
        if (page is null) return;

        var name = (_session.CurrentPath is null ? "未命名" : Path.GetFileNameWithoutExtension(_session.CurrentPath))
                   + "-当前页";

        var dlg = new SaveFileDialog
        {
            Title = "导出当前页为 PNG",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            FileName = name,
            InitialDirectory = EnsureDir(_session.DefaultExportDir()),
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dlg.ShowDialog(this) != true) return;

        var r = _session.ExportCurrentPage(page, Board.Geometry, dlg.FileName, ExportOptions());
        StatusText.Text = r.Message;
        if (!r.Ok) MessageBox.Show(this, r.Message, "白板 · 导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnExportAllPng(object s, RoutedEventArgs e) => ExportAllPages();

    private void ExportAllPages()
    {
        CommitTextEdit();

        var dlg = new OpenFolderDialog
        {
            Title = "选择导出目录（每页一张 PNG）",
            InitialDirectory = EnsureDir(_session.DefaultExportDir()),
            Multiselect = false
        };

        if (dlg.ShowDialog(this) != true) return;

        var r = _session.ExportAllPages(Board.Geometry, dlg.FolderName, ExportOptions());
        StatusText.Text = r.Message;
        if (!r.Ok) MessageBox.Show(this, r.Message, "白板 · 导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private PngExportOptions ExportOptions() => new()
    {
        Scale = 2,
        MarginWorld = 24,
        UseContentBounds = true,
        Background = Board.BackgroundColor
    };

    private static string EnsureDir(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch (Exception) { }
        return dir;
    }

    /// <summary>文件可见性：在资源管理器里打开数据目录（不写注册表，只是启动 explorer）。</summary>
    private void OnOpenDataFolder(object s, RoutedEventArgs e)
    {
        try
        {
            var target = _session.CurrentPath is not null
                ? Path.GetDirectoryName(_session.CurrentPath)!
                : _paths.DataRoot;

            Directory.CreateDirectory(target);

            // UseShellExecute 才能让 shell 打开文件夹；explorer 自己负责窗口管理
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + target + "\"",
                UseShellExecute = true
            });
            StatusText.Text = $"已在资源管理器中打开：{target}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开文件夹失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 打开随程序发布的《快捷键说明.txt》（在 exe 同级目录，与发布产物一起走）。
    /// 找不到时把内容直接显示在窗口里，而不是给用户一个"文件不存在"。
    /// </summary>
    private void OnOpenHelp(object s, RoutedEventArgs e)
    {
        var file = Path.Combine(_paths.BaseDirectory, "快捷键说明.txt");
        try
        {
            if (File.Exists(file))
            {
                Process.Start(new ProcessStartInfo { FileName = file, UseShellExecute = true });
                StatusText.Text = $"已打开：{file}";
                return;
            }

            MessageBox.Show(this,
                "快捷键：\n\n" +
                "文件：Ctrl+N 新建 / Ctrl+O 打开 / Ctrl+S 保存 / Ctrl+Shift+S 另存为\n" +
                "      Ctrl+E 导出当前页 / Ctrl+Shift+E 导出全部页\n" +
                "编辑：Ctrl+Z 撤销 / Ctrl+Y 重做 / Ctrl+A 全选 / Del 删除选中\n" +
                "视图：Ctrl+= 放大 / Ctrl+- 缩小 / Ctrl+0 100% / F11 全屏 / Esc 退出全屏或取消选择\n" +
                "     滚轮缩放、中键拖动平移、两指捏合缩放平移\n" +
                "工具：1 画笔 / 2 直线 / 3 矩形 / 4 椭圆 / 5 橡皮 / 6 或 S 选择\n" +
                "选择：点对象选中、点空白取消、拖动移动、拖动空白框选\n\n" +
                $"（未找到说明文件：{file}）",
                "白板 · 快捷键", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开说明失败：{ex.Message}";
        }
    }

    private bool ConfirmDiscardIfDirty()    {
        if (!_session.IsDirty) return true;

        var answer = MessageBox.Show(this,
            $"「{_session.DisplayName}」有未保存的改动，是否先保存？",
            "白板", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) return true;

        return SaveWithDialogIfNeeded().Ok;
    }

    /// <summary>切换文档后统一刷新画布、撤销按钮、标题、页面目录。</summary>
    private void AdoptDocument(string status, bool ok = true)
    {
        Board.CancelPending();
        Board.ClearSelection();
        Board.Document = _session.Document;
        Board.Commands.SetCurrentPage(_session.Document.CurrentPage.Id);
        Board.Geometry.Clear();
        Board.InvalidateVisual();

        _lastPageCommand = null;
        BtnUndoPageOp.Visibility = Visibility.Collapsed;

        RebuildPageList();
        UpdateUndoButtons();
        UpdateTitle();
        StatusText.Text = status;
        if (!ok) WarnText.Visibility = Visibility.Collapsed;
    }

    private void UpdateTitle()
    {
        var name = _session.DisplayName;
        FileTitleText.Text = name;
        Title = $"{name} — 白板";
        BtnSave.IsEnabled = true;
        BtnSaveAs.IsEnabled = true;
    }

    // ── 工具与画笔 ────────────────────────────────────────────────────────

    private void ApplyPenDefaults()
    {
        var palette = _theme.Preset.Colors;
        Board.PenColor = palette.Count > 0 ? palette[0].Value : "#F5F5F0";
        Board.PenWidth = 3;
    }

    private void OnToolPen(object s, RoutedEventArgs e) => SelectTool(_penTool);
    private void OnToolLine(object s, RoutedEventArgs e) => SelectTool(_lineTool);
    private void OnToolRect(object s, RoutedEventArgs e) => SelectTool(_rectTool);
    private void OnToolEllipse(object s, RoutedEventArgs e) => SelectTool(_ellipseTool);
    private void OnToolEraser(object s, RoutedEventArgs e) => SelectTool(_eraserTool);
    private void OnToolSelect(object s, RoutedEventArgs e) => SelectTool(_selectTool);
    private void OnToolText(object s, RoutedEventArgs e) => SelectTool(_textTool);

    private void SelectTool(ITool tool)
    {
        // 切工具前先把正在编辑的文字提交掉（否则编辑框会留在屏幕上、内容却没落库）
        CommitTextEdit();

        Board.SetTool(tool);
        UpdateToolButtons();
        Board.Focus();
    }

    // ── 就地文本编辑（评审 T5：叠加真 TextBox，IME 天然可用）──────────────

    /// <summary>
    /// 开始编辑。<paramref name="existing"/> 为 null 表示新建（点击处即文本框左上角）。
    ///
    /// 关键点：**编辑框的字号要乘当前缩放**，位置也要按当前视口换算——
    /// 否则放大到 3 倍时，编辑框里的字比别处小一圈，提交瞬间会"跳大"。
    /// </summary>
    public void BeginTextEdit(PointD worldPoint, TextObject? existing)
    {
        // 已经有编辑中的文字：先提交掉，避免两段编辑叠加
        if (_editingText is not null || _editingIsNew) CommitTextEdit();

        var vp = Board.Viewport;
        if (vp is null) return;

        _editingIsNew = existing is null;
        _editingText = existing;
        _pendingTextWorld = worldPoint;

        if (existing is not null)
        {
            // 改已有文字：把左上角对回它自己的位置，字号用它的
            worldPoint = new PointD(existing.X, existing.Y);
            _textTool.FontSize = existing.FontSize;
            TextEditor.Text = existing.Text;
            TextEditor.SelectAll();
            StatusText.Text = "编辑文字：Ctrl+Enter 或点击别处提交，Esc 取消";
        }
        else
        {
            TextEditor.Text = "";
            StatusText.Text = $"新建文字（{_textTool.FontSize:0.#} 号）：Enter 换行，Ctrl+Enter 或点击别处提交，Esc 取消";
        }

        PositionTextEditor(worldPoint, _textTool.FontSize);

        TextEditor.Foreground = GeometryInterop.BrushFromHex(Board.PenColor);
        TextEditor.FontFamily = new FontFamily(_textTool.FontFamily);
        TextEditor.Visibility = Visibility.Visible;

        // 排版后再摆一次：文本框会随内容长高，位置以上边缘为准不跳动
        TextEditor.Dispatcher.BeginInvoke(new Action(() =>
        {
            PositionTextEditor(worldPoint, _textTool.FontSize);
            TextEditor.Focus();
            Keyboard.Focus(TextEditor);
        }), DispatcherPriority.Loaded);
    }

    /// <summary>把编辑框摆到给定的世界坐标，并把字号换算成屏幕像素。</summary>
    private void PositionTextEditor(PointD worldPoint, double fontSize)
    {
        var vp = Board.Viewport;
        if (vp is null) return;

        var screen = vp.WorldToScreen(worldPoint);
        Canvas.SetLeft(TextEditor, screen.X);
        Canvas.SetTop(TextEditor, screen.Y);

        // 字号是世界单位 → 屏幕像素
        TextEditor.FontSize = Math.Max(1, fontSize * vp.Zoom);
        TextEditor.Width = Math.Max(60, TextEditMaxWidth * vp.Zoom);
        TextEditor.MinHeight = Math.Max(20, fontSize * 1.4 * vp.Zoom);
    }

    /// <summary>编辑框允许的最大宽度（世界单位），与 <c>TextGeometry.DefaultMaxWidth</c> 保持一致。</summary>
    private const double TextEditMaxWidth = 900;

    /// <summary>提交编辑：测量 → 新建对象或修改已有对象（都是一条可撤销命令）。</summary>
    public void CommitTextEdit()
    {
        if (!_editingIsNew && _editingText is null) return;
        if (TextEditor.Visibility != Visibility.Visible) return;

        var page = _session.Document.CurrentPage;
        var text = TextEditor.Text ?? "";
        var size = _textTool.FontSize;
        var family = _textTool.FontFamily;

        var (w, h) = TextGeometry.Measure(text, size, family, _textTool.Bold, _textTool.Italic);

        var target = _editingText;
        var isNew = _editingIsNew;

        // 先收掉编辑框，避免下面的命令触发重绘时编辑框还浮在上面
        TextEditor.Visibility = Visibility.Collapsed;
        _editingText = null;
        _editingIsNew = false;

        if (string.IsNullOrWhiteSpace(text))
        {
            // 空文本：如果是在改已有文字，等于把这段文字删掉
            if (!isNew && target is not null)
            {
                Board.Commands.Execute(new RemoveObjectsCommand(page, [target]));
                Board.Selection.Remove(target);
                StatusText.Text = "文字已清空，该文本对象已删除（可撤销）";
            }
            else
            {
                StatusText.Text = "未输入内容，已放弃";
            }

            Board.Focus();
            return;
        }

        if (!isNew && target is not null)
        {
            Board.Commands.Execute(new SetTextCommand(page, target, text, w, h));
            StatusText.Text = $"已修改文字（{target.LineCount} 行，可撤销）";
        }
        else
        {
            var world = _pendingTextWorld;
            var obj = TextObject.FromWorldTopLeft(
                _session.Document.AllocateObjectId(), text, world, w, h,
                size, Board.PenColor, family, _textTool.Bold, _textTool.Italic);

            Board.Commands.Execute(new AddObjectCommand(page, obj));
            StatusText.Text = $"已添加文字：{obj.FirstLine}（可撤销）";
        }

        Board.InvalidateVisual();
        Board.Focus();
    }

    /// <summary>放弃编辑（Esc）。</summary>
    public void CancelTextEdit()
    {
        if (TextEditor.Visibility != Visibility.Visible) return;

        TextEditor.Visibility = Visibility.Collapsed;
        _editingText = null;
        _editingIsNew = false;
        StatusText.Text = "已取消文字编辑";
        Board.Focus();
    }

    /// <summary>是否正在编辑文字（<see cref="ITextEditHost.IsEditing"/>）。</summary>
    public bool IsEditing => TextEditor.Visibility == Visibility.Visible;

    private void OnTextEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelTextEdit();
            e.Handled = true;
            return;
        }

        // Ctrl+Enter 提交；单独 Enter 用于换行（多行标题/条目很常见）
        if (e.Key is Key.Enter or Key.Return && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            CommitTextEdit();
            e.Handled = true;
        }
    }

    /// <summary>点到别处就提交（点画布、切工具、点页面目录都会走到这里）。</summary>
    private void OnTextEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TextEditor.Visibility == Visibility.Visible) CommitTextEdit();
    }

    // ── 页面目录 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 重建整个页面目录（页数变化时调用）。
    /// 缩略图是**内容裁剪**的位图，因此一眼就能看出哪页写了什么。
    /// </summary>
    private void RebuildPageList()
    {
        var doc = _session.Document;
        PageList.Items.Clear();
        _thumbImages.Clear();
        _thumbFrames.Clear();

        for (var i = 0; i < doc.Pages.Count; i++)
        {
            var index = i;
            var page = doc.Pages[i];

            var image = new Image
            {
                Source = _thumbnails.Render(page),
                Stretch = Stretch.Uniform,
                Height = 96,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            var frame = new Border
            {
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                Padding = new Thickness(2),
                Margin = new Thickness(0, 0, 0, 8),
                CornerRadius = new CornerRadius(4),
                Child = new StackPanel
                {
                    Children =
                    {
                        image,
                        new TextBlock
                        {
                            Text = $"第 {i + 1} 页　{page.Count} 个对象",
                            Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0xB9, 0xB9)),
                            FontSize = 11,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 4, 0, 0)
                        }
                    }
                }
            };

            // 整块可点：切页
            frame.Cursor = Cursors.Hand;
            frame.ToolTip = "点击切到这一页；把选中的对象拖到这里可搬到这一页；上下拖动缩略图可调整页序";

            // ① 拖动缩略图 = 调整页序（在 MouseMove 里真正发起拖放；
            //    单纯点一下（没拖动）仍然走「切页」）
            var dragStart = new Point();
            var dragArmed = false;

            frame.PreviewMouseLeftButtonDown += (_, e) =>
            {
                dragStart = e.GetPosition(PageList);
                dragArmed = true;
            };
            frame.PreviewMouseLeftButtonUp += (_, _) => dragArmed = false;

            frame.PreviewMouseMove += (_, e) =>
            {
                if (!dragArmed || e.LeftButton != MouseButtonState.Pressed) return;

                // 4px 阈值：与选择工具的拖动阈值一致，避免手抖把"切页"变成"换页序"
                var p = e.GetPosition(PageList);
                if (Math.Abs(p.X - dragStart.X) < 4 && Math.Abs(p.Y - dragStart.Y) < 4) return;

                dragArmed = false;
                var data = new DataObject(PageReorderFormat, index);
                DragDrop.DoDragDrop(frame, data, DragDropEffects.Move);
            };

            // ② 放置目标：既接收"页序拖放"，也接收"跨页搬运对象"
            frame.AllowDrop = true;
            frame.DragOver += (_, e) =>
            {
                if (e.Data.GetDataPresent(PageReorderFormat))
                    e.Effects = DragDropEffects.Move;
                else
                    e.Effects = Board.Selection.IsEmpty ? DragDropEffects.None : DragDropEffects.Move;

                e.Handled = true;
            };

            frame.Drop += (_, e) =>
            {
                if (e.Data.GetDataPresent(PageReorderFormat))
                {
                    e.Handled = true;
                    var from = (int)(e.Data.GetData(PageReorderFormat) ?? -1);
                    if (from >= 0 && from != index) ReorderPages(from, index);
                    return;
                }

                DropSelectionOnPage(index, e);
            };

            // 点击切页：放在冒泡的 Up 上，拖放发起后不会再触发（DragDrop 会吃掉这次 Up）
            frame.MouseLeftButtonUp += (_, _) => Board.GoToPage(index);

            _thumbImages.Add(image);
            _thumbFrames.Add(frame);
            PageList.Items.Add(frame);
        }

        HighlightCurrentPage();
        UpdatePageButtons();
    }

    /// <summary>把当前页的缩略图帧描亮（其余恢复暗色）。</summary>
    private void HighlightCurrentPage()
    {
        var current = _session.Document.CurrentPageIndex;
        for (var i = 0; i < _thumbFrames.Count; i++)
        {
            _thumbFrames[i].BorderBrush = new SolidColorBrush(i == current
                ? Color.FromRgb(0xEE, 0xD8, 0x58)
                : Color.FromRgb(0x3A, 0x3A, 0x3A));
        }

        PageCountRun.Text = $"　{current + 1} / {_session.Document.Pages.Count}";
    }

    private void UpdatePageButtons()
    {
        var doc = _session.Document;
        BtnPageDel.IsEnabled = doc.Pages.Count > 1;
        BtnPagePrev.IsEnabled = doc.CurrentPageIndex > 0;
        BtnPageNext.IsEnabled = doc.CurrentPageIndex < doc.Pages.Count - 1;
        BtnPageMove.IsEnabled = !Board.Selection.IsEmpty && doc.Pages.Count > 1;
    }

    private void OnPageChanged()
    {
        HighlightCurrentPage();
        UpdatePageButtons();
        UpdateUndoButtons();

        var doc = _session.Document;
        StatusText.Text = $"已切到第 {doc.CurrentPageIndex + 1} / {doc.Pages.Count} 页" +
                          $"（本页 {doc.CurrentPage.Count} 个对象）";
    }

    /// <summary>内容变了 → 节流后刷新缩略图（书写过程中不要每笔都重渲染）。</summary>
    private void OnCanvasContentChanged()
    {
        _thumbnailTimer.Stop();
        _thumbnailTimer.Start();
    }

    private void OnThumbnailTick(object? sender, EventArgs e)
    {
        _thumbnailTimer.Stop();
        RefreshThumbnail(_session.Document.CurrentPageIndex);
        UpdatePageButtons();
    }

    /// <summary>只重渲染一页的缩略图（其余页没变，不必重画）。</summary>
    private void RefreshThumbnail(int index)
    {
        if (index < 0 || index >= _thumbImages.Count) return;

        var page = _session.Document.Pages[index];
        _thumbImages[index].Source = _thumbnails.Render(page);

        // 顺手更新"第 N 页　M 个对象"那行文字
        if (_thumbFrames[index].Child is StackPanel sp && sp.Children.Count > 1 &&
            sp.Children[1] is TextBlock label)
            label.Text = $"第 {index + 1} 页　{page.Count} 个对象";
    }

    private void OnPageNew(object s, RoutedEventArgs e) => RunPageCommand(new AddPageCommand(_session.Document));

    private void OnPageCopy(object s, RoutedEventArgs e)
        => RunPageCommand(new DuplicatePageCommand(_session.Document, _session.Document.CurrentPage));

    private void OnPageDelete(object s, RoutedEventArgs e)
    {
        var doc = _session.Document;
        if (doc.Pages.Count <= 1)
        {
            StatusText.Text = "只剩一页，不能再删（画板至少要有一页）";
            return;
        }

        var page = doc.CurrentPage;
        var answer = MessageBox.Show(this,
            $"确定删除第 {doc.CurrentPageIndex + 1} 页吗？\n\n" +
            $"这一页上有 {page.Count} 个对象。删除后可以用状态栏的「撤销页面操作」找回。",
            "白板 · 删除页面", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        RunPageCommand(new RemovePageCommand(doc, page));
    }

    /// <summary>
    /// 执行一条页面级命令，并在状态栏留一个「撤销」入口。
    ///
    /// 为什么不用 Ctrl+Z：Ctrl+Z 的语义是"撤销**当前页**上的操作"（ADR：每页独立撤销栈）。
    /// 把页面结构操作混进去，会出现"切到别的页之后，Ctrl+Z 撤销的是另一页的删除页动作"这种怪事。
    /// 所以页面操作单独给一个明确入口，撤销目标一目了然。
    /// </summary>
    private void RunPageCommand(CoreCommand command)
    {
        Board.CancelPending();
        command.Do();

        _lastPageCommand = command;

        // 命令可能自己改了当前页（新建/复制/删除都会）
        Board.Commands.SetCurrentPage(_session.Document.CurrentPage.Id);
        Board.ClearSelection();
        Board.InvalidateVisual();

        RebuildPageList();
        UpdateUndoButtons();
        UpdatePageButtons();

        var refused = command is RemovePageCommand { Refused: true };
        StatusText.Text = refused
            ? "只剩一页，不能再删（画板至少要有一页）"
            : $"已{command.Name}（第 {_session.Document.CurrentPageIndex + 1} / {_session.Document.Pages.Count} 页）";

        BtnUndoPageOp.Visibility = refused ? Visibility.Collapsed : Visibility.Visible;

        // 内容变了 → 存档标记 + 自动保存计时
        _session.MarkDirty();
    }

    private void OnUndoPageOp(object s, RoutedEventArgs e)
    {
        if (_lastPageCommand is null)
        {
            StatusText.Text = "没有可撤销的页面操作";
            return;
        }

        var name = _lastPageCommand.Name;
        _lastPageCommand.Undo();
        _lastPageCommand = null;
        BtnUndoPageOp.Visibility = Visibility.Collapsed;

        Board.Commands.SetCurrentPage(_session.Document.CurrentPage.Id);
        Board.ClearSelection();
        Board.InvalidateVisual();
        RebuildPageList();
        UpdateUndoButtons();

        StatusText.Text = $"已撤销「{name}」";
        _session.MarkDirty();
    }

    private void OnPagePrev(object s, RoutedEventArgs e) => Board.GoToPreviousPage();
    private void OnPageNext(object s, RoutedEventArgs e) => Board.GoToNextPage();

    /// <summary>「移到…」：把选中的对象搬到指定页（触屏友好，替代拖动）。</summary>
    private void OnPageMoveObjects(object s, RoutedEventArgs e)
    {
        if (Board.Selection.IsEmpty)
        {
            StatusText.Text = "先选几个对象，再点「移到…」";
            return;
        }

        var doc = _session.Document;
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
            Foreground = Brushes.White,
            PlacementTarget = BtnPageMove,
            Placement = PlacementMode.Bottom
        };

        for (var i = 0; i < doc.Pages.Count; i++)
        {
            var index = i;
            var isCurrent = i == doc.CurrentPageIndex;
            var item = new MenuItem
            {
                Header = isCurrent ? $"第 {i + 1} 页（当前页）" : $"移到第 {i + 1} 页",
                IsEnabled = !isCurrent,
                Foreground = isCurrent ? Brushes.Gray : Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24))
            };
            item.Click += (_, _) => MoveSelectionToPage(index);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void MoveSelectionToPage(int index)
    {
        var n = Board.MoveSelectionToPage(index);
        if (n == 0)
        {
            StatusText.Text = "没有可搬运的对象（或目标就是当前页）";
            return;
        }

        RefreshThumbnail(_session.Document.CurrentPageIndex);
        RefreshThumbnail(index);
        UpdatePageButtons();
        StatusText.Text = $"已把 {n} 个对象搬到第 {index + 1} 页（可撤销）";
    }

    /// <summary>
    /// 画布把选中的对象拖出界了（拖到页面目录区域）→ 判断落在哪个缩略图上并搬运。
    /// 返回 true 表示已经处理（画布不再自行处理这次拖动）。
    /// </summary>
    private bool OnSelectionDraggedOut(Point screenPoint)
    {
        if (Board.Selection.IsEmpty) return false;

        for (var i = 0; i < _thumbFrames.Count; i++)
        {
            var frame = _thumbFrames[i];
            if (!frame.IsVisible) continue;

            var topLeft = frame.PointToScreen(new Point(0, 0));
            var rect = new Rect(topLeft, new Size(frame.ActualWidth, frame.ActualHeight));
            if (!rect.Contains(screenPoint)) continue;

            MoveSelectionToPage(i);
            return true;
        }

        return false;
    }

    private void DropSelectionOnPage(int index, DragEventArgs e)
    {
        e.Handled = true;

        if (Board.Selection.IsEmpty) return;
        if (index == _session.Document.CurrentPageIndex) return;

        MoveSelectionToPage(index);
    }

    /// <summary>页序拖放用的数据格式（与"跨页搬运对象"区分开）。</summary>
    private const string PageReorderFormat = "WhiteBoard.PageReorder";

    /// <summary>
    /// 调整页序：把第 <paramref name="from"/> 页移动到第 <paramref name="to"/> 页的位置。
    ///
    /// 用与其它页面操作一样的「撤销页面操作」入口，不占用 Ctrl+Z
    /// （Ctrl+Z 的语义是"撤销当前页上的内容改动"）。
    /// </summary>
    private void ReorderPages(int from, int to)
    {
        var doc = _session.Document;
        if (from < 0 || from >= doc.Pages.Count) return;

        var page = doc.Pages[from];
        var cmd = new MovePageCommand(doc, page, to);
        RunPageCommand(cmd);

        StatusText.Text = $"已把第 {from + 1} 页移到第 {to + 1} 位" +
                          $"（现在共 {doc.Pages.Count} 页，可撤销）";
    }

    // ── 选择与选中对象的操作 ──────────────────────────────────────────────

    /// <summary>多选开关：触屏没有 Ctrl 键，所以必须给一个"按一下就常开"的替代。</summary>
    private void OnToggleMultiSelect(object s, RoutedEventArgs e)
    {
        _selectTool.Additive = !_selectTool.Additive;
        UpdateToolButtons();
        StatusText.Text = _selectTool.Additive
            ? "多选：开（再点对象则加入/移出选中集合）"
            : "多选：关";
    }

    /// <summary>框选 / 圈选切换（决策 D2：触屏上圈选更好按）。</summary>
    private void OnToggleLasso(object s, RoutedEventArgs e)
    {
        _selectTool.Mode = _selectTool.Mode == SelectionMode.Lasso
            ? SelectionMode.Rectangle
            : SelectionMode.Lasso;

        UpdateToolButtons();
        StatusText.Text = _selectTool.Mode == SelectionMode.Lasso
            ? "框选方式：圈选（沿手指轨迹围一圈）"
            : "框选方式：矩形";
    }

    private void OnSelectAll(object s, RoutedEventArgs e)
    {
        Board.SelectAll();
        StatusText.Text = Board.Selection.IsEmpty
            ? "本页没有可选中的对象"
            : $"已全选 {Board.Selection.Count} 个对象";
    }

    private void OnDeleteSelection(object s, RoutedEventArgs e) => DeleteSelection();

    private void DeleteSelection()
    {
        var n = Board.Selection.Count;
        if (!Board.DeleteSelection())
        {
            StatusText.Text = "没有选中任何对象（先用「选择」工具点一下对象）";
            return;
        }

        UpdateUndoButtons();
        StatusText.Text = $"已删除 {n} 个对象（可撤销）";
    }

    private void OnBringToFront(object s, RoutedEventArgs e) => ChangeZOrder(true);
    private void OnSendToBack(object s, RoutedEventArgs e) => ChangeZOrder(false);

    /// <summary>
    /// 克隆选中的对象：偏移一点复制一份（命令内部已做 16 单位偏移；
    /// 不偏移的话新旧完全重叠，用户会以为没反应），并把选中切到新对象上，方便接着拖动。
    /// </summary>
    private void OnCloneSelection(object s, RoutedEventArgs e)
    {
        var page = _session.Document.CurrentPage;
        if (Board.Selection.IsEmpty)
        {
            StatusText.Text = "没有选中任何对象（先用「选择」点一下对象）";
            return;
        }

        Board.CancelPending();

        var sources = Board.Selection.Objects.ToList();
        var cmd = new CloneCommand(page, sources, () => _session.Document.AllocateObjectId());
        Board.Commands.Execute(cmd);

        Board.Selection.SetMany(cmd.Clones);
        Board.InvalidateVisual();
        StatusText.Text = $"已克隆 {sources.Count} 个对象（选中已切到副本，可撤销）";
    }

    /// <summary>
    /// 把工具栏当前笔宽应用到**选中的笔迹**上。
    /// 只对自由笔迹开放：<see cref="FreehandObject.PenWidth"/> 参与几何指纹，
    /// 改完缓存会自动失效重建；直线/矩形/椭圆的粗细是它们局部几何的一部分，S2 再开放。
    /// </summary>
    private void OnApplyPenWidthToSelection(object s, RoutedEventArgs e)
    {
        var strokes = Board.Selection.Objects.OfType<FreehandObject>().ToList();

        if (strokes.Count == 0)
        {
            StatusText.Text = "请先选中**笔迹**（直线/矩形/椭圆的粗细由它们自身的形状决定，暂不支持改）";
            return;
        }

        Board.CancelPending();

        Board.Commands.Execute(new SetPenWidthCommand(strokes, Board.PenWidth));
        Board.InvalidateVisual();
        StatusText.Text = $"已把 {strokes.Count} 条笔迹的笔宽改为 {Board.PenWidth:0.#}（可撤销）";
    }

    private void ChangeZOrder(bool toFront)
    {
        if (Board.Selection.IsEmpty)
        {
            StatusText.Text = "没有选中任何对象";
            return;
        }

        var n = Board.Selection.Count;
        Board.ChangeSelectionZOrder(toFront);
        StatusText.Text = $"已{(toFront ? "置顶" : "置底")} {n} 个对象（可撤销）";
    }

    private void OnSelectionChanged()
    {
        UpdateSelectionButtons();
        if (!Board.Selection.IsEmpty)
            StatusText.Text = $"已选中 {Board.Selection.Count} 个对象" +
                              "　│　拖动可移动，Del 删除，Ctrl+A 全选，Esc 取消选择";
    }

    private void UpdateSelectionButtons()
    {
        var has = !Board.Selection.IsEmpty;
        BtnDelete.IsEnabled = has;
        BtnFront.IsEnabled = has;
        BtnBack.IsEnabled = has;
        Highlight(BtnMultiSelect, _selectTool.Additive);
        Highlight(BtnLasso, _selectTool.Mode == SelectionMode.Lasso);
    }

    private void UpdateToolButtons()
    {
        var current = Board.Tool;
        Highlight(BtnSelect, ReferenceEquals(current, _selectTool));
        Highlight(BtnText, ReferenceEquals(current, _textTool));
        Highlight(BtnPen, ReferenceEquals(current, _penTool));
        Highlight(BtnLine, ReferenceEquals(current, _lineTool));
        Highlight(BtnRect, ReferenceEquals(current, _rectTool));
        Highlight(BtnEllipse, ReferenceEquals(current, _ellipseTool));
        Highlight(BtnEraser, ReferenceEquals(current, _eraserTool));

        Highlight(BtnW3, Math.Abs(Board.PenWidth - 3) < 0.001);
        Highlight(BtnW6, Math.Abs(Board.PenWidth - 6) < 0.001);
        Highlight(BtnW10, Math.Abs(Board.PenWidth - 10) < 0.001);

        Highlight(BtnFontS, Math.Abs(_textTool.FontSize - TextObject.SmallFontSize) < 0.001);
        Highlight(BtnFontM, Math.Abs(_textTool.FontSize - TextObject.MediumFontSize) < 0.001);
        Highlight(BtnFontL, Math.Abs(_textTool.FontSize - TextObject.LargeFontSize) < 0.001);
        Highlight(BtnFontB, _textTool.Bold);

        UpdateSelectionButtons();
    }

    private static void Highlight(Button b, bool on)
    {
        b.Background = on ? new SolidColorBrush(Color.FromRgb(0x3E, 0x6E, 0x4E)) : Brushes.Transparent;
        b.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF0));
        b.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        b.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
    }

    private void BuildColorPalette()
    {
        ColorList.Items.Clear();
        foreach (var c in _theme.Preset.Colors)
        {
            var hex = c.Value;
            var btn = new Button
            {
                Width = 30,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 0),
                Background = ToBrush(hex),
                ToolTip = $"{c.Name} {hex}",
                Tag = hex,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                BorderThickness = new Thickness(1)
            };
            btn.Click += (_, _) =>
            {
                Board.PenColor = hex;
                Board.Focus();
            };
            ColorList.Items.Add(btn);
        }
    }

    private void OnWidth3(object s, RoutedEventArgs e) => SetWidth(3);
    private void OnWidth6(object s, RoutedEventArgs e) => SetWidth(6);
    private void OnWidth10(object s, RoutedEventArgs e) => SetWidth(10);

    private void SetWidth(double w)
    {
        Board.PenWidth = w;
        UpdateToolButtons();
        Board.Focus();
    }

    // ── 文字字号与粗体 ────────────────────────────────────────────────────

    private void OnFontSmall(object s, RoutedEventArgs e) => SetFontSize(TextObject.SmallFontSize);
    private void OnFontMedium(object s, RoutedEventArgs e) => SetFontSize(TextObject.MediumFontSize);
    private void OnFontLarge(object s, RoutedEventArgs e) => SetFontSize(TextObject.LargeFontSize);

    private void SetFontSize(double size)
    {
        CommitTextEdit();

        _textTool.FontSize = size;

        // 如果在文本工具下且选中了文本对象，就顺手把选中的文本改成这个字号（可撤销）
        ApplyStyleToSelectedText();

        UpdateToolButtons();
        StatusText.Text = $"字号：{size:0.#}（世界单位，随缩放等比放大）";
        Board.Focus();
    }

    private void OnToggleBold(object s, RoutedEventArgs e)
    {
        CommitTextEdit();
        _textTool.Bold = !_textTool.Bold;
        ApplyStyleToSelectedText();
        UpdateToolButtons();
        StatusText.Text = _textTool.Bold ? "粗体：开" : "粗体：关";
        Board.Focus();
    }

    /// <summary>把当前字号/粗体应用到选中的文本对象（一条可撤销命令，多个对象合并为一条）。</summary>
    private void ApplyStyleToSelectedText()
    {
        var page = _session.Document.CurrentPage;
        var targets = Board.Selection.Objects.OfType<TextObject>().ToList();
        if (targets.Count == 0) return;

        var parts = new List<CoreCommand>();
        foreach (var t in targets)
        {
            var (w, h) = TextGeometry.Measure(t.Text, _textTool.FontSize, t.FontFamily, _textTool.Bold, t.Italic);
            var cmd = new SetTextStyleCommand(t, _textTool.FontSize, _textTool.Bold, _textTool.Italic, w, h);
            cmd.Do();
            parts.Add(cmd);
        }

        Board.Commands.PushTransaction("调整文字样式", parts);
        Board.InvalidateVisual();
        StatusText.Text = $"已把 {targets.Count} 段文字改为 {_textTool.FontSize:0.#} 号{(_textTool.Bold ? "粗体" : "")}（可撤销）";
    }

    // ── 编辑 ──────────────────────────────────────────────────────────────

    private void OnUndo(object s, RoutedEventArgs e) => DoUndo();
    private void OnRedo(object s, RoutedEventArgs e) => DoRedo();

    private void DoUndo()
    {
        CommitTextEdit();

        Board.Undo();
        UpdateUndoButtons();
    }

    private void DoRedo()
    {
        Board.Redo();
        UpdateUndoButtons();
    }

    private void OnClear(object s, RoutedEventArgs e)
    {
        var page = _session.Document.CurrentPage;
        if (page.Count == 0)
        {
            StatusText.Text = "当前页已是空白";
            return;
        }

        Board.CancelPending();
        Board.Commands.Execute(new ClearPageCommand(page));
        Board.InvalidateVisual();
        UpdateUndoButtons();
        StatusText.Text = $"已清空当前页（可撤销）；对象数 {page.Count}";
    }

    private void UpdateUndoButtons()
    {
        BtnUndo.IsEnabled = Board.Commands.CanUndo;
        BtnRedo.IsEnabled = Board.Commands.CanRedo;
    }

    // ── 视图 ──────────────────────────────────────────────────────────────

    private void OnZoomIn(object s, RoutedEventArgs e) => Board.ZoomBy(1.25);
    private void OnZoomOut(object s, RoutedEventArgs e) => Board.ZoomBy(1 / 1.25);
    private void OnResetView(object s, RoutedEventArgs e) => Board.ResetView();

    private void OnToggleFullScreen(object s, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;

        if (_fullScreen)
        {
            _preFullScreenState = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            StatusText.Text = "已进入全屏（再按 F11 或 Esc 退出）";
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _preFullScreenState;
            StatusText.Text = "已退出全屏";
        }

        Board.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Esc：全屏时退出全屏；否则取消选择（"Esc 一下回到干净状态"是通用肌肉记忆）
        if (e.Key == Key.Escape)
        {
            if (_fullScreen)
            {
                ToggleFullScreen();
            }
            else if (!Board.Selection.IsEmpty)
            {
                Board.ClearSelection();
                StatusText.Text = "已取消选择";
            }

            e.Handled = true;
            return;
        }

        if (HandleBareKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    // ── 状态栏 ────────────────────────────────────────────────────────────

    private void OnBoardStatus(string text)
    {
        var f = Board.LastFrame;
        StatusText.Text = $"{text}　│　本页对象 {_session.Document.CurrentPage.Count}　│　" +
                          $"第 {_session.Document.CurrentPageIndex + 1}/{_session.Document.Pages.Count} 页　│　" +
                          $"帧：{f}";
    }

    private void ShowEnvironment()
    {
        DataDirText.Text = $"数据目录：{_paths.DataRoot}" +
                           (_paths.IsWritable ? "（可写）" : "　⚠️ 不可写，已进入只读模式：自动保存已关闭");
    }

    private void ShowTheme()
    {
        var preset = _theme.Preset;
        var source = _theme.Source switch
        {
            ThemeSource.EmbeddedDefault => "内置默认（data\\theme\\color.txt 尚未创建）",
            ThemeSource.UserFile => "用户文件 data\\theme\\color.txt",
            _ => "内置默认（用户文件解析失败已回退）"
        };

        ThemeText.Text = $"主题：{preset.Name}（{preset.Id}）　背景 {preset.Background}　来源：{source}";

        if (!string.IsNullOrWhiteSpace(_theme.Warning))
        {
            WarnText.Text = "⚠️ " + _theme.Warning;
            WarnText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 载入示例画板（<c>--demo</c>）：波形笔迹 + 矩形/椭圆/直线 + 被橡皮擦断的一段 + 第 2 页。
    /// 内容是用**生产链路**合成的（合成指针事件 → 工具 → 命令），因此看到的就是真实绘制结果。
    /// </summary>
    private void LoadDemoBoard()
    {
        CommitTextEdit();

        var demo = DemoBoard.Build((int)Math.Max(800, SystemParameters.WorkArea.Width) - 40, 800);

        _session.Adopt(demo.Document, null);
        Board.Document = demo.Document;
        Board.Commands.SetCurrentPage(demo.Document.CurrentPage.Id);
        UpdateUndoButtons();
        UpdateTitle();
        RebuildPageList();

        StatusText.Text = $"已载入示例画板：{demo.Document.Pages.Count} 页，" +
                          $"第 1 页 {demo.Document.Pages[0].Count} 个对象" +
                          $"（笔迹 {demo.FreehandCount}、形状 {demo.ShapeCount}、其中 1 条被橡皮擦断）";

        // 验收选中框：预先选中椭圆与被擦断的那条笔迹
        if (App.IsDemoSelectRequested)
        {
            var page = demo.Document.Pages[0];
            var picked = page.Objects
                .Where(o => o.Kind is "ellipse" or "freehand")
                .OrderByDescending(o => o.Kind == "ellipse")
                .ThenByDescending(o => o is FreehandObject f ? f.Points.Count : 0)
                .Take(2)
                .ToList();

            if (picked.Count > 0)
            {
                SelectTool(_selectTool);
                Board.Selection.SetMany(picked);
                StatusText.Text += $"　│　已选中 {picked.Count} 个对象（选中框示例）";
            }
        }
    }

    private static Color ToColor(string hex)
    {
        var (a, r, g, b) = ColorMath.ParseHex(hex);
        return Color.FromArgb(a, r, g, b);
    }

    private static SolidColorBrush ToBrush(string hex) => new(ToColor(hex));

    /// <summary>把无参方法包成 ICommand（用于 InputBindings 快捷键）。</summary>
    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _action;
        public RelayCommand(Action action) => _action = action;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
    }
}
