using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using FlingTrainerX.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;
using FlingTrainerX.Models;
using FlingTrainerX.Services;
using FlingTrainerX.Views;

namespace FlingTrainerX;

public partial class MainWindow : Window
{
    private enum ViewMode { Library, Favorites, Queue, Settings }
    private enum LibraryTab { Recent, Popular }

    /// <summary>
    /// 固定版心：窗口默认 1280×780，右栏可用约 1018 → 版心 1020，一行 5 张、每张 191；
    /// 每页 15 条正好 3 行铺满一屏（不出滚动条）。窗口更宽时版心最多放到 1460，
    /// 卡片能长多大由「3 行是否还装得下」决定（见 <see cref="UpdateCardMetrics"/>）。
    /// </summary>
    private const double MaxContentWidth = 1460;

    /// <summary>版心左右各留的边距（和 XAML 里 CardArea 的 Margin 保持一致）。</summary>
    private const double ContentSideMargin = 26;

    /// <summary>左侧栏宽度 + 外框描边，用来从窗口宽度反推右栏可用宽度。</summary>
    private const double SidebarWidth = 210;

    /// <summary>卡片宽度的上下限：1080 屏也不会挤成一团，满载也就 280，不会变成大图。</summary>
    private const double MinCardWidth = 130;
    private const double MaxCardWidth = 280;

    /// <summary>卡片之间留的空隙（像素）。</summary>
    private const double CardGap = 12;

    /// <summary>列表右侧给滚动条预留的宽度，和 CardListBox 的 Padding 保持一致。</summary>
    private const double CardListGutter = 6;

    /// <summary>行与行之间的空隙，和 DataTemplate 里那个 ItemsControl 的 Margin 保持一致。</summary>
    private const double CardRowGap = 12;

    /// <summary>
    /// 卡片下半部的标题区高度：两行标题 + 一行「更新时间 / 选项数」标签。
    /// 封面高度 = 卡宽 × 9/16，正好和官网头图同比例，不裁不变形。
    /// </summary>
    private const double CardInfoHeight = 84;

    /// <summary>窗口四角圆角半径（DWM 原生圆角 + 内容裁剪用同一套数值）。</summary>
    private const double ShellCornerRadius = 12;

    /// <summary>窗口能被拖到的最小尺寸；再小布局就挤了（版心按 1080 宽屏设计）。</summary>
    private const double MinWindowWidth = 1080;
    private const double MinWindowHeight = 680;

    private TrainerLibrary _library = new();

    /// <summary>当前视图实际展示的数据（翻页会往后追加）。</summary>
    private readonly List<TrainerItem> _items = new();

    /// <summary>虚拟滚动的最小单位：一行 5 张卡片。</summary>
    private readonly ObservableCollection<CardRow> _rows = new();

    /// <summary>收藏列表，按收藏时间倒序（来自 SQLite）。</summary>
    private readonly List<TrainerItem> _favorites = new();

    private ViewMode _mode = ViewMode.Library;
    private LibraryTab _libraryTab = LibraryTab.Recent;
    private bool _detailOpen;

    // 详情页开关动画：和「关于」弹窗同一套观感（淡入 + 轻微放大回弹），只是面板大、
    // 所以起始缩放收到 0.96、回弹幅度收到 0.35，避免 1000 宽的面板晃得夸张。
    private const double DetailScaleFrom = 0.96;
    private static readonly TimeSpan DetailOpenTime = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DetailCloseTime = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan DetailBackdropTime = TimeSpan.FromMilliseconds(240);

    /// <summary>每次开关自增，用来识别「关闭动画播到一半又被重新打开」的情况。</summary>
    private int _detailAnimToken;
    private bool _busy;

    /// <summary>正在同步官网数据：同一时间只允许一个同步任务。</summary>
    private bool _syncRunning;

    /// <summary>同步任务的取消源，取消按钮要用。</summary>
    private CancellationTokenSource? _syncCts;

    /// <summary>
    /// 上下分页的状态：当前页、官网总数、每页内容缓存。
    /// 游戏库和搜索结果各一份，退出搜索后游戏库的页码进度不会被打乱。
    /// </summary>
    private sealed class Pager
    {
        public int Page = 1;
        public int TotalPages = 1;
        public int TotalCount;                                    // 官网 X-WP-Total，0 = 还没拿到
        public bool Loading;
        public bool Failed;

        /// <summary>上次想跳去但还没加载成功的页码（失败重试的目标）。</summary>
        public int PendingPage;

        public readonly Dictionary<int, List<TrainerItem>> Cache = new();

        public void Reset()
        {
            Page = 1;
            TotalPages = 1;
            TotalCount = 0;
            Loading = false;
            Failed = false;
            PendingPage = 0;
            Cache.Clear();
        }
    }

    private readonly Pager _libraryPager = new();

    private readonly Pager _searchPager = new();

    /// <summary>当前生效的分页状态：搜索时用搜索那份，否则用游戏库那份。</summary>
    private Pager CurrentPager => Searching ? _searchPager : _libraryPager;

    /// <summary>库的换代序号：清缓存 / 手动同步时自增，晚到的旧页响应直接丢掉。</summary>
    private int _librarySeq;

    /// <summary>当前生效的官网搜索词；空串表示没在搜索，列表回到正常游戏库。</summary>
    private string _keyword = "";

    private bool _searching;
    private string? _searchError;

    /// <summary>连续搜索时用它做序号，晚到的旧响应直接丢掉。</summary>
    private int _searchSeq;

    /// <summary>首屏正在从官网拉取（无本地数据，所以启动时一定是这个状态）。</summary>
    private bool _firstLoading = true;
    private string? _firstError;

    private double _cardW;
    private double _cardH;

    /// <summary>上一次算卡片尺寸时，是不是「一页 15 条装不下、要滚动」的状态。</summary>
    private bool _cardScrollable;

    // ==================== 搜索智能提示（本地游戏名库） ====================

    /// <summary>打字停下来 160ms 再查库，避免每敲一个字都打一次 SQL。</summary>
    private readonly DispatcherTimer _suggestTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };

    private List<GameNameRow> _suggests = new();

    /// <summary>从提示里选了一条、正在回填搜索框：这时别再触发一次提示查询。</summary>
    private bool _applyingSuggest;

    // ==================== 游戏名库同步（官网全量 + Steam 中文名） ====================

    private CancellationTokenSource? _nameSyncCts;
    private bool _nameSyncRunning;

    private ScrollViewer? _listScroll;
    private readonly WinForms.NotifyIcon _trayIcon;
    private readonly TrayMenuWindow _trayMenu;
    private readonly Drawing.Icon _trayIconImage;
    private bool _exitRequested;

    /// <summary>缩进托盘之前的窗口状态（普通 / 最大化），从托盘回来时按它还原。</summary>
    private WindowState _stateBeforeTray = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();

        // 设置页「免责声明」卡片上显示的版本号
        VersionBadge.Text = $"v{AppVersion}";

        var executablePath = Environment.ProcessPath;
        _trayIconImage = !string.IsNullOrWhiteSpace(executablePath)
            ? Drawing.Icon.ExtractAssociatedIcon(executablePath) ?? (Drawing.Icon)Drawing.SystemIcons.Application.Clone()
            : (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        _trayMenu = new TrayMenuWindow();
        _trayMenu.OpenRequested += (_, _) => ShowMainWindow();
        _trayMenu.ExitRequested += (_, _) => ExitFromTray();

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "风灵月影宗储物袋",
            Visible = true
        };
        _trayIcon.MouseUp += (_, e) =>
        {
            // 右键 → 小菜单（打开主界面 / 退出程序）；左键 → 直接把主界面叫回来
            if (e.Button == WinForms.MouseButtons.Right)
                Dispatcher.BeginInvoke(new Action(() => _trayMenu.ShowAtCursor()));
            else if (e.Button == WinForms.MouseButtons.Left)
                Dispatcher.BeginInvoke(new Action(ShowMainWindow));
        };

        // 不再读取任何本地数据（无内置种子、无列表缓存），启动后立即向官网请求
        _library = new TrainerLibrary();
        TrainerDataService.CurrentLibrary = _library;
        _favorites.AddRange(TrainerDataService.LoadFavorites(_library));

        CardList.ItemsSource = _rows;
        QueueList.ItemsSource = DownloadManager.Instance.Tasks;
        NavList.SelectedIndex = 0;

        // 列表可视高度是「3 行装不装得下」的依据，但它要等排版跑完才有值：
        // 挂一次 SizeChanged，让卡片尺寸在拿到真实视口后再收敛一次，
        // 免得启动时按「视口 0 → 不压高度」算出个偏大的卡片，之后一直不复原
        CardList.SizeChanged += (_, _) => UpdateCardMetrics();

        DownloadManager.Instance.Changed += (_, _) => OnDownloadsChanged();

        _suggestTimer.Tick += SuggestTimer_Tick;

        Loaded += OnLoaded;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        SizeChanged += OnSizeChanged;
        StateChanged += OnWindowStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        Deactivated += OnWindowDeactivated;
    }

    /// <summary>自检时才有的调试日志：记录提示弹窗是谁关掉的，正式运行不会写。</summary>
#if DEBUG
    private string? _selfCheckLog;
#endif

    [System.Diagnostics.Conditional("DEBUG")]
    private void CheckLog(string text)
    {
#if DEBUG
        if (_selfCheckLog is null) return;

        try { File.AppendAllText(_selfCheckLog, $"{DateTime.Now:HH:mm:ss.fff} {text}\n"); }
        catch { /* 自检日志写不进去也不影响流程 */ }
#endif
    }

    /// <summary>
    /// 提示列表改成 StaysOpen=True 之后，点窗口别处要自己收。
    /// 两种点击要放行：搜索框（继续输入）和提示列表本身（要选中那一条）。
    /// 注意 Popup 的点击也会走到这里 —— 它的内容虽然挂在独立窗口上，输入路由还是并进来的，
    /// 这里要是不放行，就会先把列表清空，后面的 ListBox 只能拿到一个 null。
    /// </summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!SuggestPopup.IsOpen) return;

        if (e.OriginalSource is DependencyObject src)
        {
            if (SearchShell.IsAncestorOf(src)) return;

            if (SuggestPopup.Child is Visual popupRoot &&
                src is Visual visual &&
                (ReferenceEquals(popupRoot, visual) || popupRoot.IsAncestorOf(visual)))
            {
                return;
            }
        }

        CheckLog("OnPreviewMouseDown → CloseSuggest");
        CloseSuggest();
    }

    /// <summary>
    /// 切到别的程序时把提示收掉。Popup 是独立窗口，点它的那一瞬间主窗口也会收到一次 Deactivated，
    /// 所以不能立刻关 —— 等一拍再看前台到底是谁：还是自己家的（主窗口或 Popup 窗口）就不动。
    /// </summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (!SuggestPopup.IsOpen) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!SuggestPopup.IsOpen) return;

            var foreground = GetForegroundWindow();
            var main = new WindowInteropHelper(this).Handle;

            var popup = IntPtr.Zero;
            if (SuggestPopup.Child is FrameworkElement child &&
                PresentationSource.FromVisual(child) is HwndSource source)
            {
                popup = source.Handle;
            }

            if (foreground == main || (popup != IntPtr.Zero && foreground == popup)) return;

            CheckLog($"Deactivated → CloseSuggest（前台 {foreground}，主窗口 {main}，Popup {popup}）");
            CloseSuggest();
        }));
    }

    /// <summary>
    /// 高 DPI 适配（PerMonitorV2）：按当前显示器的工作区（物理像素 ÷ 当前 DPI 缩放）算窗口大小，
    /// 100%、150%、200% 混合缩放下窗口都不会超出屏幕。显示器切换（DpiChanged）时重新算一次。
    /// </summary>
    private void ApplyWindowBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        // 用窗口自己的 DPI（而不是视觉树的），跨屏移动时它和 MonitorFromWindow 才是同一套
        var scale = GetDpiForWindow(hwnd) / 96d;
        if (scale <= 0) scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        var workWidth = (info.Work.Right - info.Work.Left) / scale;
        var workHeight = (info.Work.Bottom - info.Work.Top) / scale;
        if (workWidth < 320 || workHeight < 240) return;

        var width = Math.Min(1280, Math.Max(900, workWidth - 32));
        var height = Math.Min(780, Math.Max(520, workHeight - 32));

        // 窗口是固定尺寸，只有工作区放不下时才缩到放得下（1080p + 150% 缩放这类场景）
        // 最大化时 WindowChrome 会把窗口向外撑出一个边框宽度（左上角跑到 -7,-7），
        // 上限如果正好卡在工作区大小，撑出来的部分会被截掉，右边和下边就会漏出一条桌面缝隙
        MaxWidth = workWidth + 40;
        MaxHeight = workHeight + 40;
        MinWidth = Math.Min(MinWindowWidth, width);
        MinHeight = Math.Min(MinWindowHeight, height);
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
    }

    private const uint MonitorDefaultToNearest = 2;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE：Windows 11 起支持的窗口圆角开关。</summary>
    private const int DwmWindowCornerPreference = 33;

    /// <summary>DWMWCP_ROUND：系统默认圆角半径。</summary>
    private const int DwmWindowCornerRound = 2;

    /// <summary>DWMWCP_DONOTROUND：不要圆角（最大化时用，四角要贴屏幕边）。</summary>
    private const int DwmWindowCornerDoNotRound = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    private const int WmGetMinMaxInfo = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X, Y;
    }

    /// <summary>WM_GETMINMAXINFO 的载荷，只用到最大化尺寸、位置和最小拖动尺寸。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point32 Reserved;
        public Point32 MaxSize;
        public Point32 MaxPosition;
        public Point32 MinTrackSize;
        public Point32 MaxTrackSize;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyRoundedCorners(WindowState != WindowState.Maximized);
        ApplyWindowBounds();
        UpdateShellClip();

        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            ApplyMaximizedBounds(hwnd, lParam, ref handled);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 自己算最大化矩形。WindowChrome 会把窗口往外撑一圈隐形边框（本机 96 DPI 下是 7px），
    /// 不补这一圈的话，内容要么到不了屏幕边（露出桌面缝隙），要么伸到任务栏底下被裁掉。
    /// </summary>
    private void ApplyMaximizedBounds(IntPtr hwnd, IntPtr lParam, ref bool handled)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        // 隐形边框宽度 = (窗口宽 - 客户区宽) / 2，直接用当前窗口实测，换 DPI 也不用改常数
        var insetX = 0;
        var insetY = 0;
        if (GetWindowRect(hwnd, out var win) && GetClientRect(hwnd, out var client) && client.Right > 0)
        {
            insetX = Math.Max(0, ((win.Right - win.Left) - client.Right) / 2);
            insetY = Math.Max(0, ((win.Bottom - win.Top) - client.Bottom) / 2);
        }

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        // 最大化后内容区正好等于工作区（任务栏以上），外面那圈隐形边框跑到屏幕外
        mmi.MaxPosition.X = info.Work.Left - info.Monitor.Left - insetX;
        mmi.MaxPosition.Y = info.Work.Top - info.Monitor.Top - insetY;
        mmi.MaxSize.X = (info.Work.Right - info.Work.Left) + insetX * 2;
        mmi.MaxSize.Y = (info.Work.Bottom - info.Work.Top) + insetY * 2;

        // 接管了这条消息就要自己守住最小尺寸
        mmi.MinTrackSize.X = (int)Math.Round(MinWidth * scale);
        mmi.MinTrackSize.Y = (int)Math.Round(MinHeight * scale);

        // 拖拽调整大小时系统送来的上限可能是 0，不补一个合理值窗口会被压到最小尺寸
        var maxTrackX = (info.Monitor.Right - info.Monitor.Left) + insetX * 2;
        var maxTrackY = (info.Monitor.Bottom - info.Monitor.Top) + insetY * 2;
        if (mmi.MaxTrackSize.X < mmi.MinTrackSize.X) mmi.MaxTrackSize.X = maxTrackX;
        if (mmi.MaxTrackSize.Y < mmi.MinTrackSize.Y) mmi.MaxTrackSize.Y = maxTrackY;

        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
    }

    /// <summary>
    /// 让窗口四角变圆。优先用 Windows 11 的 DWM 原生圆角（自带抗锯齿和阴影），
    /// 系统不支持时退回自己裁剪内容区域。最大化时反过来要求直角。
    /// </summary>
    private void ApplyRoundedCorners(bool rounded)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var preference = rounded ? DwmWindowCornerRound : DwmWindowCornerDoNotRound;
            DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // 老系统没有这个 API，靠 UpdateShellClip() 的裁剪兜底
        }
    }

    /// <summary>内容跟着外框一起裁圆角，侧栏和图片不会溢出到圆角外面。</summary>
    private void UpdateShellClip()
    {
        if (Shell.ActualWidth <= 0 || Shell.ActualHeight <= 0) return;

        // 最大化时窗口铺满工作区，四角是直角
        var radius = WindowState == WindowState.Maximized ? 0 : ShellCornerRadius;

        Shell.Clip = new RectangleGeometry(
            new Rect(0, 0, Shell.ActualWidth, Shell.ActualHeight), radius, radius);
    }

    /// <summary>窗口尺寸固定，状态变化只处理圆角与描边（缩进托盘 / 恢复时保持圆角）。</summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var maxed = WindowState == WindowState.Maximized;

        // 铺满屏幕时圆角和描边都要收掉，否则屏幕边缘会露出一圈亮线
        Shell.CornerRadius = new CornerRadius(maxed ? 0 : ShellCornerRadius);
        Shell.BorderThickness = new Thickness(maxed ? 0 : 1);

        ApplyRoundedCorners(!maxed);
        UpdateShellClip();
    }

    /// <summary>窗口被拖到另一块缩放比例不同的显示器上时，重新按新显示器收一次尺寸。</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // 消息到达时窗口位置可能还在切换途中，等这一轮布局走完再按新显示器算，
        // 否则会拿「新 DPI + 旧显示器」算出一个偏小的尺寸
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            ApplyWindowBounds();

            if (_detailOpen && DetailHost.Content is TrainerDetailView view)
                view.FitTo(RootGrid.ActualWidth, RootGrid.ActualHeight);
        }));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateCardMetrics();

        // 首页只读本地 SQLite：启动完全不碰网络，界面立刻就有内容。
        // 想要官网新数据走「设置 → 数据更新」里的两个同步按钮。
        LoadCachedCards();

        Render();
        UpdateStats();
        UpdateQueueBadge();

        // 游戏名库为空就顺手抓一份（有数据直接跳过），搜索框的智能提示靠它
        _ = SeedGameNamesAsync();
    }

    /// <summary>
    /// 把一页的新数据并进内存库：同 ID 的用新对象**替换**（把本地路径搬过去），新的追加。
    /// 换掉旧对象是为了让收藏夹 / 详情页拿到的永远是最新版本，不会停在启动时的缓存快照上。
    /// </summary>
    private void MergeIntoLibrary(IEnumerable<TrainerItem> items)
    {
        var index = new Dictionary<int, int>(_library.Items.Count);
        for (var i = 0; i < _library.Items.Count; i++) index[_library.Items[i].Id] = i;

        foreach (var item in items)
        {
            if (index.TryGetValue(item.Id, out var at))
            {
                TrainerDataService.MergeLocal(item, _library.Items[at]);
                _library.Items[at] = item;
            }
            else
            {
                index[item.Id] = _library.Items.Count;
                _library.Items.Add(item);
            }
        }
    }

    /// <summary>
    /// 把本地 SQLite 里的数据读成当前库，并按条数铺好分页；首次运行没有缓存就留一个空库。
    /// 首页的数据来源只有这里，不再访问官网。
    /// </summary>
    private void LoadCachedCards()
    {
        var cached = TrainerDataService.LoadCachedLibrary();
        _library = cached ?? new TrainerLibrary { Source = TrainerWeb.Site, Pages = 1 };
        TrainerDataService.CurrentLibrary = _library;

        // 读本地是同步的，不存在「正在读取」的中间态
        _firstLoading = false;
        _firstError = null;

        // 库换了对象，收藏里的引用也要换成新实例，否则详情页拿到的还是启动时的空快照
        _favorites.Clear();
        _favorites.AddRange(TrainerDataService.LoadFavorites(_library));

        ResetLocalPaging();
    }

    /// <summary>
    /// 首页分页改成纯本地切片：总条数、总页数都按本地库算，
    /// 官方的 X-WP-Total 只在同步时用到，不再参与显示。
    /// </summary>
    private void ResetLocalPaging()
    {
        _libraryPager.Reset();
        _libraryPager.TotalCount = _library.Items.Count;
        _libraryPager.TotalPages = Math.Max(1,
            (int)Math.Ceiling(_library.Items.Count / (double)TrainerWeb.PageSize));
        _libraryPager.Page = 1;

        if (_library.Items.Count > 0) _libraryPager.Cache[1] = LocalPage(1);
    }

    /// <summary>取本地库的第 <paramref name="page"/> 页，每页 <see cref="TrainerWeb.PageSize"/> 条。</summary>
    private List<TrainerItem> LocalPage(int page) =>
        _library.Items
            .Skip((page - 1) * TrainerWeb.PageSize)
            .Take(TrainerWeb.PageSize)
            .ToList();

    /// <summary>同步完之后用：重读本地库并刷新界面（不联网）。</summary>
    private void ReloadFromLocal()
    {
        _librarySeq++;   // 让还在飞的搜索 / 翻页响应作废
        LoadCachedCards();

        Render();
        UpdateStats();
        UpdateQueueBadge();
    }

    /// <summary>
    /// 把封面图片下载到 f\&lt;id&gt;\cover.jpg 并回写缓存表。
    /// 只跑 3 个并发，避免和列表请求抢带宽；落盘后 <see cref="TrainerItem.CoverPath"/> 变了，
    /// 卡片会自动从网络地址切到本地文件。
    /// </summary>
    private static async Task CacheCoversAsync(IEnumerable<TrainerItem> items)
    {
        var todo = items.Where(i => i.Id > 0 && !i.HasLocalCover).ToList();
        if (todo.Count == 0) return;

        using var gate = new SemaphoreSlim(3);

        var jobs = todo.Select(async item =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (AppDatabase.CacheMuted) return;   // 正在清缓存，别往 f 里写

                if (await TrainerFiles.CacheCoverAsync(item).ConfigureAwait(false) && !AppDatabase.CacheMuted)
                    AppDatabase.Instance.SaveCache(item);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(jobs).ConfigureAwait(false);
    }

    /// <summary>
    /// 收藏夹是启动时按 SQLite 快照还原的（那时列表还空着）。首屏到达后用实时数据替换同 ID 的
    /// 快照，封面和版本号才不会停在收藏当时的旧值。
    /// </summary>
    private void RelinkFavorites()
    {
        if (_favorites.Count == 0) return;

        var live = _library.Items.ToDictionary(i => i.Id);
        var changed = false;

        for (var i = 0; i < _favorites.Count; i++)
        {
            if (!live.TryGetValue(_favorites[i].Id, out var item)) continue;
            if (ReferenceEquals(_favorites[i], item)) continue;

            _favorites[i] = item;
            changed = true;
        }

        // 早期收藏的快照里没有头图地址，按 ID 补一次，封面才不会停在旧图
        foreach (var fav in _favorites.Where(f => string.IsNullOrWhiteSpace(f.Cover)))
            _ = FillFavoriteCoverAsync(fav);

        if (changed && _mode == ViewMode.Favorites) Render();
    }

    private static async Task FillFavoriteCoverAsync(TrainerItem item)
    {
        var cover = await TrainerWeb.FetchCoverAsync(item.Id);
        if (!string.IsNullOrWhiteSpace(cover)) item.Cover = cover;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateShellClip();
        UpdateCardMetrics();

        // 弹框尺寸是窗口的百分比，窗口一变就跟着重算
        if (_detailOpen && DetailHost.Content is TrainerDetailView view)
            view.FitTo(RootGrid.ActualWidth, RootGrid.ActualHeight);
    }

    // ==================== 卡片尺寸 ====================

    /// <summary>
    /// 固定版心布局：内容区最宽 <see cref="MaxContentWidth"/>，多出来的宽度平均留在两侧。
    /// 一行恒定 5 张，卡片宽度只随窗口宽度等比变化（1080 窗口 ≈ 151，1280 窗口 ≈ 191，
    /// 更宽的屏幕锁死在 191），高度按封面 16:9 + 固定标题区跟随，永不变形。
    /// </summary>
    private void UpdateCardMetrics()
    {
        // 直接拿窗口宽度反推，不依赖子控件排版：Window.SizeChanged 触发时子控件拿到的
        // ActualWidth 还可能是上一帧的，按它算会差一档
        var slot = ActualWidth - SidebarWidth - ContentSideMargin * 2;
        if (slot < 200) slot = 1280 - SidebarWidth - ContentSideMargin * 2; // 还没真正显示时按默认窗口算

        var contentW = Math.Min(MaxContentWidth, slot);

        // 显式给宽度 + 居中，宽屏上才能把整块摆在中间
        // （Width 默认是 NaN，NaN 参与比较恒为 false，必须先判 NaN 再判差值）
        if (double.IsNaN(CardArea.Width) || Math.Abs(CardArea.Width - contentW) > 0.5)
            CardArea.Width = contentW;

        // 列表右侧给滚动条留了 6px（CardListBox 的 Padding），剩下的整条宽度由 5 张卡片 + 4 个间距精确分完，
        // 这样第一张贴左、最后一张贴右，和上方标题的左右边缘对得上
        var avail = Math.Max(0, contentW - CardListGutter);
        var w = CardRowPanel.CardWidthOf(avail, CardRow.Capacity, CardGap);
        w = Math.Clamp(w, MinCardWidth, MaxCardWidth);

        // 每页 15 条 = 3 行：卡片再宽也不能让 3 行撑出列表可视高度，
        // 否则默认一屏就会冒出滚动条。窗口很高时才允许跟着放大（上限仍是 MaxCardWidth）。
        // 留 4px 余量，避免取整后差一两像素刚好溢出。
        // 超过一页（收藏夹、以后要是有更大的页）就不压了：按宽度自然排，让列表自己滚动，
        // 否则条数越多卡片越小，越滚越难看清。
        var viewport = CardList.ActualHeight;
        if (viewport > 120 && _items.Count <= TrainerWeb.PageSize)
        {
            var maxRowH = (viewport - CardRowGap * 3 - 4) / 3;
            var maxCover = maxRowH - CardInfoHeight;
            if (maxCover > 0)
            {
                var hLimit = Math.Floor(maxCover * 16d / 9d);
                w = Math.Min(w, Math.Max(MinCardWidth, hLimit));
            }
        }

        // 封面按 16:9 撑满上半部（官网头图就是这个比例），下半部固定放文字
        var h = Math.Round(w * 9d / 16d) + CardInfoHeight;

        var oldRowHeight = _cardH + CardRowGap;
        if (Math.Abs(w - _cardW) < 0.5 && Math.Abs(h - _cardH) < 0.5) return;

        _cardW = w;
        _cardH = h;

        Resources["CardW"] = w;
        Resources["CardH"] = h;
        Resources["CardInfoH"] = new GridLength(CardInfoHeight);

        // 行高跟着变了：停在原来那一行，别让画面弹回顶部
        KeepRowAnchor(oldRowHeight);
    }

    /// <summary>
    /// 行高变了（窗口缩放）之后，按「原来停在第几行」把滚动位置换算过去，
    /// 避免改窗口大小时内容一下子跳走。行数不变，所以不用重新分行。
    /// </summary>
    private void KeepRowAnchor(double oldRowHeight)
    {
        if (_items.Count == 0 || oldRowHeight <= 0) return;

        var scroll = _listScroll ??= FindScrollViewer(CardList);
        if (scroll is null) return;

        var anchorRow = scroll.VerticalOffset / oldRowHeight;
        var target = Math.Max(0, anchorRow * (_cardH + CardRowGap));

        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            // 先让新行高真正排完，ScrollViewer 的 Extent 更新之后再滚
            CardList.UpdateLayout();
            scroll.ScrollToVerticalOffset(target);
        }));
    }

    // ==================== 数据渲染 ====================

    /// <summary>是否处于「官网搜索结果」状态。</summary>
    private bool Searching => _keyword.Length > 0;

    // 列表接口没有浏览量字段，热门页用每款游戏的修改项目数作排序依据（当前页内排）。
    private IEnumerable<TrainerItem> SourceItems
    {
        get
        {
            if (_mode == ViewMode.Favorites) return _favorites;

            var pager = CurrentPager;
            if (pager.Cache.TryGetValue(pager.Page, out var page))
            {
                return _libraryTab == LibraryTab.Popular
                    ? page.OrderByDescending(i => i.RealOptionCount)
                        .ThenByDescending(i => DateOrder(i.UpdatedText))
                    : page;
            }

            // 第 1 页还没到手：搜索中 / 已经开始按页取数就先空着（显示加载状态），
            // 本地缓存里有旧卡片就先顶一屏，避免启动时白屏
            return Searching || _libraryPager.Cache.Count > 0 || _library.Items.Count == 0
                ? Array.Empty<TrainerItem>()
                : _library.Items.OrderByDescending(i => DateOrder(i.UpdatedText));
        }
    }

    private static DateTime DateOrder(string value) =>
        DateTime.TryParse(value.Replace('.', '-'), out var date) ? date : DateTime.MinValue;

    private void Render()
    {
        switch (_mode)
        {
            case ViewMode.Library when Searching:
                SectionTitle.Text = "搜索结果";
                SectionSubtitle.Text = _searchPager.TotalCount > 0
                    ? $"“{_keyword}” · 共 {_searchPager.TotalCount} 款"
                    : $"“{_keyword}”";
                break;
            case ViewMode.Library:
                SectionTitle.Text = "Steam 单机游戏库";
                // 「已经收录」按官网总数（X-WP-Total）实时显示；当前页码在右下角分页栏和状态栏
                SectionSubtitle.Text = _libraryPager.TotalCount > 0
                    ? $"已经收录 {_libraryPager.TotalCount} 款游戏"
                    : $"已经收录 {CachedGameCount()} 款游戏";
                break;
            case ViewMode.Favorites:
                SectionTitle.Text = "收藏夹";
                SectionSubtitle.Text = "";
                break;
            case ViewMode.Queue:
                SectionTitle.Text = "下载队列";
                // 副标题由 RefreshQueue() 换成真实的任务统计
                SectionSubtitle.Text = "暂无下载任务";
                break;
            case ViewMode.Settings:
                SectionTitle.Text = "设置";
                SectionSubtitle.Text = "";
                break;
        }

        // 没有副标题时连那 5px 间距一起收掉，标题下面不留空档
        SectionSubtitle.Visibility = string.IsNullOrWhiteSpace(SectionSubtitle.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        // 收藏夹视图下，卡片左上角会多出一个「取消收藏」按钮
        CardList.Tag = _mode == ViewMode.Favorites;
        LibraryTabs.Visibility = _mode == ViewMode.Library && !Searching
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecentTabButton.IsEnabled = _libraryTab != LibraryTab.Recent;
        PopularTabButton.IsEnabled = _libraryTab != LibraryTab.Popular;
        QueuePanel.Visibility = _mode == ViewMode.Queue ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = _mode == ViewMode.Settings ? Visibility.Visible : Visibility.Collapsed;

        if (_mode == ViewMode.Queue)
        {
            CardList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            RefreshQueue();
            UpdatePager();
            return;
        }

        if (_mode == ViewMode.Settings)
        {
            CardList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            UpdatePager();
            return;
        }

        // 「同步数据」按钮只在「本地一条都没有」时露出来；
        // 搜索没结果、收藏夹为空这些情况不该出现同步入口
        EmptySyncButton.Visibility = Visibility.Collapsed;

        // 列表按当前页取数（搜索与游戏库各自一份分页缓存）
        SetItems(SourceItems);

        // 首屏还没拿到数据：只有这一个状态需要特殊处理（数据全部实时拉取）
        if (_mode == ViewMode.Library && !Searching && _items.Count == 0 && (_firstLoading || _firstError != null))
        {
            CardList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            EmptyDesc.TextAlignment = TextAlignment.Center;
            EmptyDesc.MaxWidth = 520;

            if (_firstLoading)
            {
                EmptyGlyph.Text = "\uE895";
                EmptyTitle.Text = "正在读取本地数据";
                EmptyDesc.Text = "首页的数据全部来自本地 SQLite。";
            }
            else
            {
                EmptyGlyph.Text = "\uE783";
                EmptyTitle.Text = "读取失败";
                EmptyDesc.Text = $"读取本地数据库失败：{_firstError}\n\n" +
                                 "可以到「设置 → 数据更新」里重新同步一次。";
            }

            UpdatePager();
            return;
        }

        if (_items.Count == 0)
        {
            CardList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            EmptyDesc.TextAlignment = TextAlignment.Center;
            EmptyDesc.MaxWidth = 460;

            if (_mode == ViewMode.Favorites)
            {
                EmptyGlyph.Text = "\uE734";
                EmptyTitle.Text = "收藏夹还是空的";
                EmptyDesc.Text = "打开任意游戏详情，点击「加入收藏夹」，收藏会写进本地 SQLite，下次打开就能在这里快速找到。";
            }
            else if (Searching)
            {
                EmptyGlyph.Text = "\uE721";
                EmptyTitle.Text = _searching ? "正在搜索…" : "没有搜到";
                EmptyDesc.Text = _searching
                    ? $"正在搜索“{_keyword}”"
                    : _searchError is not null
                        ? $"搜索失败：{_searchError}"
                        : $"没有找到与“{_keyword}”相关的修改器，试试英文游戏名或换个关键词。";
            }
            else
            {
                // 空库不再弹框问「要不要同步」，就在页面正中给一个按钮，点了直接抓官网
                EmptyGlyph.Text = "\uE895";
                EmptyTitle.Text = "本地还没有数据";
                EmptyDesc.Text = "点击从官方获取最新游戏数据";
                EmptySyncButton.Visibility = Visibility.Visible;
            }
        }
        else
        {
            CardList.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
        }

        UpdatePager();
    }

    /// <summary>整体重建（换页 / 搜索 / 切换视图）。</summary>
    private void SetItems(IEnumerable<TrainerItem> items)
    {
        _items.Clear();
        _items.AddRange(items);

        _rows.Clear();
        foreach (var row in CardRow.Split(_items))
            _rows.Add(row);

        // 条数跨过「一页装得下 / 装不下」这条线时，卡片尺寸规则要换一套（见 UpdateCardMetrics）
        var scrollable = _items.Count > TrainerWeb.PageSize;
        if (scrollable != _cardScrollable)
        {
            _cardScrollable = scrollable;
            UpdateCardMetrics();
        }
    }

    // ==================== 上下分页 ====================

    /// <summary>游戏库和搜索结果都走分页；收藏夹 / 队列 / 设置不分页。</summary>
    private bool CanPage => _mode == ViewMode.Library;

    /// <summary>刷新右下角分页栏：总数、当前页码、上一页 / 下一页可用态、加载与重试。</summary>
    private void UpdatePager()
    {
        var pager = CurrentPager;
        var canShow = CanPage && (pager.Cache.Count > 0 || pager.Loading || pager.Failed);
        PagerBar.Visibility = canShow ? Visibility.Visible : Visibility.Collapsed;
        if (!canShow) return;

        PagerTotalText.Text = pager.TotalCount > 0 ? $"共 {pager.TotalCount} 款" : "";
        PagerTotalText.Visibility = pager.TotalCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        // 只显示当前页码：总数官网知道，但界面上不需要「第几 / 共几页」这种完整页码
        PagerPageText.Text = $"第 {pager.Page} 页";
        PagerPrev.IsEnabled = !pager.Loading && pager.Page > 1;
        PagerNext.IsEnabled = !pager.Loading && pager.Page < pager.TotalPages;
        PagerLoading.Visibility = pager.Loading ? Visibility.Visible : Visibility.Collapsed;
        PagerRetry.Visibility = pager.Failed && !pager.Loading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PagerPrev_Click(object sender, RoutedEventArgs e) => _ = GoToPageAsync(CurrentPager.Page - 1);

    private void PagerNext_Click(object sender, RoutedEventArgs e) => _ = GoToPageAsync(CurrentPager.Page + 1);

    /// <summary>失败重试：目标还是「上次想去的那一页」，页码停在旧页不影响当前显示。</summary>
    private void PagerRetry_Click(object sender, RoutedEventArgs e)
    {
        var pager = CurrentPager;
        var target = pager.PendingPage > 0 ? pager.PendingPage : pager.Page + 1;
        pager.Failed = false;
        _ = GoToPageAsync(target);
    }

    /// <summary>页码变了：重排卡片、滚回顶部、刷新副标题 / 状态栏 / 分页栏。</summary>
    private void ShowPageChanged()
    {
        Render();
        ScrollToTop();
        UpdateStats();
    }

    /// <summary>
    /// 跳到第 <paramref name="target"/> 页：已经缓存过的页直接切换，
    /// 没缓存的向官网请求（期间列表停留在当前页，分页栏显示加载状态）。
    /// </summary>
    private async Task GoToPageAsync(int target)
    {
        var pager = CurrentPager;
        target = Math.Clamp(target, 1, Math.Max(1, pager.TotalPages));
        if (pager.Loading) return;

        // 首页只读本地：游戏库的分页就是本地数组切片，不发请求、无需加载态
        if (!Searching)
        {
            pager.Cache[target] = LocalPage(target);
            pager.PendingPage = 0;
            pager.Page = target;
            pager.Failed = false;
            ShowPageChanged();
            return;
        }

        if (pager.Cache.ContainsKey(target))
        {
            pager.PendingPage = 0;
            pager.Page = target;
            pager.Failed = false;
            ShowPageChanged();
            return;
        }

        pager.PendingPage = target;
        pager.Loading = true;
        pager.Failed = false;
        UpdatePager();
        StatusText.Text = Searching
            ? $"正在加载搜索结果第 {target} 页…"
            : $"正在加载第 {target} 页…";

        var searching = Searching;
        var keyword = _keyword;
        var searchSeq = _searchSeq;
        var librarySeq = _librarySeq;

        // 换了关键词 / 退出搜索 / 清了缓存 / 手动同步：这一页的响应作废
        bool Stale() => searching ? searchSeq != _searchSeq : librarySeq != _librarySeq;

        try
        {
            var (items, totalPages, totalCount) = searching
                ? await TrainerWeb.SearchAsync(keyword, target)
                : await TrainerWeb.FetchPageAsync(target);

            if (Stale()) return;

            // 新页里既可能有见过的（把本地路径搬过去），也有全新的（并入内存库，收藏关联才找得到）
            MergeIntoLibrary(items);
            RelinkFavorites();

            pager.Cache[target] = items;
            pager.TotalPages = Math.Max(1, totalPages);
            pager.TotalCount = totalCount;
            pager.Page = target;
            pager.PendingPage = 0;

            // 新的一页同样落库，下次启动直接读缓存
            TrainerDataService.SaveCache(items);
            _ = CacheCoversAsync(items);

            ShowPageChanged();
        }
        catch
        {
            if (Stale()) return;
            pager.Failed = true;
            StatusText.Text = $"第 {target} 页加载失败，可点右下角的「重试」";
        }
        finally
        {
            pager.Loading = false;
            if (!Stale()) UpdatePager();
        }
    }

    // ==================== 官网搜索 ====================

    /// <summary>关键词里有没有汉字：有汉字的话官网（英文站）搜不到，要先在本地翻成英文名。</summary>
    private static bool IsChinese(string text) =>
        text.Any(c => (c >= 0x3400 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF));

    /// <summary>
    /// 按回车才走这里：向 flingtrainer.com 的搜索接口要第 1 页（和官网搜索页同一套结果与顺序）。
    /// 输入过程中不会发请求，也不会动当前列表；后续页走右下角分页栏。
    /// </summary>
    private async Task SubmitSearchAsync()
    {
        var typed = SearchBox.Text.Trim();

        // 清空后回车 = 退出搜索，回到原来的游戏库
        if (typed.Length == 0)
        {
            ExitSearch();
            return;
        }

        // 官网接口按 URL 编码传关键词（见 TrainerWeb.SearchEndpointOf），
        // 但官网只有英文内容，中文关键词发过去一定是空结果 ——
        // 本地有中文名库，先把它翻成英文名，再拿英文名去官网搜（查询本身仍然走官网）。
        var keyword = typed;
        var translatedFrom = "";
        if (IsChinese(typed) && AppDatabase.Instance.IsAvailable)
        {
            var english = AppDatabase.Instance.FindEnglishByChinese(typed);
            if (!string.IsNullOrWhiteSpace(english) &&
                !string.Equals(english.Trim(), typed, StringComparison.OrdinalIgnoreCase))
            {
                keyword = english.Trim();
                translatedFrom = typed;
            }
        }

        if (_searching) return;

        // 搜索结果显示在「游戏库」这一页里，先把导航切过去
        if (_mode != ViewMode.Library)
        {
            NavList.SelectedIndex = 0;
            if (_mode != ViewMode.Library) _mode = ViewMode.Library;
        }

        var seq = ++_searchSeq;

        _keyword = keyword;
        _searchPager.Reset();
        _searchError = null;
        _searching = true;

        Render();
        ScrollToTop();
        StatusText.Text = translatedFrom.Length > 0
            ? $"正在搜索“{translatedFrom}” → {keyword}…"
            : $"正在搜索“{keyword}”…";

        try
        {
            var (items, totalPages, totalCount) = await TrainerWeb.SearchAsync(keyword, 1);

            // 期间又搜了别的关键词：这次的结果作废
            if (seq != _searchSeq) return;

            _searchPager.Cache[1] = items;
            _searchPager.Page = 1;
            _searchPager.TotalPages = Math.Max(1, totalPages);
            _searchPager.TotalCount = totalCount;

            if (items.Count > 0)
            {
                TrainerDataService.SaveCache(items);
                _ = CacheCoversAsync(items);
            }

            var label = translatedFrom.Length > 0 ? $"“{translatedFrom}” → {keyword}" : $"“{keyword}”";

            StatusText.Text = totalCount > 0
                ? $"搜索{label}"
                : translatedFrom.Length > 0
                    ? $"搜索{label} · 没有结果"
                    : IsChinese(typed)
                        ? $"搜索{label} · 没有结果（库里的名字是英文，可以试试英文名）"
                        : $"搜索{label} · 没有结果";
        }
        catch (Exception ex)
        {
            if (seq != _searchSeq) return;
            _searchError = ex.Message;
            StatusText.Text = $"搜索失败：{ex.Message}";
        }
        finally
        {
            if (seq == _searchSeq)
            {
                _searching = false;
                Render();
            }
        }
    }

    /// <summary>退出搜索：清掉结果，列表回到游戏库（页码还停在原来那一页）。</summary>
    private void ExitSearch(bool clearText = false)
    {
        var was = Searching;

        // 先把状态清干净，再去改输入框内容——否则 TextChanged 会再进来一次
        _searchSeq++;
        _keyword = "";
        _searchPager.Reset();
        _searching = false;
        _searchError = null;

        if (clearText && SearchBox.Text.Length > 0) SearchBox.Text = "";

        Render();

        if (!was) return;

        ScrollToTop();
        UpdateStats();
    }

    /// <summary>
    /// 「已经收录」的数字以 SQLite 缓存表为准，不用内存里那份列表：
    /// 一键清除缓存之后库里已经是 0 条，内存里的卡片还没散，用内存数就会显示成几百款。
    /// 数据库不可用（建库失败）时退回内存数量，至少不会一直报 0。
    /// </summary>
    private int CachedGameCount() =>
        AppDatabase.Instance.IsAvailable ? AppDatabase.Instance.CacheCount() : _library.Items.Count;

    /// <summary>刷新左侧栏的同步时间与收藏数量，状态栏写当前页码与官网总数。</summary>
    private void UpdateStats()
    {
        SidebarSyncText.Text = string.IsNullOrEmpty(_library.FetchedAt)
            ? "等待同步"
            : $"同步于 {_library.FetchedAt}";

        NavFavLabel.Text = _favorites.Count > 0 ? $"收藏夹 · {_favorites.Count}" : "收藏夹";

        // 设置页「同步中文名」那行的覆盖数也一起跟着最新的库状态走
        UpdateNameSyncSetting();

        if (Searching)
        {
            StatusText.Text = _searching
                ? $"正在搜索“{_keyword}”…"
                : _searchPager.TotalCount > 0
                    ? $"搜索“{_keyword}”"
                    : $"搜索“{_keyword}” · 没有结果";
            return;
        }

        // 页码和官网总数都在右下角分页栏里，这里只报「本页加载了多少」
        StatusText.Text = $"已加载 {_items.Count} 款游戏";
    }

    // ==================== 交互 ====================

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || NavList.SelectedIndex < 0) return;

        _mode = NavList.SelectedIndex switch
        {
            1 => ViewMode.Favorites,
            2 => ViewMode.Queue,
            3 => ViewMode.Settings,
            _ => ViewMode.Library
        };

        Render();
        ScrollToTop();
    }

    private void LibraryTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tab }) return;

        _libraryTab = tab == "Popular" ? LibraryTab.Popular : LibraryTab.Recent;
        Render();
        ScrollToTop();
    }

    // ==================== 下载队列 ====================

    private void OnDownloadsChanged()
    {
        UpdateQueueBadge();
        if (_mode == ViewMode.Queue) RefreshQueue();
    }

    /// <summary>左侧导航角标显示正在下载的数量。</summary>
    private void UpdateQueueBadge()
    {
        var tasks = DownloadManager.Instance.Tasks;
        var active = tasks.Count(t => t.IsActive);

        NavQueueLabel.Text = active > 0 ? $"下载队列 · {active}" : "下载队列";

        var empty = tasks.Count == 0;
        QueueEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        QueueList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshQueue()
    {
        UpdateQueueBadge();
        SectionSubtitle.Text = DownloadManager.Instance.SummaryText;
        SectionSubtitle.Visibility = Visibility.Visible;   // 别被「收藏夹/设置没有副标题」那次折叠带过来
        UpdatePager();
    }

    private void QueueCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DownloadTask task }) DownloadManager.Instance.Cancel(task);
    }

    private void QueueRetry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DownloadTask task }) DownloadManager.Instance.Retry(task);
    }

    private void QueueOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DownloadTask task }) DownloadManager.Instance.OpenFolder(task);
    }

    private void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DownloadTask task }) DownloadManager.Instance.Remove(task);
    }

    private void OpenDownloadDir_Click(object sender, RoutedEventArgs e)
        => DownloadManager.Instance.OpenFolder(null);

    /// <summary>点左下角的 flingtrainer.com：用系统默认浏览器打开官网。</summary>
    private void DataSourceLink_Click(object sender, MouseButtonEventArgs e)
    {
        OpenExternal("https://flingtrainer.com/");
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        var count = DownloadManager.Instance.ClearFinished();
        StatusText.Text = count == 0 ? "没有可清理的任务" : $"已清理 {count} 个任务";
        RefreshQueue();
    }

    private void ScrollToTop()
    {
        _listScroll ??= FindScrollViewer(CardList);
        _listScroll?.ScrollToTop();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }

        return null;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        // 输入过程中不发请求、不动列表；但整段删空时直接退出搜索结果，回到原来的游戏库
        if (IsLoaded && Searching && SearchBox.Text.Trim().Length == 0)
            ExitSearch();

        // 从提示里选了一条、正在回填：别再弹一次提示
        if (_applyingSuggest) return;

        QueueSuggest();
    }

    // ==================== 搜索智能提示（走本地 SQLite） ====================
    //
    // 提示只做「帮你把名字打对」，真正的搜索还是发给官网：
    // 选中一条 → 把英文名回填搜索框 → 走原来的 SubmitSearchAsync。

    /// <summary>输入停下来才查；删空就立刻收起提示。</summary>
    private void QueueSuggest()
    {
        _suggestTimer.Stop();

        if (SearchBox.Text.Trim().Length == 0)
        {
            CloseSuggest();
            return;
        }

        _suggestTimer.Start();
    }

    private async void SuggestTimer_Tick(object? sender, EventArgs e)
    {
        _suggestTimer.Stop();

        var keyword = SearchBox.Text.Trim();
        if (keyword.Length == 0)
        {
            CloseSuggest();
            return;
        }

        if (!AppDatabase.Instance.IsAvailable) return;

        // 查 SQLite 放到线程池，别卡输入
        var rows = await Task.Run(() => AppDatabase.Instance.SearchGameNames(keyword, 8));

        // 查询期间用户又改了字：这次结果作废
        if (SearchBox.Text.Trim() != keyword) return;

        if (rows.Count == 0)
        {
            CloseSuggest();
            return;
        }

        _suggests = rows;
        SuggestList.ItemsSource = rows;
        SuggestList.SelectedIndex = -1;
        SuggestPopup.IsOpen = true;

        SearchBox.Focus();
    }

    private void CloseSuggest()
    {
        if (!SuggestPopup.IsOpen) return;

        CheckLog("CloseSuggest 执行");
        SuggestPopup.IsOpen = false;
        SuggestList.ItemsSource = null;
        _suggests = new List<GameNameRow>();
    }

    /// <summary>把提示项回填进搜索框，并直接按官网搜索走一次。</summary>
    private void ApplySuggest(GameNameRow row)
    {
        _applyingSuggest = true;
        SearchBox.Text = row.CleanEn;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        _applyingSuggest = false;

        SearchHint.Visibility = Visibility.Collapsed;
        CloseSuggest();

        _ = SubmitSearchAsync();
    }

    /// <summary>
    /// 鼠标点提示项：不依赖 ListBox 的选中，直接从命中的元素往上找到那一行的 DataContext。
    /// 按下就处理（Up 太晚，Popup 是独立窗口，鼠标一按就可能已经被判成「点了外面」）。
    /// </summary>
    private void SuggestList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = RowFromElement(e.OriginalSource);
        CheckLog($"SuggestList PreviewMouseDown：src={e.OriginalSource?.GetType().Name}，row={row?.CleanEn ?? "null"}");

        if (row is null) return;

        e.Handled = true;   // 别让 ListBox 再走自己的选中 / 焦点逻辑，否则会抢走搜索框焦点
        ApplySuggest(row);
    }

    private static GameNameRow? RowFromElement(object? source)
    {
        var dep = source as DependencyObject;
        while (dep is not null and not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);

        return (dep as ListBoxItem)?.DataContext as GameNameRow;
    }

    private void MoveSuggest(int delta)
    {
        if (_suggests.Count == 0) return;

        var index = SuggestList.SelectedIndex + delta;
        if (index < 0) index = _suggests.Count - 1;
        if (index >= _suggests.Count) index = 0;

        SuggestList.SelectedIndex = index;
        SuggestList.ScrollIntoView(SuggestList.SelectedItem);
    }

    /// <summary>
    /// 提示打开时：↑↓ 挪高亮、回车把那一条拿去搜官网、Esc 先收提示。
    /// 必须挂在 PreviewKeyDown 上——单行 TextBox 自己的类处理器会先把 ↑↓ 当成
    /// 「光标去行首 / 行尾」吃掉，写在 KeyDown 里永远收不到方向键。
    /// </summary>
    private void SearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (SuggestPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    e.Handled = true;
                    MoveSuggest(+1);
                    return;
                case Key.Up:
                    e.Handled = true;
                    MoveSuggest(-1);
                    return;
                case Key.Escape:
                    e.Handled = true;
                    CloseSuggest();
                    return;
                case Key.Enter:
                    e.Handled = true;
                    // 选中了就用选中的那一条，没选就直接搜输入框里的字
                    if (SuggestList.SelectedItem is GameNameRow row)
                        ApplySuggest(row);
                    else
                    {
                        CloseSuggest();
                        _ = SubmitSearchAsync();
                    }
                    return;
            }
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CloseSuggest();
            _ = SubmitSearchAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ExitSearch(clearText: true);
        }
    }

    private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TrainerItem item }) return;
        e.Handled = true;
        OnFavoriteChanged(item, false);
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TrainerItem item }) return;
        OpenDetail(item);
    }

    // ==================== 卡片悬停：封面轻微放大 ====================

    /// <summary>卡片封面那个 Border 在模板里的名字。</summary>
    private const string CoverArtName = "CoverArt";

    private void Card_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => ZoomCover(sender as DependencyObject, 1.08);

    private void Card_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => ZoomCover(sender as DependencyObject, 1.0);

    /// <summary>
    /// 悬停时把封面画刷放大一点点（1.08 倍），退出时回到原样。
    /// 放大的是画刷而不是布局：不重排、不动原图，圆角裁剪保证放大后也不会溢出卡片。
    /// </summary>
    private static void ZoomCover(DependencyObject? card, double target)
    {
        if (card is null) return;

        var cover = FindCoverArt(card);
        if (cover?.Background is not ImageBrush template) return;

        // 模板里的 Freezable 会被 WPF 冻结以便多实例共享，直接动画会抛异常，先克隆出可写副本
        var brush = template;
        if (brush.IsFrozen)
        {
            brush = (ImageBrush)template.CloneCurrentValue();
            cover.Background = brush;
        }

        if (brush.RelativeTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform(1, 1, 0.5, 0.5);
            brush.RelativeTransform = scale;
        }

        var duration = TimeSpan.FromMilliseconds(target > 1 ? 260 : 320);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease });
    }

    private static Border? FindCoverArt(DependencyObject root)
    {
        if (root is Border { Name: CoverArtName } self) return self;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindCoverArt(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }

        return null;
    }

    private void OpenDetail(TrainerItem item)
    {
        var isFavorite = _favorites.Any(f => f.Id == item.Id);
        var view = new TrainerDetailView(item, isFavorite);
        view.CloseRequested += (_, _) => CloseDetail();
        view.FavoriteChanged += (_, _) => OnFavoriteChanged(item, view.IsFavorite);

        // 启动修改器成功 → 本程序自动收进托盘（右下角图标，右键可「打开主界面 / 退出程序」）
        view.Launched += (_, _) => HideForLaunchedApp();
        view.FitTo(RootGrid.ActualWidth, RootGrid.ActualHeight);

        DetailHost.Content = view;
        DetailLayer.Visibility = Visibility.Visible;
        _detailOpen = true;

        // 封面跟着弹窗一起上屏，不再等动画播完：卡片刚显示过同一张图，
        // 位图基本还在 NetImages 的缓存里（本地文件解码也就十几毫秒），
        // 网络图本来就是后台下载、下完自动刷新，不会卡住开场动画。
        _ = view.LoadImagesAsync();

        // 先把布局算完，动画只做位图合成：期间不重排、不重绘文字，稳定 60 帧
        DetailBackdrop.BeginAnimation(OpacityProperty, null);
        DetailBackdrop.Opacity = 0;
        DetailHost.BeginAnimation(OpacityProperty, null);
        DetailHost.Opacity = 0;
        DetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        DetailScale.ScaleX = DetailScale.ScaleY = DetailScaleFrom;
        DetailSlide.BeginAnimation(TranslateTransform.YProperty, null);
        DetailSlide.Y = 10;
        DetailHost.UpdateLayout();

        DetailHost.CacheMode = new BitmapCache();

        var token = ++_detailAnimToken;

        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        // 和关于弹窗同一档回弹（0.5）：从 0.96 起跳，实测过冲约 0.5%、350ms 左右落定
        var spring = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };

        DetailBackdrop.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(DetailBackdropTime)) { EasingFunction = fade });

        DetailHost.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170))) { EasingFunction = fade });

        // X / Y 各用一条独立时间线：同一个 Timeline 复用给两个属性会各自生成时钟，Completed 会触发两次
        var zoomX = new DoubleAnimation(DetailScaleFrom, 1, new Duration(DetailOpenTime)) { EasingFunction = spring };
        var zoomY = new DoubleAnimation(DetailScaleFrom, 1, new Duration(DetailOpenTime)) { EasingFunction = spring };

        // 注意：Completed 必须在 BeginAnimation 之前挂，先启动再挂回调的话永远不会触发
        zoomY.Completed += (_, _) =>
        {
            // 开场还没播完就被关掉（token 变了）就别再去构建列表了，那个详情页已经不要了
            if (token != _detailAnimToken) return;

            // 动画结束后恢复实时渲染，滚动与文字才清晰；选项列表再依次上屏
            // （33~46 行的模板 + 文本排版约 80ms，放在开场动画之前会明显卡一下）
            DetailHost.CacheMode = null;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                view.LoadOptions();
            }));
        };

        DetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoomX);
        DetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoomY);
        DetailSlide.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, new Duration(DetailOpenTime)) { EasingFunction = fade });
    }

    /// <summary>收藏状态变化：写 SQLite + 更新内存列表 + 刷新收藏夹视图。</summary>
    private void OnFavoriteChanged(TrainerItem item, bool favorite)
    {
        if (favorite)
        {
            if (_favorites.All(f => f.Id != item.Id)) _favorites.Insert(0, item);
        }
        else
        {
            _favorites.RemoveAll(f => f.Id == item.Id);
        }

        TrainerDataService.SetFavorite(item, favorite);

        if (_mode == ViewMode.Favorites) Render();
        UpdateStats();

        StatusText.Text = favorite
            ? $"已收藏「{item.DisplayTitle}」· 共 {_favorites.Count} 款"
            : $"已取消收藏「{item.DisplayTitle}」· 共 {_favorites.Count} 款";
    }

    /// <summary>点遮罩（弹框外那片半透明区域）关闭弹框；点在弹框内部不触发。</summary>
    private void DetailLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_detailOpen) return;

        // 只有事件源就是那层遮罩矩形才算「点在外面」，点到弹框的任何内容都不关
        if (!ReferenceEquals(e.OriginalSource, DetailBackdrop)) return;

        e.Handled = true;
        CloseDetail();
    }

    private void CloseDetail()
    {
        if (!_detailOpen) return;
        _detailOpen = false;

        DetailHost.CacheMode = new BitmapCache();

        var token = ++_detailAnimToken;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(DetailCloseTime);

        var cardFade = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };

        // 注意：Completed 必须在 BeginAnimation 之前挂，先启动再挂回调的话永远不会触发
        cardFade.Completed += (_, _) =>
        {
            // 播到一半又被点开（token 变了）就别收层，否则会把刚打开的详情页一起关掉
            if (token != _detailAnimToken) return;

            DetailLayer.Visibility = Visibility.Collapsed;
            DetailHost.Content = null;
            DetailHost.CacheMode = null;

            // 清掉动画值，回到 XAML 里的初始态（0.96 / 透明），下次打开才能从头播
            DetailHost.BeginAnimation(OpacityProperty, null);
            DetailBackdrop.BeginAnimation(OpacityProperty, null);
            DetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            DetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            DetailSlide.BeginAnimation(TranslateTransform.YProperty, null);
            DetailHost.Opacity = 0;
            DetailBackdrop.Opacity = 0;
            DetailScale.ScaleX = DetailScale.ScaleY = DetailScaleFrom;
            DetailSlide.Y = 10;
        };

        // 面板比关于弹窗大得多，收场只缩到 0.97、位移 6，够柔和又不晃
        DetailHost.BeginAnimation(OpacityProperty, cardFade);
        DetailScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = ease });
        DetailScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = ease });
        DetailSlide.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, 6, duration) { EasingFunction = ease });
        DetailBackdrop.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, 0, duration) { EasingFunction = ease });
    }

    /// <summary>
    /// 首页只读本地，所以右上角这个按钮不再是「联网刷新」，而是「重新读一遍本地库」：
    /// 主要给同步完成之后手动刷一次用，读的是 SQLite，秒回，不碰网络。
    /// 真要拿官网新数据，去「设置 → 数据更新」。
    /// </summary>
    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;

        var rotate = new RotateTransform();
        RefreshGlyph.RenderTransform = rotate;
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });

        RefreshButton.IsEnabled = false;

        try
        {
            ReloadFromLocal();

            StatusText.Text = _library.Items.Count > 0
                ? $"已重新读取本地数据 · {_library.Items.Count} 款"
                : "本地还没有数据，去「设置 → 数据更新」同步一次";
        }
        finally
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            RefreshButton.IsEnabled = true;
            _busy = false;
        }
    }

    // ==================== 游戏名库：官网全量 + Steam 中文名 ====================

    /// <summary>后台同步当前走到哪一步；进度面板和左下角小胶囊都按它渲染。</summary>
    private enum NameSyncStage
    {
        Idle,
        Fetching,
        Filling,
        Done,
        Cancelled,
        Failed
    }

    private sealed record NameSyncResult(int Total, int Cn, bool Cancelled, string? Error, bool SteamBlocked);

    private NameSyncStage _nameSyncStage = NameSyncStage.Idle;

    /// <summary>面板上那两行文字与进度，全部来自这几个字段：面板随时收起再打开都能还原。</summary>
    private string _nameSyncPhaseText = "还没同步过 Steam 中文名";
    private string _nameSyncCurrent = "";
    private int _nameSyncDone;
    private int _nameSyncRunTotal;

    /// <summary>失败之后要不要继续把胶囊挂在顶栏：用户点开看过一次就收起来，别一直杵在那儿。</summary>
    private bool _nameSyncFailureDismissed;

    private bool NameSyncRunning => _nameSyncStage is NameSyncStage.Fetching or NameSyncStage.Filling;

    /// <summary>后台开跑（不阻塞调用方，也不弹面板）。</summary>
    private void StartNameSync(bool rebuild) => _ = RunNameSyncAsync(rebuild);

    /// <summary>
    /// 后台同步中文名。两条路：
    /// <para><paramref name="rebuild"/> = true：按 A-Z 重抓官网全量英文名重建名单（<b>已有的中文名会原样保留</b>）。</para>
    /// <para>false：不动名单，只把还没有中文名的那些拿去 Steam 慢慢换（启动时自动走这条）。</para>
    /// 两条路最后都只去问「还没有中文名」的那批，拿到的才写库；全程不挡界面，
    /// 进度体现在顶栏小胶囊和「点开的面板」上；取消时已写的中文名都保留。
    /// </summary>
    private async Task<NameSyncResult> RunNameSyncAsync(bool rebuild)
    {
        if (_nameSyncRunning) return new NameSyncResult(0, 0, true, null, false);

        _nameSyncRunning = true;
        _nameSyncCts?.Dispose();
        _nameSyncCts = new CancellationTokenSource();
        var ct = _nameSyncCts.Token;

        var total = 0;
        var cn = 0;
        var cancelled = false;
        var steamBlocked = false;
        string? error = null;

        _nameSyncDone = 0;
        _nameSyncRunTotal = 0;
        _nameSyncCurrent = "";
        _nameSyncFailureDismissed = false;

        try
        {
            var full = rebuild || AppDatabase.Instance.GameNameCount() == 0;
            List<GameNameRow> rows;

            if (full)
            {
                _nameSyncStage = NameSyncStage.Fetching;
                _nameSyncPhaseText = "正在抓取 A-Z 游戏列表…";
                UpdateNameUi();

                // 先抓到完整的新名单再动表：抓取途中取消的话，旧名单和旧中文名都还留在库里
                var fresh = await TrainerWeb.FetchAllNamesAsync(ct);
                if (fresh.Count == 0) throw new InvalidOperationException("没有返回游戏列表");

                // 重建只换名单，已换到的中文名按 trainer_id 带过去，不用重新问一遍 Steam
                var keptCn = AppDatabase.Instance.RebuildGameNames(fresh);
                total = AppDatabase.Instance.GameNameCount();
                if (total == 0) throw new InvalidOperationException("游戏名库写入失败");

                _nameSyncPhaseText = keptCn > 0
                    ? $"名单已更新：{total} 款，保留了 {keptCn} 个已有中文名"
                    : $"名单已更新：{total} 款";

                // 重建之后也只补缺的，不把 767 条重新问一遍
                rows = AppDatabase.Instance.LoadGameNamesMissingCn();
            }
            else
            {
                // 只补缺的：已经是中文名的不用再问一遍 Steam
                rows = AppDatabase.Instance.LoadGameNamesMissingCn();
                total = AppDatabase.Instance.GameNameCount();
            }

            if (rows.Count > 0)
            {
                _nameSyncStage = NameSyncStage.Filling;
                _nameSyncPhaseText = full
                    ? "正在从 Steam 逐个换取中文名…"
                    : $"正在补齐剩余 {rows.Count} 款的 Steam 中文名…";
                _nameSyncRunTotal = rows.Count;
                UpdateNameUi();

                (cn, steamBlocked) = await FillChineseNamesAsync(rows, ct);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            _nameSyncRunning = false;
            _nameSyncCts?.Dispose();
            _nameSyncCts = null;
        }

        var cnTotal = AppDatabase.Instance.GameNameCnCount();
        var allTotal = AppDatabase.Instance.GameNameCount();

        _nameSyncStage = error is not null || steamBlocked
            ? NameSyncStage.Failed
            : cancelled
                ? NameSyncStage.Cancelled
                : NameSyncStage.Done;

        _nameSyncPhaseText = error is not null
            ? $"同步失败：{error}"
            : steamBlocked
                ? "Steam 连不上，先停在这儿了 · 点「开始同步」可以再试"
                : cancelled
                    ? $"已取消 · 中文名 {cnTotal} / {allTotal}"
                    : $"同步完成 · {allTotal} 款游戏，其中 {cnTotal} 款有中文名";

        _nameSyncCurrent = "";
        _nameSyncDone = _nameSyncRunTotal;
        UpdateNameUi();

        // 面板开着才提示一下再收起；用户已经收起来的就别跳出来打扰
        if (NameSyncLayer.Visibility == Visibility.Visible)
        {
            await Task.Delay(cancelled ? 900 : 1600);
            if (!NameSyncRunning) HideNameSyncPanel();
        }

        return new NameSyncResult(total, cn, cancelled, error, steamBlocked);
    }

    /// <summary>
    /// 去 Steam 换中文名：2 路并发 + 每条之间歇一小会儿，慢慢跑不惊动 Steam。
    /// 拿到一个就写一个（取消 / 关面板都不丢），连着失败太多次（基本等于 Steam 不可达）就提前收工。
    /// </summary>
    private async Task<(int Got, bool Blocked)> FillChineseNamesAsync(List<GameNameRow> rows, CancellationToken ct)
    {
        var done = 0;
        var got = 0;
        var fails = 0;
        var blocked = false;
        var lastPush = 0L;

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await Parallel.ForEachAsync(
                rows,
                new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = abort.Token },
                async (row, token) =>
                {
                    var name = await SteamWeb.FetchChineseNameAsync(SteamQueryName(row.NameEn), token)
                        .ConfigureAwait(false);

                    var n = Interlocked.Increment(ref done);

                    if (name is null)
                    {
                        // 请求本身失败：连着 12 次且一条中文名都没拿到，就别再试了
                        if (Interlocked.Increment(ref fails) >= 12 && Volatile.Read(ref got) == 0)
                        {
                            blocked = true;
                            abort.Cancel();
                        }
                    }
                    else if (name.Length > 0)
                    {
                        Interlocked.Increment(ref got);
                        Interlocked.Exchange(ref fails, 0);
                        AppDatabase.Instance.UpdateGameNameCn(row.TrainerId, name);
                    }
                    else
                    {
                        Interlocked.Exchange(ref fails, 0);
                    }

                    // 后台跑就慢慢来：每条之间喘一口气，767 条摊到两三分钟，不至于被限流
                    if (name is not null && !token.IsCancellationRequested)
                    {
                        try { await Task.Delay(90, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }

                    // UI 不用每条都刷：200ms 推一次足够顺，也不会把界面线程喂爆
                    var now = Environment.TickCount64;
                    if (n == rows.Count || now - Interlocked.Read(ref lastPush) >= 200)
                    {
                        Interlocked.Exchange(ref lastPush, now);
                        var current = name is { Length: > 0 } ? name : row.CleanEn;
                        var doneNow = n;
                        _ = Dispatcher.BeginInvoke(new Action(() => UpdateNameSyncProgress(doneNow, rows.Count, current)));
                    }
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 不是用户取消，是 Steam 一直连不上触发的提前收工
            blocked = true;
        }

        return (got, blocked);
    }

    /// <summary>拿去 Steam 搜的名字：去掉官网标题里的 " Trainer" / " Enhanced" 后缀。</summary>
    private static string SteamQueryName(string officialTitle) =>
        officialTitle.Replace(" Trainer", "").Replace(" Enhanced", "").Trim();

    // ==================== 同步 UI：顶栏小胶囊 + 进度面板 ====================

    /// <summary>把当前状态同时刷到小胶囊、面板和设置页那行中文名上。</summary>
    private void UpdateNameUi()
    {
        UpdateNamePill();
        RefreshNamePanel();
        UpdateNameSyncSetting();
    }

    /// <summary>
    /// 顶栏胶囊：只在本轮同步跑着（或刚失败还没被看过）时出现，平时整颗收起来不占地方。
    /// 数字直接取内存里这一轮的进度，不再每条都去 COUNT 一遍数据库 ——
    /// 既省 IO，也不会出现「库里的数还没写进去、胶囊看着不动」的错觉。
    /// </summary>
    private void UpdateNamePill()
    {
        var running = NameSyncRunning;
        var showFailed = _nameSyncStage == NameSyncStage.Failed && !_nameSyncFailureDismissed;

        NamePill.Visibility = running || showFailed ? Visibility.Visible : Visibility.Collapsed;

        if (!running && !showFailed)
        {
            StartPillPulse(false);
            return;
        }

        var accent = (Brush)FindResource("B.AccentLight");
        NamePillSub.Foreground = accent;

        switch (_nameSyncStage)
        {
            case NameSyncStage.Fetching:
                NamePillText.Text = "正在抓取名单";
                NamePillSub.Text = "";
                NamePillDot.Fill = accent;
                StartPillPulse(true);
                break;

            case NameSyncStage.Filling:
                var pct = _nameSyncRunTotal > 0
                    ? (int)Math.Round(_nameSyncDone * 100.0 / _nameSyncRunTotal)
                    : 0;
                NamePillText.Text = $"同步中 {pct}%";
                NamePillSub.Text = $"{_nameSyncDone} / {_nameSyncRunTotal}";
                NamePillDot.Fill = accent;
                StartPillPulse(true);
                break;

            case NameSyncStage.Failed:
                NamePillText.Text = "同步中断";
                NamePillSub.Text = "点我看看";
                NamePillDot.Fill = (Brush)FindResource("B.Accent");
                StartPillPulse(false);
                break;
        }
    }

    /// <summary>
    /// 设置页「同步中文名」那一行：平时显示全库的中文名覆盖情况，跑起来跟着百分比走，
    /// 按钮顺手换成「查看进度」。原来这行挂在左下角「数据源」里，现在统一收到设置页。
    /// </summary>
    private void UpdateNameSyncSetting()
    {
        var available = AppDatabase.Instance.IsAvailable;
        NameSyncButton.IsEnabled = available;

        if (NameSyncRunning)
        {
            var pct = _nameSyncRunTotal > 0 ? _nameSyncDone * 100 / _nameSyncRunTotal : 0;
            NameSyncSettingDesc.Text = _nameSyncRunTotal > 0
                ? $"正在同步 Steam 中文名 {pct}% · {_nameSyncDone} / {_nameSyncRunTotal}"
                : "正在抓取官网 A-Z 名单…";
            NameSyncButton.Content = "查看进度";
            return;
        }

        var all = available ? AppDatabase.Instance.GameNameCount() : 0;
        var cn = available ? AppDatabase.Instance.GameNameCnCount() : 0;

        NameSyncSettingDesc.Text = all == 0
            ? "本地还没有游戏名单，同步时会先抓官网 A-Z，再逐个向 Steam 换取中文名。"
            : $"已换到 {cn} / {all} 个中文名，同步只补还没有的那些。";

        NameSyncButton.Content = cn > 0 ? "继续同步" : "开始同步";
    }

    /// <summary>同步中的呼吸灯：小圆点一明一暗，比转圈省事也不会抢视线。</summary>
    private void StartPillPulse(bool on)
    {
        if (on)
        {
            NamePillDot.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                });
            return;
        }

        NamePillDot.BeginAnimation(OpacityProperty, null);
        NamePillDot.Opacity = 1;
    }

    /// <summary>面板内容全部由字段渲染：随时点开、随时收起，看到的都是最新一帧。</summary>
    private void RefreshNamePanel()
    {
        var running = NameSyncRunning;

        NameSyncTitle.Text = _nameSyncStage switch
        {
            NameSyncStage.Fetching or NameSyncStage.Filling => "Steam 中文名同步",
            NameSyncStage.Done => "中文名同步完成",
            NameSyncStage.Cancelled => "中文名同步已取消",
            NameSyncStage.Failed => "中文名同步中断",
            _ => "Steam 中文名同步"
        };

        NameSyncPhase.Text = _nameSyncPhaseText;

        NameSyncBar.IsIndeterminate = _nameSyncStage == NameSyncStage.Fetching;
        NameSyncBar.Maximum = Math.Max(1, _nameSyncRunTotal);
        NameSyncBar.Value = _nameSyncStage == NameSyncStage.Done
            ? NameSyncBar.Maximum
            : Math.Min(_nameSyncDone, NameSyncBar.Maximum);

        NameSyncCount.Text = _nameSyncRunTotal > 0 ? $"{_nameSyncDone} / {_nameSyncRunTotal}" : "";
        NameSyncDetail.Text = _nameSyncCurrent;

        NameSyncCancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        NameSyncCancel.IsEnabled = running;

        NameSyncStart.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        NameSyncStart.Content = _nameSyncStage == NameSyncStage.Idle ? "开始同步" : "继续同步";
        NameSyncRebuild.IsEnabled = !running;

        NameSyncHint.Text = running
            ? "同步在后台慢慢跑，收起这个面板也不影响，可以继续浏览游戏库。"
            : "关掉面板不会影响任何数据；「重新抓取完整名单」会先清空游戏名表再全量重来。";
    }

    private void ShowNameSyncPanel()
    {
        RefreshNamePanel();
        NameSyncLayer.Visibility = Visibility.Visible;
        NameSyncCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        NameSyncBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void HideNameSyncPanel()
    {
        NameSyncLayer.Visibility = Visibility.Collapsed;
        NameSyncCard.BeginAnimation(OpacityProperty, null);
        NameSyncBackdrop.BeginAnimation(OpacityProperty, null);
        NameSyncCard.Opacity = 0;
        NameSyncBackdrop.Opacity = 0;
    }

    /// <summary>同步每推进一点就刷这里：面板开着能看到，收起来了只更新小胶囊上的百分比。</summary>
    private void UpdateNameSyncProgress(int done, int total, string current)
    {
        _nameSyncDone = done;
        _nameSyncRunTotal = Math.Max(1, total);
        _nameSyncCurrent = current;
        UpdateNameUi();
    }

    /// <summary>小胶囊：开 / 关进度面板。再点一下收起来，方便。</summary>
    private void NamePill_Click(object sender, RoutedEventArgs e)
    {
        if (NameSyncLayer.Visibility == Visibility.Visible)
        {
            HideNameSyncPanel();
            return;
        }

        // 中断提示看过一次就算「知会过了」，胶囊该收了
        _nameSyncFailureDismissed = true;
        UpdateNamePill();

        ShowNameSyncPanel();
    }

    /// <summary>
    /// 设置页「同步中文名」：先把面板打开（能看到正在换哪一款、随时能取消），没在跑就直接开跑；
    /// 已经在跑的话只相当于「查看进度」，不会重复起任务。
    /// </summary>
    private void NameSyncSetting_Click(object sender, RoutedEventArgs e)
    {
        _nameSyncFailureDismissed = true;
        UpdateNamePill();

        ShowNameSyncPanel();

        if (!NameSyncRunning) StartNameSync(rebuild: false);
    }

    private void NameSyncClose_Click(object sender, RoutedEventArgs e) => HideNameSyncPanel();

    private void NameSyncBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => HideNameSyncPanel();

    /// <summary>面板上的「开始 / 继续同步」：只补还没有中文名的那些。</summary>
    private void NameSyncStart_Click(object sender, RoutedEventArgs e) => StartNameSync(rebuild: false);

    /// <summary>面板上的「重新抓取完整名单」：清空重建，最彻底的那种。</summary>
    private void NameSyncRebuild_Click(object sender, RoutedEventArgs e) => StartNameSync(rebuild: true);

    private void NameSyncCancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_nameSyncRunning) return;

        NameSyncCancel.IsEnabled = false;
        _nameSyncPhaseText = "正在取消，已拿到的中文名会保留…";
        RefreshNamePanel();
        _nameSyncCts?.Cancel();
    }

    /// <summary>
    /// 启动时准备游戏名库：表里有货就跳过；空的才去抓一份（只抓英文名，很便宜）。
    /// 只做静默准备——启动不再弹框问「要不要同步中文名」，要换名去「设置 → 数据更新」点一下。
    /// </summary>
    private async Task SeedGameNamesAsync()
    {
        if (!AppDatabase.Instance.IsAvailable) return;

        if (AppDatabase.Instance.GameNameCount() == 0)
        {
            try
            {
                var rows = await TrainerWeb.FetchAllNamesAsync();
                if (rows.Count > 0 && AppDatabase.Instance.GameNameCount() == 0)
                    AppDatabase.Instance.SaveGameNames(rows);
            }
            catch
            {
                // 抓不到就下次启动再试，不影响主流程
            }
        }

        UpdateNameUi();
    }

    /// <summary>同步后新列表是全新对象，把收藏夹里的引用换成新实例（拿最新版本与下载数据）。</summary>
    private void RemapFavorites()
    {
        var byId = _library.Items.ToDictionary(i => i.Id);
        for (var i = 0; i < _favorites.Count; i++)
        {
            if (byId.TryGetValue(_favorites[i].Id, out var live))
                _favorites[i] = live;
        }
    }

    // ==================== 设置 · 数据更新 ====================

    /// <summary>首页空状态里的「同步数据」：和设置里的「同步全部数据」是同一条路。</summary>
    private void EmptySync_Click(object sender, RoutedEventArgs e) => _ = RunSyncAsync(SyncKind.All);

    private void SyncAll_Click(object sender, RoutedEventArgs e) => _ = RunSyncAsync(SyncKind.All);

    private void SyncExisting_Click(object sender, RoutedEventArgs e) => _ = RunSyncAsync(SyncKind.Existing);

    /// <summary>同步进度框是否开着（模态：点遮层、按 Esc 都关不掉）。</summary>
    private bool SyncDialogOpen => SyncLayer.Visibility == Visibility.Visible;

    /// <summary>
    /// 跑一次同步。抓取全在后台，进度通过 <see cref="Progress{T}"/> 回到 UI 线程；
    /// 不管成功、失败还是被取消，结束时都收起弹框并把本地库重读一遍。
    /// </summary>
    /// <param name="kind">同步全部数据 / 同步已有数据。</param>
    private async Task RunSyncAsync(SyncKind kind)
    {
        if (_syncRunning) return;

        // 本地一条都没有时，「同步已有数据」无事可做，先说明白再返回
        if (kind == SyncKind.Existing && AppDatabase.Instance.CacheCount() == 0)
        {
            StatusText.Text = "本地还没有数据，先点「同步全部数据」";
            StatusDot.Fill = (Brush)FindResource("B.Accent");
            return;
        }

        _syncRunning = true;
        _syncCts?.Dispose();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;

        SyncAllButton.IsEnabled = false;
        SyncExistingButton.IsEnabled = false;
        ShowSyncDialog(kind);

        SyncResult result;
        try
        {
            var progress = new Progress<SyncProgress>(OnSyncProgress);

            result = kind == SyncKind.All
                ? await TrainerSyncService.SyncAllAsync(progress, ct)
                : await TrainerSyncService.SyncExistingAsync(progress, ct);
        }
        catch (Exception ex)
        {
            // 服务内部已经吞过一遍，这里兜住「连进度框都撑不住」的极端情况，别让进程直接崩
            result = new SyncResult { Error = ex.Message };
        }
        finally
        {
            CloseSyncDialog();
            SyncAllButton.IsEnabled = true;
            SyncExistingButton.IsEnabled = true;
            _syncRunning = false;
            _syncCts?.Dispose();
            _syncCts = null;
        }

        // 同步写的是本地库，首页跟着重读一遍才看得到新数据
        ReloadFromLocal();

        StatusText.Text = result.Summary;
        StatusDot.Fill = (Brush)FindResource(result.Error is null && !result.Canceled ? "B.Green" : "B.Accent");
    }

    /// <summary>把模态进度框立起来，文案按同步类型切。</summary>
    private void ShowSyncDialog(SyncKind kind)
    {
        SyncTitle.Text = kind == SyncKind.All ? "同步全部数据" : "同步已有数据";
        SyncSubtitle.Text = kind == SyncKind.All
            ? "一页一页抓取官网列表，没改动的自动跳过"
            : "按本地库逐款重抓详情页，补全下载表";

        SyncGameText.Text = "正在连接官网…";
        SyncPhaseText.Text = "";
        SyncCountText.Text = "";
        SyncPercentText.Text = "";
        SyncBar.IsIndeterminate = true;
        SyncBar.Value = 0;
        SyncCancelButton.Content = "取消同步";
        SyncCancelButton.IsEnabled = true;

        SyncLayer.Visibility = Visibility.Visible;
    }

    private void CloseSyncDialog() => SyncLayer.Visibility = Visibility.Collapsed;

    /// <summary>渲染一条进度：游戏名、阶段说明、进度条和计数。</summary>
    private void OnSyncProgress(SyncProgress p)
    {
        if (!SyncDialogOpen) return;

        if (p.Game.Length > 0)
            SyncGameText.Text = p.Phase == SyncPhase.Syncing ? $"正在同步 {p.Game} 游戏" : p.Game;

        SyncPhaseText.Text = p.Detail;

        if (p.Ratio >= 0)
        {
            SyncBar.IsIndeterminate = false;
            SyncBar.Value = Math.Clamp(p.Ratio, 0, 1);
            SyncPercentText.Text = $"{Math.Clamp(p.Ratio, 0, 1) * 100:0}%";
        }
        else
        {
            SyncBar.IsIndeterminate = true;
            SyncPercentText.Text = "";
        }

        if (p.Total <= 0)
        {
            SyncCountText.Text = "";
            return;
        }

        var text = $"已处理 {p.Done} / {p.Total} · 更新 {p.Updated} · 跳过 {p.Skipped}";
        if (p.Failed > 0) text += $" · 失败 {p.Failed}";
        SyncCountText.Text = text;
    }

    /// <summary>取消：只打取消标记，抓取那头自己收尾，已经写进库的数据都保留。</summary>
    private void SyncCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_syncCts is null) return;

        SyncCancelButton.IsEnabled = false;
        SyncCancelButton.Content = "正在取消…";
        SyncPhaseText.Text = "正在停止，已经同步好的部分会保留";
        _syncCts.Cancel();
    }

    // ==================== 设置 · 清除缓存 ====================

    /// <summary>正在等用户点「确定 / 取消」的确认框；同一时间只允许一个。</summary>
    private TaskCompletionSource<bool>? _confirm;

    /// <summary>一键清除缓存：先问一句，确认后清 SQLite 的卡片缓存 + 删掉 f 目录下的所有内容。</summary>
    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (_confirm is not null) return;   // 连点两下只弹一个框
        if (!_resetDone) return;            // 上一次清完还在重新拉首页，别叠着来一遍

        var active = DownloadManager.Instance.Tasks.Count(t => t.IsActive);
        var body = "这样操作会删除本地的缓存数据。" +
                   (active > 0 ? $"\n\n当前还有 {active} 个下载任务，会先被取消。" : "");

        if (!await ConfirmAsync("清除本地缓存？", body, "清除")) return;

        _resetDone = false;                 // 从这里开始，整套清除 + 重新加载都没走完
        ClearCacheButton.IsEnabled = false;
        AppDatabase.MuteCache();            // 掐断在途下载，别让缓存写入和落盘又填回来
        try
        {
            // 1) 先停掉正在下的任务，免得一边删一边又写回来
            foreach (var task in DownloadManager.Instance.Tasks.Where(t => t.IsActive).ToList())
                DownloadManager.Instance.Cancel(task);

            // 2) 等首页加载 / 翻页请求 / 封面下载 / 被取消的任务收尾（否则刚删完又被写回去）
            for (var i = 0; i < 40 &&
                 (_firstLoading || _libraryPager.Loading || _searchPager.Loading ||
                  DownloadManager.Instance.ActiveCount > 0); i++)
                await Task.Delay(100);

            // 3) 清空 SQLite 里的卡片缓存表（收藏夹那张表不动）+ 删掉 f 目录下的所有内容
            var rows = AppDatabase.Instance.ClearCache();
            var (files, bytes, failed) = DeleteContents(AppPaths.FilesDir);

            // 4) 已经在传输中的封面可能还会落盘，多扫几轮直到连续一轮没东西可删
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(400);
                rows += AppDatabase.Instance.ClearCache();

                var (extraFiles, extraBytes, extraFailed) = DeleteContents(AppPaths.FilesDir);
                files += extraFiles;
                bytes += extraBytes;
                failed = extraFailed;

                if (extraFiles == 0 && extraFailed == 0) break;   // 这一轮没东西可删 = 真的空了
            }

            var summary = $"已清除缓存 · {rows} 条缓存记录 · {files} 个文件（{DownloadTask.Human(bytes)}）" +
                          (failed > 0 ? $" · {failed} 项被占用，已跳过" : "");
            StatusText.Text = summary;
            _pendingClearSummary = summary;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"清除失败：{ex.Message}";
        }
        finally
        {
            // 再静音一小会儿，别让马上又开始的下一次下载把刚清掉的东西立刻写回来
            await Task.Delay(800);
            AppDatabase.UnmuteCache();
            ClearCacheButton.IsEnabled = true;
        }

        ResetAfterClear();
    }

    /// <summary>清完缓存后要长时间显示的提示，重新加载列表时会被覆盖，加载完再贴回去。</summary>
    private string _pendingClearSummary = "";

    /// <summary>清完缓存 + 重新加载这一整套流程是否走完（只给自检用来等一个稳定状态）。</summary>
    private bool _resetDone = true;

    /// <summary>
    /// 清完缓存之后，内存里那份列表也得跟着丢掉：
    /// 否则界面还挂着几百张卡片，而「已经收录」按 SQLite 算已经是 0，两个数字互相打脸。
    /// 首页只读本地，所以这里重读一遍库（现在是空的），不再去官网拉数据。
    /// </summary>
    private void ResetAfterClear()
    {
        _resetDone = false;
        try
        {
            if (Searching) ExitSearch(clearText: true);

            // 换代序号 + 分页重置：清空过程中在飞的翻页响应回来后直接丢弃
            _librarySeq++;
            LoadCachedCards();

            Render();
            UpdateStats();
            UpdateQueueBadge();

            // 重读会把状态栏刷成条数信息，把这次清掉多少再贴回去，别让提示一闪就没
            if (!string.IsNullOrEmpty(_pendingClearSummary))
            {
                StatusText.Text = _pendingClearSummary;
                _pendingClearSummary = "";
            }
        }
        finally
        {
            _resetDone = true;
        }
    }

    /// <summary>
    /// 删掉目录下的全部内容（保留目录本身）。单个文件被占用时跳过、不中断，
    /// 返回删掉的文件数、总字节数和失败项数。
    /// </summary>
    private static (int files, long bytes, int failed) DeleteContents(string root)
    {
        if (!Directory.Exists(root)) return (0, 0, 0);

        var files = 0;
        var bytes = 0L;
        var failed = 0;

        // 子目录（f\<id>\）整棵删掉
        foreach (var dir in Directory.EnumerateDirectories(root).ToList())
        {
            try
            {
                var info = new DirectoryInfo(dir);
                var count = 0;
                var size = 0L;
                foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    count++;
                    size += file.Length;
                }

                info.Delete(recursive: true);
                files += count;
                bytes += size;
            }
            catch
            {
                failed++;
            }
        }

        // 根目录下散着的文件
        foreach (var path in Directory.EnumerateFiles(root).ToList())
        {
            try
            {
                bytes += new FileInfo(path).Length;
                File.Delete(path);
                files++;
            }
            catch
            {
                failed++;
            }
        }

        return (files, bytes, failed);
    }

    /// <summary>弹出确认框，等用户点「确定」（true）或「取消 / 点遮罩 / Esc」（false）。</summary>
    /// <param name="primary">确定按钮用主色（普通操作）；默认用警示色（删除 / 清空这类不可逆操作）。</param>
    private Task<bool> ConfirmAsync(string title, string body, string okText, bool primary = false)
    {
        ConfirmTitle.Text = title;
        ConfirmBody.Text = body;
        ConfirmOkButton.Content = okText;
        ConfirmOkButton.Style = (Style)FindResource(primary ? "PrimaryBtn" : "DangerBtn");
        ConfirmLayer.Visibility = Visibility.Visible;

        _confirm = new TaskCompletionSource<bool>();
        ConfirmOkButton.Focus();
        return _confirm.Task;
    }

    private void CloseConfirm(bool result)
    {
        ConfirmLayer.Visibility = Visibility.Collapsed;

        var pending = _confirm;
        _confirm = null;
        pending?.TrySetResult(result);
    }

    private void ConfirmOk_Click(object sender, RoutedEventArgs e) => CloseConfirm(true);

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => CloseConfirm(false);

    /// <summary>点遮罩（弹框外面那片半透明区域）= 取消。遮罩是独立一层，点卡片碰不到它。</summary>
    private void ConfirmBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseConfirm(false);

    // ==================== 关于 / 声明弹窗 ====================

    // 新版 QQ 那套观感：淡入 + 从 0.92 轻微放大（带一点回弹），关闭时略微缩一下再淡出。
    // 用 Animation 而不是 Visibility 硬切，时间都压得比较长，看起来"慢慢打开/合上"。
    private static readonly TimeSpan AboutOpenTime = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan AboutCloseTime = TimeSpan.FromMilliseconds(220);

    /// <summary>每次开关自增，用来识别"关闭动画播到一半又被重新打开"的情况。</summary>
    private int _aboutAnimToken;

    private void About_Click(object sender, RoutedEventArgs e) => ShowAbout();

    private void ShowAbout()
    {
        ++_aboutAnimToken;
        AboutLayer.Visibility = Visibility.Visible;

        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        // 回弹：Amplitude 越大弹得越明显；0.5 实测大约多冲出去 0.5% 再回落，肉眼刚好能察觉
        var spring = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };

        AboutBackdrop.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(240))) { EasingFunction = fade });

        AboutCard.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170))) { EasingFunction = fade });

        AboutScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.93, 1, new Duration(AboutOpenTime)) { EasingFunction = spring });
        AboutScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.93, 1, new Duration(AboutOpenTime)) { EasingFunction = spring });

        AboutShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, new Duration(AboutOpenTime)) { EasingFunction = fade });
    }

    private void CloseAbout()
    {
        if (AboutLayer.Visibility != Visibility.Visible)
        {
            return;
        }

        var token = ++_aboutAnimToken;
        var fade = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(AboutCloseTime);

        var cardFade = new DoubleAnimation(1, 0, duration) { EasingFunction = fade };

        // 注意：Completed 必须在 BeginAnimation 之前挂，先启动再挂回调的话永远不会触发
        cardFade.Completed += (_, _) =>
        {
            // 播完时如果用户又点开了（token 变了），就别收层、也别清动画
            if (token != _aboutAnimToken)
            {
                return;
            }

            AboutLayer.Visibility = Visibility.Collapsed;

            // 清掉动画值，让元素回到 XAML 里的初始态（0.92 / 透明），下次打开才能从头播
            AboutCard.BeginAnimation(OpacityProperty, null);
            AboutBackdrop.BeginAnimation(OpacityProperty, null);
            AboutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            AboutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            AboutShift.BeginAnimation(TranslateTransform.YProperty, null);
        };

        // 卡片先淡出、缩一点点（0.97）再收层，比直接消失柔和
        AboutCard.BeginAnimation(OpacityProperty, cardFade);
        AboutScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        AboutScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        AboutShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, 6, duration) { EasingFunction = fade });
        AboutBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration) { EasingFunction = fade });
    }

    private void AboutClose_Click(object sender, RoutedEventArgs e) => CloseAbout();

    private void AboutBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseAbout();

    // ==================== 版本 / 外链 ====================

    /// <summary>当前程序版本，取程序集版本号（csproj 里的 &lt;Version&gt;）。</summary>
    private static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>用系统默认浏览器打开链接；失败就把原因写进状态栏，不弹异常。</summary>
    private void OpenExternal(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打不开浏览器：{ex.Message}";
        }
    }

    // ==================== 窗口 ====================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 窗口是固定尺寸（不能拉大缩小），双击标题栏也不做最大化，只负责拖动
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* 忽略拖动异常 */ }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 收进托盘：不是「最小化」，是把窗口真正藏起来（SW_HIDE 那一档）——
    /// 任务栏按钮、Alt+Tab 条目、屏幕左下角那截小窗一起消失，只剩右下角的托盘图标。
    /// </summary>
    private void HideToTray()
    {
        // 记一下藏之前是普通还是最大化，回来时原样还原
        if (WindowState != WindowState.Minimized) _stateBeforeTray = WindowState;

        // 只调 Hide()：可见性交给系统，任务栏按钮会被真正移除。
        // 别再写 ShowInTaskbar=false —— 那个属性会重建任务栏按钮，反而在左下角留下残影
        Hide();
    }

    /// <summary>
    /// 「启动程序」成功之后把自己收进托盘：停 0.6 秒，一来让 Toast 露个脸，
    /// 二来等修改器的窗口先拿到前台，不跟它抢焦点。收进去就是静默的，不弹任何提示。
    /// </summary>
    private void HideForLaunchedApp()
    {
        if (_exitRequested) return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_exitRequested) return;

            // 直接藏，不弹气泡 / 系统通知：托盘图标本来就在，别再打扰一次
            HideToTray();
        };
        timer.Start();
    }

    /// <summary>
    /// 把主界面亮出来并置前。托盘菜单用它；「双击第二次图标」时也走这里
    /// （那时候窗口可能正藏在托盘里）。
    /// </summary>
    public void ShowMainWindow()
    {
        if (!IsVisible) Show();

        // 隐藏期间窗口状态可能被系统压成 Minimized，按藏之前的样子（普通 / 最大化）还原
        if (WindowState == WindowState.Minimized) WindowState = _stateBeforeTray;

        Activate();
        Topmost = true;      // 抢一次前台：Windows 不允许后台进程直接抢焦点，先置顶再放开
        Topmost = false;
        Focus();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        _trayIcon.Visible = false;

        // 后台还在换中文名的话，先把它掐掉再退，别留一堆 http 请求挂着
        try { _nameSyncCts?.Cancel(); } catch { }

        System.Windows.Application.Current.Shutdown();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
        {
            _trayIcon.Visible = false;
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Close();
        _trayIconImage.Dispose();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 同步框是模态的：Esc 不关它，要停只能点「取消同步」
        if (e.Key == Key.Escape && SyncDialogOpen)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && NameSyncLayer.Visibility == Visibility.Visible)
        {
            // 只是收起面板，后台同步继续跑
            HideNameSyncPanel();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && AboutLayer.Visibility == Visibility.Visible)
        {
            CloseAbout();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _confirm is not null)
        {
            CloseConfirm(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _detailOpen)
        {
            CloseDetail();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SuggestPopup.IsOpen)
        {
            // 提示列表开着时，Esc 只收提示，不动搜索框里的字（和搜索框自己的处理一致）
            CloseSuggest();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Searching)
        {
            // 搜索框有焦点时自己的 KeyDown 已经处理过，这里兜住「焦点不在搜索框」的情况
            ExitSearch(clearText: true);
            e.Handled = true;
        }
        else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

#if DEBUG
    // ==================== 开发期自检（--capture，仅 Debug 编译） ====================

    /// <summary>
    /// 托盘自检：模拟「启动程序成功 → 收进托盘」、拍托盘右键菜单、再从托盘恢复主界面，
    /// 把每一步的窗口状态写进 tray-state.txt，顺带整屏截一张看托盘图标在不在。
    /// </summary>
    public async Task RunTrayCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "tray-state.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(1500);

        void Note(string step)
        {
            // OS 级可见性：只有它是 False，才算真的「跟系统隐藏一样」，任务栏按钮才会消失
            var hwnd = new WindowInteropHelper(this).Handle;
            var osVisible = hwnd != IntPtr.Zero && IsWindowVisible(hwnd);

            File.AppendAllText(log,
                $"{step}: OsVisible={osVisible} IsVisible={IsVisible} State={WindowState} " +
                $"InTaskbar={ShowInTaskbar} TrayIconVisible={_trayIcon.Visible}\n");
        }

        Note("01-startup");

        // 1) 详情页点「启动程序」：真的启动一个 exe，成功后主界面应自动缩进托盘
        var item = _library.Items.FirstOrDefault(i => i.Groups.Count > 0) ?? _library.Items.FirstOrDefault();
        if (item is not null)
        {
            OpenDetail(item);
            await Task.Delay(900);
        }

        // 借 cmd.exe 当「修改器」：复制一份改名，启动后按名字精确回收，不碰用户的进程
        var beforeW = ActualWidth;
        var beforeH = ActualHeight;

        var fake = Path.Combine(dir, "TestTrainer.exe");
        var launchedByName = "TestTrainer";
        try
        {
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fake, true);
            if (DetailHost.Content is TrainerDetailView launcher) launcher.LaunchFile(fake);
            else HideForLaunchedApp();
        }
        catch
        {
            HideForLaunchedApp();
        }

        await Task.Delay(1800);
        Note("02-after-launch-hide");

        foreach (var process in System.Diagnostics.Process.GetProcessesByName(launchedByName))
        {
            try { process.Kill(); } catch { /* 已经退了 */ }
        }

        // 2) 右键托盘图标弹出的菜单
        _trayMenu.ShowAtCursor();
        await Task.Delay(700);
        SaveShotOf(_trayMenu, Path.Combine(dir, "tray-menu.png"));

        // 把鼠标挪到第一项上，拍一张悬停态（拍完还原鼠标位置）
        var saved = WinForms.Cursor.Position;
        var hoverPoint = _trayMenu.PointToScreen(new System.Windows.Point(70, 42));
        SetCursorPos((int)hoverPoint.X, (int)hoverPoint.Y);
        await Task.Delay(400);
        SaveShotOf(_trayMenu, Path.Combine(dir, "tray-menu-hover.png"));
        SetCursorPos(saved.X, saved.Y);

        CaptureScreen(Path.Combine(dir, "tray-screen.png"));
        _trayMenu.Hide();
        await Task.Delay(300);

        // 3) 菜单里的「打开主界面」
        ShowMainWindow();
        await Task.Delay(900);
        Note("03-after-open-main");
        File.AppendAllText(log, $"窗口尺寸 {beforeW:F0}x{beforeH:F0} → {ActualWidth:F0}x{ActualHeight:F0}\n");
        SaveShot(Path.Combine(dir, "tray-restored.png"));
    }

    /// <summary>
    /// 自检：先藏进托盘，再从托盘「退出程序」——验证隐藏状态下点退出，进程真的会退干净
    /// （隐藏窗口如果挡住了关窗流程，这条命令就会一直挂着）。
    /// </summary>
    public async Task RunTrayExitCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "tray-exit.txt");
        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(800);

        HideToTray();
        var hwnd = new WindowInteropHelper(this).Handle;
        File.AppendAllText(log,
            $"hidden: OsVisible={(hwnd != IntPtr.Zero && IsWindowVisible(hwnd))} TrayIconVisible={_trayIcon.Visible}\n");

        await Task.Delay(600);
        ExitFromTray();
        File.AppendAllText(log, "exit-called\n");
    }

    /// <summary>
    /// 自检：收藏夹 / 设置页的版式，以及「一键清除缓存」整条链路
    /// （点按钮 → 弹确认框 → 点确定 → 真清 SQLite + 真删 f 目录）。
    /// </summary>
    public async Task RunClearCacheCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "clear.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(1500);

        // 首屏的封面还在陆续落盘，先等它们安静下来（正常使用时点按钮也是这个时机）
        for (var i = 0; i < 60 && DownloadManager.Instance.ActiveCount > 0; i++) await Task.Delay(100);
        await Task.Delay(2500);

        // 先造两个假文件，验证 f 目录真的被清空（含子目录与根目录下的散文件）
        var probe = AppPaths.FilesFor(990001);
        File.WriteAllText(Path.Combine(probe, "probe.bin"), new string('x', 4096));
        File.WriteAllText(Path.Combine(AppPaths.FilesDir, "loose.txt"), "loose");

        NavList.SelectedIndex = 1;               // 收藏夹
        await Task.Delay(700);
        SaveShot(Path.Combine(dir, "page-favorites.png"));

        NavList.SelectedIndex = 3;               // 设置
        await Task.Delay(700);
        SaveShot(Path.Combine(dir, "page-settings.png"));

        var rowsBefore = AppDatabase.Instance.CacheCount();
        var filesBefore = Directory.GetFiles(AppPaths.FilesDir, "*", SearchOption.AllDirectories).Length;
        var bytesBefore = Directory.GetFiles(AppPaths.FilesDir, "*", SearchOption.AllDirectories)
            .Sum(p => new FileInfo(p).Length);

        ClearCacheButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Task.Delay(600);
        SaveShot(Path.Combine(dir, "confirm.png"));

        ConfirmOkButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        // 按钮恢复可用 = 清除流程（含多轮扫尾 + 解除静音）真正跑完，最多等 12 秒
        for (var i = 0; i < 120 && !ClearCacheButton.IsEnabled; i++) await Task.Delay(100);

        // 再等「清完缓存 + 重读本地库」这套流程整走完，否则量到的是中间态。
        // 注意先等一会儿：点「确定」只是把确认框关掉，真正的清除是接着才在调度器上跑起来的。
        await Task.Delay(1000);

        var timeline = new System.Text.StringBuilder();
        for (var i = 0; i < 400; i++)
        {
            if (i % 10 == 0)
                timeline.Append($"{i * 100}ms resetDone={_resetDone} firstLoading={_firstLoading} " +
                                $"items={_library.Items.Count} cache={AppDatabase.Instance.CacheCount()}\n");
            if (_resetDone && !_firstLoading) break;
            await Task.Delay(100);
        }
        await Task.Delay(1200);   // 解除静音后再观察一会儿，确认没有文件被重新写回来

        var rowsAfter = AppDatabase.Instance.CacheCount();
        var filesAfter = Directory.GetFiles(AppPaths.FilesDir, "*", SearchOption.AllDirectories).Length;
        var dirsAfter = Directory.GetDirectories(AppPaths.FilesDir).Length;

        var statusOnSettings = StatusText.Text;

        // 回到游戏库，看「已经收录」这个数字到底取的是哪一份数据
        NavList.SelectedIndex = 0;
        await Task.Delay(700);
        var subtitle = SectionSubtitle.Text;
        var statusOnLibrary = StatusText.Text;

        File.AppendAllText(log,
            $"清除前：缓存 {rowsBefore} 条 / f 目录 {filesBefore} 个文件（{DownloadTask.Human(bytesBefore)}）\n" +
            $"清除后：缓存 {rowsAfter} 条 / f 目录 {filesAfter} 个文件 / {dirsAfter} 个子目录\n" +
            $"确认框：{(ConfirmLayer.Visibility == Visibility.Visible ? "仍打开" : "已关闭")}\n" +
            $"设置页状态栏：{statusOnSettings}\n" +
            $"游戏库副标题：{subtitle}\n" +
            $"游戏库状态栏：{statusOnLibrary}\n" +
            $"内存条数：{_library.Items.Count} / SQLite：{AppDatabase.Instance.CacheCount()}\n" +
            $"加载状态：firstLoading={_firstLoading} page={_libraryPager.Page}/{_libraryPager.TotalPages} " +
            $"total={_libraryPager.TotalCount} loading={_libraryPager.Loading}\n" +
            $"错误：{_firstError ?? "无"}\n" +
            $"时间线：\n{timeline}");

        SaveShot(Path.Combine(dir, "after-clear.png"));
    }

    /// <summary>
    /// 自检：第二次启动时那个「已经启动」提示框，单独拍一张图看版式。
    /// </summary>
    public async Task RunTipCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        Hide();
        await Task.Delay(400);

        var tip = new AlreadyRunningWindow();
        tip.Show();
        await Task.Delay(700);
        SaveShotOf(tip, Path.Combine(dir, "already-running.png"));

        tip.Close();
        await Task.Delay(200);
    }

    /// <summary>
    /// 自检：详情页的开关动画。验证三件事——开场缩放/淡入真的在动、动画播完能清掉位图缓存
    /// 接着把选项加载出来、点遮罩能关掉且关完真的收了层（不是"透明但挡着点击"）。
    /// </summary>
    public async Task RunDetailCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "detail.txt");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        await Task.Delay(600);

        var item = _library.Items[0];
        var timeline = new System.Text.StringBuilder();

        // ---- 开场：从 0.96 起跳，看缩放/透明度随时间怎么走 ----
        OpenDetail(item);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var peak = 0.0;
        var openedAt = -1L;
        for (var i = 0; i < 120; i++)
        {
            var s = DetailScale.ScaleX;
            peak = Math.Max(peak, s);
            timeline.Append($"{watch.ElapsedMilliseconds,4}ms scale={s:0.0000} op={DetailHost.Opacity:0.00} " +
                            $"mask={DetailBackdrop.Opacity:0.00} cache={(DetailHost.CacheMode is null ? "无" : "有")}\n");

            // 位图缓存被清掉 = 动画真的播完、Completed 回调真的跑了（先启动后挂回调的话这里永远是"有"）
            if (openedAt < 0 && DetailHost.CacheMode is null && s > 0.999)
            {
                openedAt = watch.ElapsedMilliseconds;
                break;
            }

            await Task.Delay(20);
        }

        await Task.Delay(300);
        SaveShot(Path.Combine(dir, "01-detail-open.png"));

        // ---- 卡片中心点落在谁身上（应该落在卡片里的元素，而不是遮罩）----
        var center = new Point(DetailHost.ActualWidth / 2, DetailHost.ActualHeight / 2);
        var hit = VisualTreeHelper.HitTest(DetailHost, center);
        var hitName = hit?.VisualHit is null ? "(null)" : hit.VisualHit.GetType().Name;
        var cardOwnsCenter = hit?.VisualHit is not null &&
                             (hit.VisualHit is not System.Windows.Shapes.Rectangle r || r.Name != "DetailBackdrop");

        // ---- 关掉：等 500ms 再看是不是真的收层了 ----
        var maskClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent
        };
        DetailBackdrop.RaiseEvent(maskClick);
        await Task.Delay(500);

        var collapsed = DetailLayer.Visibility != Visibility.Visible;
        var cleared = DetailHost.Content is null;
        var resetOpacity = Math.Abs(DetailHost.Opacity) < 0.01;
        SaveShot(Path.Combine(dir, "02-detail-closed.png"));

        // ---- 再开一次：确认收层后还能正常打开（动画值有没有被清回初始态）----
        OpenDetail(item);
        await Task.Delay(500);
        var reopened = DetailLayer.Visibility == Visibility.Visible && DetailHost.Opacity > 0.99 &&
                       Math.Abs(DetailScale.ScaleX - 1) < 0.002;

        File.WriteAllText(log,
            $"卡片中心命中：{hitName}（{(cardOwnsCenter ? "是卡片内容（对）" : "是遮罩（错）")}）\n" +
            $"开场动画落定：{openedAt}ms（>0 即 Completed 已触发并清掉了位图缓存）\n" +
            $"缩放峰值：{peak:0.0000}（起点 {DetailScaleFrom}，应 >1 表示有回弹）\n" +
            $"点遮罩后：{(collapsed ? "已收层（对）" : "仍显示（错）")} / " +
            $"内容{(cleared ? "已释放" : "未释放（错）")} / 透明度{(resetOpacity ? "已归零" : $"={DetailHost.Opacity:0.00}（错）")}\n" +
            $"再次打开：{(reopened ? "正常（动画值已回到初始态）" : "异常")}\n" +
            $"时间线：\n{timeline}");
    }

    /// <summary>
    /// 自检：确认框的遮罩点击。真的发一次鼠标点击（不是 RaiseEvent），
    /// 验证「点遮罩 = 取消」「点卡片 = 不关」，并把每一步写进 mask.txt。
    /// </summary>
    public async Task RunMaskCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "mask.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(1200);

        NavList.SelectedIndex = 3;               // 设置
        await Task.Delay(700);
        ClearCacheButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Task.Delay(700);
        SaveShot(Path.Combine(dir, "01-confirm.png"));

        // 遮罩上的取样点：窗口右下角，离卡片很远，一定是「弹框外面」
        var maskPoint = new Point(ActualWidth - 70, ActualHeight - 110);
        LogHit(maskPoint, "遮罩取样点");

        // 先点卡片内部的空白（正文那行），弹框应该无动于衷
        var bodyPoint = Center(ConfirmBody);
        File.AppendAllText(log, $"卡片内取样点 {bodyPoint} → {HitName(bodyPoint)}\n");
        await ClickAsync(bodyPoint);
        await Task.Delay(400);
        File.AppendAllText(log, $"点卡片内部后：{(ConfirmLayer.Visibility == Visibility.Visible ? "仍打开（对）" : "被关掉了（错）")}\n");
        SaveShot(Path.Combine(dir, "02-after-card-click.png"));

        // 再点遮罩，弹框应当关闭
        await ClickAsync(maskPoint);
        await Task.Delay(600);
        File.AppendAllText(log, $"点遮罩后：{(ConfirmLayer.Visibility == Visibility.Visible ? "仍打开（错）" : "已关闭（对）")}\n");
        SaveShot(Path.Combine(dir, "03-after-mask-click.png"));

        // 重开一次，确认连点遮罩两下也不会把确认框卡住
        ClearCacheButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Task.Delay(600);
        await ClickAsync(new Point(ActualWidth - 70, 90));       // 这次点右上角
        await Task.Delay(500);
        File.AppendAllText(log, $"第二次开框后点右上遮罩：{(ConfirmLayer.Visibility == Visibility.Visible ? "仍打开（错）" : "已关闭（对）")}\n");

        string HitName(Point p)
        {
            var hit = VisualTreeHelper.HitTest(ConfirmLayer, p);
            DependencyObject? v = hit?.VisualHit;
            while (v is not null && v is not FrameworkElement) v = VisualTreeHelper.GetParent(v);
            return v is FrameworkElement fe ? $"{fe.GetType().Name}({fe.Name})" : "null";
        }

        void LogHit(Point p, string label) =>
            File.AppendAllText(log, $"{label} {p} → {HitName(p)}\n");

        Point Center(FrameworkElement element)
        {
            var local = new Point(element.ActualWidth / 2, element.ActualHeight / 2);
            return element.TranslatePoint(local, ConfirmLayer);
        }
    }

    /// <summary>
    /// 自检：「数据更新」的设置卡片和同步弹框。不联网，进度是喂进去的假数据，
    /// 重点验证三件事——排版正常、点遮层关不掉、按 Esc 也关不掉（只能取消）。
    /// </summary>
    public async Task RunSyncUiCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "sync-ui.txt");
        File.WriteAllText(log, "");

        await Task.Delay(1500);

        NavList.SelectedIndex = 3;               // 设置
        await Task.Delay(900);
        SaveShot(Path.Combine(dir, "01-settings.png"));

        File.AppendAllText(log,
            $"设置页可见={SettingsPanel.Visibility == Visibility.Visible} " +
            $"同步全部按钮={(SyncAllButton.IsEnabled ? "可点" : "不可点")}\n");

        // 直接开弹框喂假进度：渲染路径和真实同步完全一样
        ShowSyncDialog(SyncKind.All);
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "02-sync-open.png"));

        Feed(SyncPhase.Syncing, "Assassin's Creed Mirage", "第 3 / 52 页 · 正在更新", 45, 780, 0.06, 43, 2);
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "03-sync-progress.png"));

        File.AppendAllText(log,
            $"进度行=「{SyncGameText.Text}」 说明=「{SyncPhaseText.Text}」 " +
            $"计数=「{SyncCountText.Text}」 百分比=「{SyncPercentText.Text}」 " +
            $"进度条不确定态={SyncBar.IsIndeterminate}\n");

        // 1) 点遮层（窗口右下角，离卡片很远）：弹框必须纹丝不动
        var backdrop = new Point(ActualWidth - 70, ActualHeight - 110);
        await ClickAsync(backdrop);
        await Task.Delay(400);
        File.AppendAllText(log,
            $"点遮层后：{(SyncDialogOpen ? "仍打开（对）" : "被关掉了（错）")}\n");
        SaveShot(Path.Combine(dir, "04-after-mask-click.png"));

        // 2) 按 Esc：同样不该关
        var esc = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        RaiseEvent(esc);
        await Task.Delay(300);
        File.AppendAllText(log,
            $"按 Esc 后：{(SyncDialogOpen ? "仍打开（对）" : "被关掉了（错）")} Esc 被吃掉={esc.Handled}\n");

        // 3) 没有正在跑的任务时点「取消同步」应当什么都不发生（不能抛异常 / 不能关框）
        SyncCancelButton.RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Task.Delay(300);
        File.AppendAllText(log,
            $"无任务时点取消：{(SyncDialogOpen ? "仍打开（对）" : "被关掉了（错）")}\n");

        // 收尾
        CloseSyncDialog();
        await Task.Delay(400);
        SaveShot(Path.Combine(dir, "05-closed.png"));
        File.AppendAllText(log, $"收尾后弹框可见={SyncDialogOpen}\n");

        void Feed(SyncPhase phase, string game, string detail, int done, int total,
            double ratio, int updated, int skipped)
        {
            OnSyncProgress(new SyncProgress
            {
                Phase = phase,
                Game = game,
                Detail = detail,
                Done = done,
                Total = total,
                Ratio = ratio,
                Updated = updated,
                Skipped = skipped
            });
        }
    }

    /// <summary>
    /// 自检：本地一条数据都没有时的首页空状态。应当直接显示「同步数据」按钮 + 那句描述，
    /// 点下去就等同「同步全部数据」（弹进度框），不再有任何启动弹框。
    /// 需要先清空本地库再跑，跑完会把数据重新抓一部分回来。
    /// </summary>
    public async Task RunEmptySyncCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "empty.txt");
        File.WriteAllText(log, "");

        await Task.Delay(1500);

        File.AppendAllText(log,
            $"本地卡片={_library.Items.Count} 空状态={EmptyState.Visibility} " +
            $"同步按钮={EmptySyncButton.Visibility} 文案=「{EmptyDesc.Text}」\n");
        SaveShot(Path.Combine(dir, "01-empty.png"));

        EmptySyncButton.RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Task.Delay(1800);

        File.AppendAllText(log,
            $"点同步后：弹框={(SyncDialogOpen ? "开（对）" : "关（错）")} 标题=「{SyncTitle.Text}」 " +
            $"进度=「{SyncPhaseText.Text}」\n");
        SaveShot(Path.Combine(dir, "02-dialog.png"));

        SyncCancelButton.RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        for (var i = 0; i < 200 && SyncDialogOpen; i++) await Task.Delay(100);
        await Task.Delay(600);

        File.AppendAllText(log,
            $"取消后：弹框={(SyncDialogOpen ? "仍开着（错）" : "已关（对）")} " +
            $"本地条数={AppDatabase.Instance.CacheCount()} " +
            $"首页卡片={_library.Items.Count} 同步按钮={EmptySyncButton.Visibility}\n");
        SaveShot(Path.Combine(dir, "03-after.png"));
    }

    /// <summary>
    /// 自检：设置页「同步中文名」按钮。点一下应当打开进度面板并真的开始跑（复用原来的同步逻辑），
    /// 按钮变「查看进度」、副标题跟着百分比走；取消之后回到「继续同步」。
    /// 会真的向 Steam 发几秒请求（和用户自己点按钮完全一致）。
    /// </summary>
    public async Task RunNameSettingCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "name-setting.txt");
        File.WriteAllText(log, "");

        await Task.Delay(1500);

        // 名单库是空的就先等启动那轮抓完，不然点下去会先走「抓 A-Z」那条路
        for (var i = 0; i < 300 && AppDatabase.Instance.GameNameCount() == 0; i++) await Task.Delay(100);

        NavList.SelectedIndex = 3;               // 设置
        await Task.Delay(800);

        File.AppendAllText(log,
            $"点之前：副标题=「{NameSyncSettingDesc.Text}」按钮=「{NameSyncButton.Content}」 " +
            $"面板={(NameSyncLayer.Visibility == Visibility.Visible ? "开（不对）" : "关（对）")}\n");
        SaveShot(Path.Combine(dir, "01-before.png"));

        NameSyncButton.RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Task.Delay(1500);

        File.AppendAllText(log,
            $"点之后：面板={(NameSyncLayer.Visibility == Visibility.Visible ? "开（对）" : "关（错）")} " +
            $"running={NameSyncRunning} 按钮=「{NameSyncButton.Content}」副标题=「{NameSyncSettingDesc.Text}」\n" +
            $"面板文案：标题=「{NameSyncTitle.Text}」阶段=「{NameSyncPhase.Text}」计数=「{NameSyncCount.Text}」\n");
        SaveShot(Path.Combine(dir, "02-running.png"));

        await Task.Delay(3000);
        File.AppendAllText(log,
            $"4 秒后：胶囊={(NamePill.Visibility == Visibility.Visible ? "显示（对）" : "隐藏（错）")} " +
            $"副标题=「{NameSyncSettingDesc.Text}」\n");
        SaveShot(Path.Combine(dir, "03-filling.png"));

        // 面板上的「取消同步」：停掉后台，已拿到的中文名保留
        NameSyncCancel.RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        for (var i = 0; i < 200 && NameSyncRunning; i++) await Task.Delay(100);
        await Task.Delay(800);

        File.AppendAllText(log,
            $"取消后：running={NameSyncRunning} 按钮=「{NameSyncButton.Content}」 " +
            $"副标题=「{NameSyncSettingDesc.Text}」面板={NameSyncLayer.Visibility}\n");
        SaveShot(Path.Combine(dir, "04-cancelled.png"));
    }

    /// <summary>
    /// 自检：走真实按钮路径跑一次「同步全部数据」。点按钮 → 弹框 → 采样进度 →
    /// 点「取消同步」→ 等弹框收起，把过程中看到的每一行文案写盘。
    /// 会真的往本地库里写数据（和用户点按钮的效果完全一致）。
    /// </summary>
    /// <param name="dir">输出目录。</param>
    /// <param name="seconds">同步持续多少秒后自动取消。</param>
    public async Task RunSyncRunCheckAsync(string dir, int seconds)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "sync-run.txt");
        File.WriteAllText(log, "");

        await Task.Delay(1200);

        NavList.SelectedIndex = 3;               // 设置
        await Task.Delay(600);

        var before = AppDatabase.Instance.CacheCount();
        var samples = new List<string>();

        SyncAllButton.RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Task.Delay(1500);
        File.AppendAllText(log, $"点按钮后弹框可见={SyncDialogOpen}\n");

        var limit = Math.Max(4, seconds) * 4;
        for (var i = 0; i < limit; i++)
        {
            if (i % 4 == 0)
                samples.Add($"{i * 250}ms | {SyncGameText.Text} | {SyncPhaseText.Text} | " +
                            $"{SyncCountText.Text} | {SyncPercentText.Text}");

            if (!SyncDialogOpen) break;
            await Task.Delay(250);
        }

        SaveShot(Path.Combine(dir, "01-during-sync.png"));

        SyncCancelButton.RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        for (var i = 0; i < 200 && SyncDialogOpen; i++) await Task.Delay(100);

        File.WriteAllText(log, string.Join("\n", samples) + "\n");
        File.AppendAllText(log,
            $"\n取消后弹框可见={SyncDialogOpen}\n" +
            $"状态栏=「{StatusText.Text}」\n" +
            $"缓存条数：{before} → {AppDatabase.Instance.CacheCount()}\n" +
            $"首页条数={_library.Items.Count} 副标题=「{SectionSubtitle.Text}」\n");

        SaveShot(Path.Combine(dir, "02-after-sync.png"));
    }

    /// <summary>在窗口坐标系里真的发一次左键单击（光标也移过去，和用户点没区别）。</summary>
    private async Task ClickAsync(Point point)
    {
        var screen = PointToScreen(point);
        SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        await Task.Delay(90);

        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(70);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(90);
    }

    /// <summary>
    /// 自检：上下分页。核对第 1 页的总数 / 页码 / 卡片数，点「下一页」后页码与内容是否跟着走，
    /// 再点「上一页」回到第 1 页（应走缓存、不重新请求），顺带验证详情页封面是打开即有。
    /// </summary>
    public async Task RunPageCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "page.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        await Task.Delay(1800);   // 封面陆续落盘，等一下再截图

        void Note(string step) => File.AppendAllText(log,
            $"{step}：副标题=「{SectionSubtitle.Text}」 状态=「{StatusText.Text}」 " +
            $"页码=「{PagerPageText.Text}」 总数=「{PagerTotalText.Text}」 卡片={_items.Count} " +
            $"分页栏={(PagerBar.Visibility == Visibility.Visible ? "显示" : "隐藏")} " +
            $"上一页可点={PagerPrev.IsEnabled} 下一页可点={PagerNext.IsEnabled}\n");

        Note("01-第1页");
        SaveShot(Path.Combine(dir, "01-page1.png"));

        var page1Ids = _items.Select(i => i.Id).ToList();

        PagerNext.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        for (var i = 0; i < 200 && _libraryPager.Loading; i++) await Task.Delay(100);
        await Task.Delay(900);
        Note("02-下一页");
        var page2Ids = _items.Select(i => i.Id).ToList();
        SaveShot(Path.Combine(dir, "02-page2.png"));

        PagerPrev.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Task.Delay(700);
        Note("03-上一页（应走缓存）");
        var backIds = _items.Select(i => i.Id).ToList();
        SaveShot(Path.Combine(dir, "03-page1-again.png"));

        File.AppendAllText(log,
            $"第 1 页 {page1Ids.Count} 条 / 第 2 页 {page2Ids.Count} 条 / 回到第 1 页 {backIds.Count} 条\n" +
            $"两页内容重叠：{page1Ids.Intersect(page2Ids).Count()} 条（应为 0）\n" +
            $"回到第 1 页是否与首次一致：{(backIds.SequenceEqual(page1Ids) ? "是" : "否")}\n" +
            $"请求过的页：{string.Join(",", _libraryPager.Cache.Keys.OrderBy(k => k))}\n");

        // ---- 详情页封面：点开瞬间就该有图，不再等开场动画 ----
        var item = _items.FirstOrDefault();
        if (item is not null)
        {
            OpenDetail(item);
            var immediate = DetailHost.Content is TrainerDetailView v && v.BannerReady;
            await Task.Delay(60);
            SaveShot(Path.Combine(dir, "04-detail-open.png"));
            File.AppendAllText(log, $"详情页打开瞬间封面已就绪：{(immediate ? "是" : "否")}\n");

            // 等开场动画彻底落定再拍一张，核对详情页本身的版式与亮度
            await Task.Delay(900);
            SaveShot(Path.Combine(dir, "05-detail-settled.png"));

            CloseDetail();
            await Task.Delay(400);
        }

        // 其余几页顺带拍一张，核对提亮之后的观感
        NavList.SelectedIndex = 1;
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "06-favorites.png"));

        NavList.SelectedIndex = 3;
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "07-settings.png"));

        NavList.SelectedIndex = 2;
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "08-queue.png"));

        NavList.SelectedIndex = 0;
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "09-back-to-library.png"));

        // ---- 搜索结果同样走上下分页 ----
        SearchBox.Text = "assassin";
        await SubmitSearchAsync();
        await Task.Delay(500);
        CloseSuggest();
        File.AppendAllText(log,
            $"搜索：副标题=「{SectionSubtitle.Text}」 状态=「{StatusText.Text}」 " +
            $"页码=「{PagerPageText.Text}」 总数=「{PagerTotalText.Text}」 卡片={_items.Count}\n");
        SaveShot(Path.Combine(dir, "10-search.png"));

        if (_searchPager.TotalPages > 1)
        {
            PagerNext.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            for (var i = 0; i < 200 && _searchPager.Loading; i++) await Task.Delay(100);
            await Task.Delay(600);
            File.AppendAllText(log,
                $"搜索第 2 页：页码=「{PagerPageText.Text}」 卡片={_items.Count} " +
                $"状态=「{StatusText.Text}」\n");
            SaveShot(Path.Combine(dir, "11-search-page2.png"));
        }

        ExitSearch(clearText: true);
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "12-after-search-exit.png"));
    }

#if DEBUG
    /// <summary>
    /// 自检：一页超过 15 条时卡片不再被压小、列表能滚动。
    /// 只往内存里把当前页再叠一份（30 条），不发请求也不写库，测完 <see cref="Render"/> 还原。
    /// </summary>
    public async Task RunScrollCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "scroll.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        await Task.Delay(1500);

        var scroll = _listScroll ??= FindScrollViewer(CardList);

        void Note(string tag) => File.AppendAllText(log,
            $"{tag}：卡片 {_cardW}x{_cardH} · 行数 {_rows.Count} · 条数 {_items.Count} · " +
            $"可视高 {scroll?.ViewportHeight:F0} 内容高 {scroll?.ExtentHeight:F0} " +
            $"可滚高度 {scroll?.ScrollableHeight:F0}\n");

        Note("15 条");
        SaveShot(Path.Combine(dir, "01-fifteen.png"));

        var snapshot = _items.ToList();
        var doubled = new List<TrainerItem>(snapshot);
        doubled.AddRange(snapshot);
        SetItems(doubled);
        await Task.Delay(800);

        Note("30 条");
        SaveShot(Path.Combine(dir, "02-thirty.png"));

        scroll?.ScrollToVerticalOffset(260);
        await Task.Delay(350);
        File.AppendAllText(log, $"滚到 260 之后实际位置：{scroll?.VerticalOffset:F0}\n");
        SaveShot(Path.Combine(dir, "03-scrolled.png"));

        Render();
        await Task.Delay(600);
        Note("还原");
        SaveShot(Path.Combine(dir, "04-restored.png"));
    }

    /// <summary>
    /// 自检：游戏名库。核对「表为空时启动自动抓全量」有没有落库、本地提示能不能查到、
    /// Steam 换中文名通不通，最后把同步进度弹窗摆出来拍一张。
    /// </summary>
    public async Task RunNameCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "names.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);

        // 启动时的种子任务是后台跑的，这里等它落库（最多 90 秒）
        var waited = 0;
        while (AppDatabase.Instance.GameNameCount() == 0 && waited < 900)
        {
            await Task.Delay(100);
            waited++;
        }

        await Task.Delay(800);

        var count = AppDatabase.Instance.GameNameCount();
        File.AppendAllText(log,
            $"游戏名库条数：{count}（等待种子 {waited * 100} ms）\n" +
            $"库里前 3 条：{string.Join(" / ", AppDatabase.Instance.SearchGameNames("a", 3).Select(r => r.CleanEn))}\n" +
            $"待补中文名：{AppDatabase.Instance.LoadGameNamesMissingCn().Count} 条（后台补齐只碰这些）\n");

        // ---- 本地提示 ----
        var hits = AppDatabase.Instance.SearchGameNames("assas", 8);
        File.AppendAllText(log,
            $"搜索 “assas” 命中 {hits.Count} 条：{string.Join(" / ", hits.Select(h => h.CleanEn))}\n");

        SearchBox.Text = "assas";
        SearchBox.Focus();
        await Task.Delay(1000);
        File.AppendAllText(log, $"提示弹窗：{(SuggestPopup.IsOpen ? "已打开" : "没打开")}，条目 {_suggests.Count}\n");

        // 弹窗是独立窗口，RenderTargetBitmap 拍不到，这里直接截屏
        CaptureWindowScreen(Path.Combine(dir, "01-suggest.png"));
        CloseSuggest();

        // 中文名也能搜（同步过才有）
        var cnHits = AppDatabase.Instance.SearchGameNames("赛博", 8);
        File.AppendAllText(log,
            $"搜索 “赛博” 命中 {cnHits.Count} 条：{string.Join(" / ", cnHits.Select(h => $"{h.CleanEn}（{h.NameCn}）"))}\n");

        // ---- 中文名提示（需要先有中文名；没有就现场换几条写进去） ----
        foreach (var probe in new[] { "Assassin's Creed Mirage", "Cyberpunk 2077", "Elden Ring" })
        {
            var cn = await SteamWeb.FetchChineseNameAsync(probe);
            File.AppendAllText(log, $"Steam：{probe} → {(cn is null ? "请求失败" : cn.Length == 0 ? "（没有中文名）" : cn)}\n");
        }

        // ---- 进度面板外观（后台同步的「看进度」窗口） ----
        _nameSyncStage = NameSyncStage.Filling;
        _nameSyncPhaseText = "正在从 Steam 逐个换取中文名…";
        _nameSyncCurrent = "";
        _nameSyncDone = 0;
        _nameSyncRunTotal = count;
        ShowNameSyncPanel();
        UpdateNameSyncProgress(Math.Max(1, count / 3), count, "Assassin's Creed Mirage");
        await Task.Delay(600);
        SaveShot(Path.Combine(dir, "02-sync-dialog.png"));

        _nameSyncStage = NameSyncStage.Done;
        _nameSyncPhaseText = $"同步完成 · {count} 款游戏，其中 {AppDatabase.Instance.GameNameCnCount()} 款有中文名";
        UpdateNameSyncProgress(count, count, "Cyberpunk 2077");
        await Task.Delay(400);
        SaveShot(Path.Combine(dir, "03-sync-done.png"));
        HideNameSyncPanel();

        // ---- 方向键选中 + 回车回填（键盘路径直接调内部方法验证，真实按键靠人工） ----
        // 先把输入清空再打，否则文本没变不会触发 TextChanged，提示不会重新查
        SearchBox.Text = "";
        await Task.Delay(60);
        SearchBox.Text = "assas";
        SearchBox.Focus();
        await Task.Delay(1000);

        MoveSuggest(+1);
        MoveSuggest(+1);
        var picked = SuggestList.SelectedItem as GameNameRow;
        File.AppendAllText(log, $"方向键选中：index={SuggestList.SelectedIndex}，选中项=「{picked?.CleanEn}」\n");

        // 再从「按键」这条路走一遍：模拟 ↓↓，看是不是真的挪到了第 2 条
        SuggestList.SelectedIndex = -1;
        SimulateSearchKey(Key.Down);
        SimulateSearchKey(Key.Down);
        File.AppendAllText(log,
            $"模拟按键 ↓↓ 之后：index={SuggestList.SelectedIndex}，" +
            $"选中项=「{(SuggestList.SelectedItem as GameNameRow)?.CleanEn}」\n");

        // 等一帧把高亮画出来，否则截到的还是上一帧（WPF 渲染在下一个合成周期）
        await Task.Delay(300);
        CaptureWindowScreen(Path.Combine(dir, "04-suggest-selected.png"));

        if (SuggestList.SelectedItem is GameNameRow row)
        {
            // 模拟回车：应该回填英文名并直接搜
            SimulateSearchKey(Key.Enter);
            await Task.Delay(1200);
            File.AppendAllText(log, $"模拟回车之后：搜索框=「{SearchBox.Text}」\n");
            SaveShot(Path.Combine(dir, "05-suggest-applied.png"));
        }
    }

    /// <summary>自检用：把一个按键按「预览」路径发给搜索框，等价于真实按下。</summary>
    private void SimulateSearchKey(Key key)
    {
        var source = PresentationSource.FromVisual(SearchBox);
        if (source is null) return;

        SearchBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
        });
    }

    /// <summary>
    /// 自检：跑一次真正的后台同步（清空 → 重抓全量 → 2 路慢慢换中文名），
    /// 全程不 await 它，用来验证「同步不挡界面」：中途还能截图、还能开关面板。
    /// </summary>
    public async Task RunNameSyncCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "sync.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        for (var i = 0; i < 900 && AppDatabase.Instance.GameNameCount() == 0; i++) await Task.Delay(100);

        var before = AppDatabase.Instance.GameNameCount();
        var started = Environment.TickCount64;

        // 后台开跑：不等它，接下来照常操作界面
        StartNameSync(rebuild: true);

        await Task.Delay(2500);
        File.AppendAllText(log,
            $"2.5 秒后：stage={_nameSyncStage}，面板={(NameSyncLayer.Visibility == Visibility.Visible ? "开" : "关")}，" +
            $"胶囊=「{NamePillText.Text} {NamePillSub.Text}」\n");

        // 关着面板也能看到胶囊上的活进度
        SaveShot(Path.Combine(dir, "01-pill-running.png"));

        // 点开面板看详情
        NamePill_Click(NamePill, new RoutedEventArgs());
        await Task.Delay(700);
        File.AppendAllText(log, $"点开面板：{NameSyncLayer.Visibility}，阶段文案=「{NameSyncPhase.Text}」\n");
        SaveShot(Path.Combine(dir, "02-panel-running.png"));

        // 收起面板，验证同步不受影响
        HideNameSyncPanel();
        await Task.Delay(1500);
        File.AppendAllText(log,
            $"收起面板 1.5 秒后仍在跑：{(NameSyncRunning ? "是" : "否")}，" +
            $"进度 {_nameSyncDone} / {_nameSyncRunTotal}\n");

        // 等它自己跑完（最多 8 分钟）
        for (var i = 0; i < 4800 && _nameSyncRunning; i++) await Task.Delay(100);

        var elapsed = Environment.TickCount64 - started;
        var after = AppDatabase.Instance.GameNameCount();
        var withCn = AppDatabase.Instance.GameNameCnCount();

        File.AppendAllText(log,
            $"同步前 {before} 条 → 同步后 {after} 条，其中 {withCn} 条有中文名\n" +
            $"stage={_nameSyncStage}，胶囊=「{NamePillText.Text} {NamePillSub.Text}」\n" +
            $"耗时 {elapsed / 1000.0:F1} 秒（2 路并发 + 每条歇 90ms）\n" +
            $"待补中文名还剩：{AppDatabase.Instance.LoadGameNamesMissingCn().Count} 条\n");

        ShowNameSyncPanel();
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "03-panel-done.png"));
        HideNameSyncPanel();

        foreach (var kw in new[] { "assassin", "cyberpunk", "elden", "gta" })
        {
            var hits = AppDatabase.Instance.SearchGameNames(kw, 4);
            File.AppendAllText(log,
                $"“{kw}”：{string.Join(" / ", hits.Select(h => $"{h.CleanEn}（{h.NameCn}）"))}\n");
        }

        SearchBox.Text = "assas";
        SearchBox.Focus();
        await Task.Delay(1000);
        CaptureWindowScreen(Path.Combine(dir, "01-suggest-cn.png"));
        CloseSuggest();
    }

    /// <summary>
    /// 自检：只走「补齐缺的中文名」这一条路（启动时自动同步用的就是它），
    /// 确认不会弹面板、界面照常可用，胶囊上有活进度。
    /// </summary>
    public async Task RunNameFillCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "fill.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        for (var i = 0; i < 900 && AppDatabase.Instance.GameNameCount() == 0; i++) await Task.Delay(100);

        var all = AppDatabase.Instance.GameNameCount();
        var cnBefore = AppDatabase.Instance.GameNameCnCount();
        var missing = AppDatabase.Instance.LoadGameNamesMissingCn().Count;

        File.AppendAllText(log,
            $"开跑前：{all} 条名单，其中 {cnBefore} 条有中文名，待补 {missing} 条\n" +
            $"    空闲时胶囊={(NamePill.Visibility == Visibility.Visible ? "显示（不对）" : "隐藏（对）")}，" +
            $"设置页中文名行=「{NameSyncSettingDesc.Text}」\n");
        SaveShot(Path.Combine(dir, "00-idle.png"));

        var started = Environment.TickCount64;
        StartNameSync(rebuild: false);

        await Task.Delay(2000);
        File.AppendAllText(log,
            $"2 秒后：stage={_nameSyncStage}，胶囊={(NamePill.Visibility == Visibility.Visible ? "显示" : "隐藏（不对）")}，" +
            $"文字=「{NamePillText.Text} {NamePillSub.Text}」，设置页中文名行=「{NameSyncSettingDesc.Text}」，" +
            $"面板={(NameSyncLayer.Visibility == Visibility.Visible ? "开（不对）" : "关（对）")}\n");

        // 中途再采一次，确认计数是跟着走的，不是写死在初始值上
        await Task.Delay(4000);
        File.AppendAllText(log,
            $"6 秒后：胶囊=「{NamePillText.Text} {NamePillSub.Text}」，设置页中文名行=「{NameSyncSettingDesc.Text}」\n");
        SaveShot(Path.Combine(dir, "01-fill-running.png"));

        for (var i = 0; i < 4800 && _nameSyncRunning; i++) await Task.Delay(100);

        var elapsed = Environment.TickCount64 - started;
        var cnAfter = AppDatabase.Instance.GameNameCnCount();

        await Task.Delay(400);   // 等收尾那一帧刷新，胶囊会在这时候收起来
        File.AppendAllText(log,
            $"结束：{AppDatabase.Instance.GameNameCount()} 条名单，中文名 {cnBefore} → {cnAfter}，stage={_nameSyncStage}\n" +
            $"耗时 {elapsed / 1000.0:F1} 秒，本轮进度 {_nameSyncDone} / {_nameSyncRunTotal}\n" +
            $"待补还剩 {AppDatabase.Instance.LoadGameNamesMissingCn().Count} 条，" +
            $"胶囊={(NamePill.Visibility == Visibility.Visible ? "仍显示（不对）" : "已收起（对）")}，" +
            $"设置页中文名行=「{NameSyncSettingDesc.Text}」\n");

        SaveShot(Path.Combine(dir, "02-fill-done.png"));
    }

    /// <summary>
    /// 自检：用真实鼠标点一下智能提示的第二行，验证「点得到、点完直接去搜官网」。
    /// 之前的毛病是 Popup 用了 StaysOpen=False，鼠标一按它先把自己关掉，点击就丢了。
    /// </summary>
    public async Task RunSuggestClickCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "click.txt");
        File.WriteAllText(log, "");
        _selfCheckLog = log;

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        for (var i = 0; i < 600 && AppDatabase.Instance.GameNameCount() == 0; i++) await Task.Delay(100);

        NavList.SelectedIndex = 0;
        await Task.Delay(400);
        Activate();
        SearchBox.Focus();
        SearchBox.Text = "assas";
        await Task.Delay(1300);

        File.AppendAllText(log,
            $"提示已开：{SuggestPopup.IsOpen}，条目 {_suggests.Count}：" +
            $"{string.Join(" | ", _suggests.Take(3).Select(s => s.CleanEn))}\n");

        if (!SuggestPopup.IsOpen || _suggests.Count < 2)
        {
            File.AppendAllText(log, "没有可点的提示项，跳过\n");
            return;
        }

        var target = _suggests[1];
        SuggestList.SelectedIndex = 0;   // 高亮先放第一行，点完看它有没有真的换过去
        SuggestList.UpdateLayout();
        await Task.Delay(150);

        if (SuggestList.ItemContainerGenerator.ContainerFromIndex(1) is not FrameworkElement container)
        {
            File.AppendAllText(log, "拿不到第 2 行的容器，跳过\n");
            return;
        }

        // Popup 是独立窗口，Visual.PointToScreen 在这套 DPI 下不可靠 ——
        // 直接拿 Popup 自己的窗口矩形，再加上行在 Popup 里的相对位置（按 DPI 换算成物理像素）
        var popupRoot = SuggestPopup.Child as FrameworkElement;
        var popupHwnd = popupRoot is null
            ? IntPtr.Zero
            : (PresentationSource.FromVisual(popupRoot) as HwndSource)?.Handle ?? IntPtr.Zero;

        var x = 0;
        var y = 0;
        if (popupHwnd != IntPtr.Zero && GetWindowRect(popupHwnd, out var popupRect))
        {
            var scale = Math.Max(1.0, GetDpiForWindow(popupHwnd) / 96.0);
            var rel = container.TransformToAncestor(popupRoot!)
                .Transform(new Point(container.ActualWidth / 2, container.ActualHeight / 2));

            x = popupRect.Left + (int)Math.Round(rel.X * scale);
            y = popupRect.Top + (int)Math.Round(rel.Y * scale);

            File.AppendAllText(log,
                $"Popup 窗口 ({popupRect.Left},{popupRect.Top})-({popupRect.Right},{popupRect.Bottom})，" +
                $"缩放 {scale:F2}，行内相对 ({rel.X:F0},{rel.Y:F0})\n");
        }
        else
        {
            var fallback = container.PointToScreen(
                new Point(container.ActualWidth / 2, container.ActualHeight / 2));
            x = (int)fallback.X;
            y = (int)fallback.Y;
            File.AppendAllText(log, $"拿不到 Popup 句柄，退回 PointToScreen ({x},{y})\n");
        }

        File.AppendAllText(log, $"要点第 2 行「{target.CleanEn}」，屏幕坐标 ({x}, {y})\n");

        // 自检窗口必须在最前面，否则这一下点到的是别的窗口
        var mainHwnd = new WindowInteropHelper(this).Handle;
        SetForegroundWindow(mainHwnd);
        await Task.Delay(400);
        File.AppendAllText(log,
            $"点击前前台是本窗口：{(GetForegroundWindow() == mainHwnd ? "是" : "否")}\n");

        SetCursorPos(x, y);
        await Task.Delay(120);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(40);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);

        await Task.Delay(1800);

        var ok = SearchBox.Text == target.CleanEn && _searchPager.TotalCount > 0;
        File.AppendAllText(log,
            $"点完之后：搜索框=「{SearchBox.Text}」，提示开着={SuggestPopup.IsOpen}\n" +
            $"    结果 {_searchPager.TotalCount} 条：{string.Join(" / ", _items.Take(2).Select(i => i.Title))}\n" +
            $"    状态栏：{StatusText.Text}\n" +
            $"    与预期一致：{(ok ? "是" : "否")}\n");

        CloseSuggest();
        SaveShot(Path.Combine(dir, "01-after-click.png"));
    }

    /// <summary>
    /// 自检：搜索关键词的 URL 编码 + 中文名翻译。
    /// 官网是英文站，中文词必须先翻成英文名；英文词里的空格、撇号交给 Uri.EscapeDataString 编码。
    /// 这里把四类关键词真打一遍，记下本地反查结果、实际发给官网的词、结果条数和状态栏文案。
    /// </summary>
    public async Task RunSearchCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "search.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        for (var i = 0; i < 600 && AppDatabase.Instance.GameNameCount() == 0; i++) await Task.Delay(100);

        File.AppendAllText(log,
            $"游戏名库 {AppDatabase.Instance.GameNameCount()} 条，其中有中文名 {AppDatabase.Instance.GameNameCnCount()} 条\n");

        foreach (var (label, keyword) in new[]
                 {
                     ("中文名 · 完整", "赛博朋克 2077"),
                     ("中文名 · 部分", "艾尔登法环"),
                     ("英文 · 空格与撇号", "Assassin's Creed"),
                     ("英文 · 单词", "elden"),
                     ("中文名 · 库里没有", "风灵月影"),
                 })
        {
            var translated = AppDatabase.Instance.FindEnglishByChinese(keyword);
            SearchBox.Text = keyword;
            await SubmitSearchAsync();
            await Task.Delay(800);

            File.AppendAllText(log,
                $"[{label}] 输入「{keyword}」\n" +
                $"    本地反查英文名：{translated ?? "（没有）"}\n" +
                $"    实际发给官网：{_keyword}（编码后 {Uri.EscapeDataString(_keyword)}）\n" +
                $"    结果 {_searchPager.TotalCount} 条：{string.Join(" / ", _items.Take(3).Select(i => i.Title))}\n" +
                $"    状态栏：{StatusText.Text}\n");
        }

        // 最后停在中文搜索的结果页上，顺带把顶栏（胶囊 + 刷新按钮）一起拍进去
        SearchBox.Text = "赛博朋克 2077";
        await SubmitSearchAsync();
        await Task.Delay(900);
        CloseSuggest();
        await Task.Delay(200);
        CaptureWindowScreen(Path.Combine(dir, "01-search-cn.png"));

        File.AppendAllText(log,
            $"截图停在：搜索「{_keyword}」，结果 {_searchPager.TotalCount} 条，第一页 {_items.Count} 张卡片\n");
    }

    /// <summary>自检：关于 / 声明弹窗的新文案排版（正文变长后靠滚动，落款要能看到）。</summary>
    public async Task RunAboutCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "about.txt");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);

        ShowAbout();
        await Task.Delay(900);
        SaveShot(Path.Combine(dir, "01-about.png"));

        var scroll = FindScrollViewer(AboutCard);
        if (scroll is not null)
        {
            scroll.ScrollToEnd();
            await Task.Delay(400);
            SaveShot(Path.Combine(dir, "02-about-end.png"));

            File.WriteAllText(log,
                $"关于卡片：{AboutCard.ActualWidth:F0}x{AboutCard.ActualHeight:F0}，" +
                $"正文可视高 {scroll.ViewportHeight:F0} / 内容高 {scroll.ExtentHeight:F0}，" +
                $"可滚 {scroll.ScrollableHeight:F0}\n");
        }

        CloseAbout();
    }
#endif

    /// <summary>
    /// 自检：核对「已经收录」（SQLite 缓存条数）和「已加载」（内存里当前列表条数）两个数字，
    /// 顺便看看正常一轮加载要多久。
    /// </summary>
    public async Task RunCountCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "count.txt");
        File.WriteAllText(log, "");

        var started = Environment.TickCount64;
        for (var i = 0; i < 600 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        var elapsed = Environment.TickCount64 - started;

        File.AppendAllText(log,
            $"首次加载耗时：{elapsed} ms\n" +
            $"副标题：{SectionSubtitle.Text}\n" +
            $"状态栏：{StatusText.Text}\n" +
            $"内存条数：{_library.Items.Count} / SQLite：{AppDatabase.Instance.CacheCount()}\n" +
            $"收藏：{_favorites.Count} / 库可用：{AppDatabase.Instance.IsAvailable}\n");

        SaveShot(Path.Combine(dir, "count.png"));
    }

    /// <summary>
    /// 自检：圆角按钮的边缘。同一个窗口分别按 1.0 / 1.25 / 1.5 / 2.0 倍渲染出来，
    /// 再抓一张当前屏幕的真实像素，用来对比「100% 缩放」和「150% 缩放」下圆角有没有发黑。
    /// </summary>
    public async Task RunCornerCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "corner.txt");
        File.WriteAllText(log, $"实际 DPI 缩放 = {VisualTreeHelper.GetDpi(this).DpiScaleX:0.###}\n");

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(1200);

        NavList.SelectedIndex = 3;               // 设置
        await Task.Delay(700);
        Activate();
        await Task.Delay(400);

        CaptureWindowScreen(Path.Combine(dir, "blue-screen.png"));
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            SaveShotAtScale(Path.Combine(dir, $"blue-{scale * 100:0}.png"), scale);

        // 红按钮在确认框里，底色比卡片更暗，是最容易看出黑边的那颗
        ClearCacheButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Task.Delay(700);

        CaptureWindowScreen(Path.Combine(dir, "red-screen.png"));
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            SaveShotAtScale(Path.Combine(dir, $"red-{scale * 100:0}.png"), scale);

        File.AppendAllText(log, "已输出 blue-*/red-* 各 1x 1.25x 1.5x 2x 以及屏幕实拍\n");
    }

    /// <summary>
    /// 自检：几种圆角画法并排渲染（纯填充 / 不同强度的内侧亮边 / 不同圆角半径），
    /// 在 1x 和 1.5x 下各出一张，用来看哪种在 100% 缩放下四角不会显得发黑。
    /// </summary>
    public Task RunRimCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);

        var variants = new (string name, double radius, Color? rim, double rimAlpha, double halo)[]
        {
            ("A-纯填充-r10", 10, null, 0, 0),
            ("B-亮边6%-r10", 10, Colors.White, 0.06, 0),
            ("C-亮边12%-r10", 10, Colors.White, 0.12, 0),
            ("D-纯填充-r12", 12, null, 0, 0),
            ("E-亮边6%-r12", 12, Colors.White, 0.06, 0),
            ("F-同色描边-r10", 10, Color.FromRgb(0x7A, 0xB2, 0xFF), 1.0, 0),
            ("G-同色衬底22%-r10", 10, null, 0, 0.22),
            ("H-同色衬底35%-r10", 10, null, 0, 0.35),
            ("I-同色衬底45%-r12", 12, null, 0, 0.45)
        };

        var host = new Grid
        {
            Width = 300,
            Height = 34 * variants.Length + 12 * (variants.Length + 1),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x28, 0x36))
        };

        for (var i = 0; i < variants.Length; i++)
        {
            var (_, radius, rim, alpha, halo) = variants[i];

            var slot = new Grid
            {
                Width = 116,
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 12 + i * (34 + 12), 0, 0)
            };

            // 衬底：比按钮大 1 DIP 的同色半透明圆角块。主填充的抗锯齿就只和这层同色混，
            // 不再直接和深色卡片底混，角上那圈"发黑"的过渡就被换成一圈很淡的同色光。
            if (halo > 0)
            {
                slot.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(radius + 1),
                    Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(halo * 255), 0x3B, 0x82, 0xF6))
                });
            }

            var button = new Border
            {
                Margin = new Thickness(halo > 0 ? 1 : 0),
                CornerRadius = new CornerRadius(radius),
                Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6))
            };

            if (rim is { } color)
            {
                button.BorderThickness = new Thickness(1);
                button.BorderBrush =
                    new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B));
            }

            slot.Children.Add(button);
            host.Children.Add(slot);
        }

        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();

        foreach (var scale in new[] { 1.0, 1.5 })
        {
            var bitmap = new RenderTargetBitmap(
                (int)Math.Round(host.Width * scale), (int)Math.Round(host.Height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(host);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(dir, $"rims-{scale * 100:0}.png"));
            encoder.Save(stream);
        }

        File.WriteAllText(Path.Combine(dir, "rims.txt"),
            string.Join("\n", variants.Select((v, i) => $"{i}: {v.name} radius={v.radius} rim={v.rim} alpha={v.rimAlpha}")));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 自检：首页右上角「最近更新 / 热门游戏」页签（选中态）的圆角描边在 1x 下发黑的对照实验。
    /// 背景 #0E1825、填充 #244A72、描边 #3974AE、圆角 8、高 32——与 LibraryTabButton 完全一致。
    /// </summary>
    public Task RunTabCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);

        var fill = Color.FromRgb(0x24, 0x4A, 0x72);
        var stroke = Color.FromRgb(0x39, 0x74, 0xAE);
        var pageBg = Color.FromRgb(0x0E, 0x18, 0x25);

        Border Pill(double radius, double thickness, Brush? brush)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(radius),
                Background = new SolidColorBrush(fill)
            };
            if (brush is not null)
            {
                b.BorderThickness = new Thickness(thickness);
                b.BorderBrush = brush;
            }
            return b;
        }

        // 每个槽位 140x44，槽间距 14，页签 116x32 居中放
        (string name, Func<UIElement> build)[] variants =
        [
            ("A-现状-描边1", () => Pill(8, 1, new SolidColorBrush(stroke))),
            ("B-衬底55%同色", () =>
            {
                var g = new Grid();
                g.Children.Add(new Border
                {
                    Margin = new Thickness(-1),
                    CornerRadius = new CornerRadius(9),
                    Background = new SolidColorBrush(Color.FromArgb(0x8C, stroke.R, stroke.G, stroke.B))
                });
                g.Children.Add(Pill(8, 1, new SolidColorBrush(stroke)));
                return g;
            }),
            ("C-衬底纯填充", () =>
            {
                var g = new Grid();
                g.Children.Add(new Border
                {
                    Margin = new Thickness(-1),
                    CornerRadius = new CornerRadius(9),
                    Background = new SolidColorBrush(fill)
                });
                g.Children.Add(Pill(8, 1, new SolidColorBrush(stroke)));
                return g;
            }),
            ("D-内嵌描边", () =>
            {
                var g = new Grid();
                g.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(fill) });
                g.Children.Add(new Border
                {
                    Margin = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(stroke)
                });
                return g;
            }),
            ("E-描边1.5", () => Pill(8, 1.5, new SolidColorBrush(stroke))),
        ];

        var host = new Grid
        {
            Width = 180,
            Height = variants.Length * 44 + (variants.Length + 1) * 14,
            Background = new SolidColorBrush(pageBg)
        };

        for (var i = 0; i < variants.Length; i++)
        {
            var slot = new Grid
            {
                Width = 140,
                Height = 44,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 14 + i * (44 + 14), 0, 0)
            };
            slot.Children.Add(variants[i].build());
            host.Children.Add(slot);
        }

        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();

        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var bitmap = new RenderTargetBitmap(
                (int)Math.Round(host.Width * scale), (int)Math.Round(host.Height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(host);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(dir, $"tabs-{scale * 100:0}.png"));
            encoder.Save(stream);
        }

        File.WriteAllText(Path.Combine(dir, "tabs.txt"),
            string.Join("\n", variants.Select((v, i) => $"{i}: {v.name}")));
        return Task.CompletedTask;
    }

    /// <summary>按给定倍数把整个窗口渲染成一张图（相当于那台机器按这个 DPI 缩放渲染）。</summary>
    private void SaveShotAtScale(string path, double scale)
    {
        try
        {
            var width = (int)Math.Round(ActualWidth * scale);
            var height = (int)Math.Round(ActualHeight * scale);
            if (width <= 0 || height <= 0) return;

            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(this);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch
        {
            // 截图失败不影响自检
        }
    }

    /// <summary>抓当前屏幕上这块窗口的真实像素（硬件渲染的结果，不是 RenderTargetBitmap）。</summary>
    private void CaptureWindowScreen(string path)
    {
        try
        {
            var origin = PointToScreen(new Point(0, 0));
            var width = (int)Math.Round(ActualWidth);
            var height = (int)Math.Round(ActualHeight);
            if (width <= 0 || height <= 0) return;

            using var bitmap = new System.Drawing.Bitmap(width, height);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen((int)Math.Round(origin.X), (int)Math.Round(origin.Y), 0, 0,
                new System.Drawing.Size(width, height));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch
        {
            // 抓不到屏幕也不影响自检
        }
    }

    /// <summary>抓整块屏幕（工具提示、Popup 都是独立 HWND，窗口截图里拍不到）。</summary>
    private void CaptureFullScreen(string path)
    {
        try
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen is null) return;

            var bounds = screen.Bounds;
            using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch
        {
            // 抓不到屏幕也不影响自检
        }
    }

    /// <summary>按类型找视觉树里的所有子元素。</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;

            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>
    /// 自检：把窗口从 1080 一路摆到 3840 宽，记录每档的版心宽度、卡片尺寸、行数，
    /// 用来核对「固定版心 + 一行 5 张」在 1080 / 2K / 4K 上的表现。
    /// </summary>
    public async Task RunLayoutCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "layout.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(2000);

        // 这台机器的屏幕可能不大：放开尺寸限制，只验证布局算法
        MinWidth = 320;
        MinHeight = 320;
        MaxWidth = 6000;
        MaxHeight = 3000;

        foreach (var width in new[] { 1080, 1280, 1440, 1920, 2560, 3840 })
        {
            Width = width;
            Height = 860;
            await Task.Delay(650);

            var perRow = _rows.Count > 0 ? _rows[0].Items.Count : 0;
            var leftGap = Math.Max(0, (MainArea.ActualWidth - CardArea.ActualWidth) / 2);
            File.AppendAllText(log,
                $"窗口 {width,5} / 实际 {ActualWidth,5:F0} / 右栏 {MainArea.ActualWidth,5:F0} → " +
                $"版心 {CardArea.ActualWidth,5:F0}（两侧各留 {leftGap,4:F0}） " +
                $"卡片 {_cardW}x{_cardH} 首行 {perRow} 条 / 共 {_rows.Count} 行\n");

            if (width is 1080 or 1440 or 1920 or 3840) SaveShot(Path.Combine(dir, $"layout-{width}.png"));
        }

        // 滚到中间再改宽度，验证行高变化后仍停在原来那一行
        Width = 1440;
        await Task.Delay(500);
        _listScroll ??= FindScrollViewer(CardList);
        _listScroll?.ScrollToVerticalOffset(400);
        await Task.Delay(400);
        var before = _listScroll?.VerticalOffset ?? 0;

        Width = 2560;
        await Task.Delay(900);
        var after = _listScroll?.VerticalOffset ?? 0;

        File.AppendAllText(log,
            $"改宽度前后滚动位置：{before:F0} → {after:F0}（行高 {CardRowGap + _cardH:F0}，" +
            $"等效行号 {after / (CardRowGap + _cardH):F2}）\n");
    }

    /// <summary>把任意窗口渲染成 PNG（用于托盘小菜单这种独立窗口）。</summary>
    private static void SaveShotOf(Window window, string path)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            var width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);
            if (width <= 0 || height <= 0) return;

            var bitmap = new RenderTargetBitmap(width, height,
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch
        {
            // 截图失败不影响自检
        }
    }

    /// <summary>整屏截图：用来核对右下角托盘区里有没有那只图标；顺带截一条任务栏，看还有没有程序按钮。</summary>
    private static void CaptureScreen(string path)
    {
        try
        {
            var bounds = WinForms.Screen.PrimaryScreen?.Bounds;
            if (bounds is null) return;

            using var bitmap = new Drawing.Bitmap(bounds.Value.Width, bounds.Value.Height);
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Value.Location, Drawing.Point.Empty, bounds.Value.Size);
            bitmap.Save(path, Drawing.Imaging.ImageFormat.Png);

            // 任务栏那条单独裁一份：整屏缩略图里那几个像素看不清
            var stripHeight = Math.Min(80, bounds.Value.Height / 4);
            using var strip = new Drawing.Bitmap(bounds.Value.Width, stripHeight);
            using (var graphics = Drawing.Graphics.FromImage(strip))
                graphics.DrawImage(bitmap,
                    new Drawing.Rectangle(0, 0, strip.Width, strip.Height),
                    new Drawing.Rectangle(0, bitmap.Height - stripHeight, bitmap.Width, stripHeight),
                    Drawing.GraphicsUnit.Pixel);

            strip.Save(Path.Combine(Path.GetDirectoryName(path) ?? "", "tray-taskbar.png"),
                Drawing.Imaging.ImageFormat.Png);
        }
        catch
        {
            // 截图失败不影响自检
        }
    }

    /// <summary>跑一遍主要界面并把窗口渲染成 PNG，用于核对 DPI 与布局。</summary>
    public async Task CaptureTourAsync(string dir)
    {
        Directory.CreateDirectory(dir);

        // 首屏是异步向官网请求的，等它落地再截图（最多等 30 秒）
        for (var i = 0; i < 300 && _firstLoading; i++)
            await Task.Delay(100);

        // 封面同样来自官网，多给一点时间让首屏的图下来
        await Task.Delay(2500);
        SaveShot(Path.Combine(dir, "01-main.png"));

        var item = _library.Items
            .OrderByDescending(i => i.RealOptionCount)
            .FirstOrDefault(i => i.Groups.Count > 0) ?? _library.Items.FirstOrDefault();
        if (item is null) return;

        // 收藏夹截图：先临时收藏这一款，拍完再取消，不污染真实收藏（顺带验证字符串主键写库）
        OnFavoriteChanged(item, true);
        NavList.SelectedIndex = 1;
        await Task.Delay(800);
        SaveShot(Path.Combine(dir, "02-favorites.png"));

        NavList.SelectedIndex = 0;
        await Task.Delay(400);
        OnFavoriteChanged(item, false);
        await Task.Delay(200);

        OpenDetail(item);
        await Task.Delay(1000);

        if (DetailHost.Content is TrainerDetailView first)
        {
            // 默认落在「软件截图」，这里显式切到选项看一下
            first.ShowOptionsTab();
            await Task.Delay(500);
        }

        SaveShot(Path.Combine(dir, "03-detail-options.png"));

        if (DetailHost.Content is TrainerDetailView view)
        {
            view.ShowShotsTab();
            await Task.Delay(1400);
            SaveShot(Path.Combine(dir, "04-detail-shots.png"));

            view.ShowDownloadTab();
            await Task.Delay(500);
            SaveShot(Path.Combine(dir, "05-detail-download.png"));

            view.DownloadFirst();
            await Task.Delay(5200);
            SaveShot(Path.Combine(dir, "06-detail-progress.png"));
        }

        CloseDetail();
        await Task.Delay(400);

        NavList.SelectedIndex = 2;
        await Task.Delay(900);
        SaveShot(Path.Combine(dir, "07-queue.png"));

        await Task.Delay(6000);
        SaveShot(Path.Combine(dir, "08-queue-done.png"));

        OpenDetail(item);
        await Task.Delay(900);
        if (DetailHost.Content is TrainerDetailView done)
        {
            done.ShowDownloadTab();
            await Task.Delay(500);
            SaveShot(Path.Combine(dir, "09-detail-done.png"));
        }

        // 点遮罩关闭：抛一次落在遮罩层上的左键按下，验证真的会关（拍一张关掉之后的图）
        var maskClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent
        };
        DetailBackdrop.RaiseEvent(maskClick);
        await Task.Delay(450);
        SaveShot(Path.Combine(dir, "09b-mask-close.png"));

        CloseDetail();
        await Task.Delay(300);

        NavList.SelectedIndex = 3;
        await Task.Delay(600);
        SaveShot(Path.Combine(dir, "10-settings.png"));

        DownloadManager.Instance.ClearFinished();
        NavList.SelectedIndex = 2;
        await Task.Delay(600);
        SaveShot(Path.Combine(dir, "11-queue-empty.png"));

        // 搜索：聚焦 + 输入（验证聚焦描边、图标与文字是否同轴）；此时列表不动
        NavList.SelectedIndex = 0;
        await Task.Delay(500);
        Activate();
        SearchBox.Focus();
        SearchBox.Text = "elden";
        await Task.Delay(500);
        CloseSuggest();
        SaveShot(Path.Combine(dir, "13-search-focus.png"));

        // 回车之后才真的向官网要结果
        await SubmitSearchAsync();
        await Task.Delay(2600);
        SaveShot(Path.Combine(dir, "14-search-result.png"));

        ExitSearch(clearText: true);
        await Task.Delay(500);

        // 模拟 200% 缩放的 1080p 屏：窗口只有 960x520 DIP，验证弹框能收进去
        MinWidth = 0;
        MinHeight = 0;
        Width = 960;
        Height = 520;
        await Task.Delay(500);
        NavList.SelectedIndex = 0;
        OpenDetail(item);
        await Task.Delay(900);
        SaveShot(Path.Combine(dir, "12-small-window-detail.png"));

    }

    private void SaveShot(string path)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var width = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);

            var bitmap = new RenderTargetBitmap(width, height,
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(this);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch
        {
            // 截图失败不影响主流程
        }
    }
#endif
}
