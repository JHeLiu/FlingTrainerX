using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using FlingTrainerX.Controls;
using System.Windows.Documents;
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
using FlingTrainerX.ViewModels;
using FlingTrainerX.Views;

namespace FlingTrainerX;

public partial class MainWindow : Window
{
    /// <summary>
    /// 首页的 ViewModel：标题 / 页签 / 空状态 / 分页栏怎么显示由它算，XAML 直接绑定。
    /// 窗口只负责取数据（官网 / SQLite）和窗口级交互（拖拽、动画、托盘），
    /// 每次状态变化把一份 <see cref="LibraryPageState"/> 喂给它即可。
    /// </summary>
    public LibraryViewModel ViewModel { get; } = new(new ObservableCollection<CardRow>());

    /// <summary>
    /// 更新进度弹框的 ViewModel：三条进度线、标题、可选项、两颗按钮的显隐和文案都在里面。
    /// 窗口只管「跑同步」，弹框长什么样由它自己说，XAML 单向绑过去。
    /// </summary>
    public SyncViewModel Sync { get; } = new();

    /// <summary>设置页的 ViewModel：选到哪一组、版本与更新状态那两行字。</summary>
    public SettingsViewModel Settings { get; } = new();

    /// <summary>
    /// 固定版心：窗口默认 1280×780，右栏可用约 1018 → 版心 1020，一行 5 张、每张 193；
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
    private const double CardGap = 8;

    /// <summary>列表右侧给滚动条预留的宽度，和 CardListBox 的 Padding 保持一致。</summary>
    private const double CardListGutter = 6;

    /// <summary>行与行之间的空隙，和 DataTemplate 里那个 ItemsControl 的 Margin 保持一致。</summary>
    private const double CardRowGap = 8;

    /// <summary>
    /// 卡片下半部的标题区高度：两行 14 号标题（行高 20 = 40）+ 一行「更新时间 / 选项数」标签（约 22）
    /// + 上下内边距 16 ≈ 84，留 2 像素余量。封面高度 = 卡宽 × 9/16，正好和官网头图同比例，不裁不变形。
    /// </summary>
    private const double CardInfoHeight = 84;

    /// <summary>窗口四角圆角半径（DWM 原生圆角 + 内容裁剪用同一套数值）。</summary>
    private const double ShellCornerRadius = 12;

    /// <summary>窗口能被拖到的最小尺寸；再小布局就挤了（版心按 1080 宽屏设计）。</summary>
    private const double MinWindowWidth = 1080;
    private const double MinWindowHeight = 680;

    private TrainerLibrary _library = new();

    /// <summary>启动阶段在后台并行读的数据；OnLoaded 时取结果，第一帧就带着卡片。</summary>
    private readonly Task<StartupData> _startupData;

    /// <summary>当前视图实际展示的数据（翻页会往后追加）。</summary>
    private readonly List<TrainerItem> _items = new();

    /// <summary>虚拟滚动的最小单位：一行 5 张卡片。</summary>
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

    /// <summary>正在更新官网数据 / 游戏名：同一时间只允许一个更新任务（三颗按钮共用这一道闸）。</summary>
    private bool _syncRunning;

    /// <summary>更新任务的取消源，取消按钮要用。</summary>
    private CancellationTokenSource? _syncCts;

    /// <summary>名录已经写完了、正在换中文名：取消时才知道该把「已取消」写在哪一条上。</summary>
    private bool _nameStageCn;

    /// <summary>
    /// 「快速更新游戏名」开跑前的等待：弹框先把两条进度和「是否同步中文名」摆出来，
    /// 等用户点了框里的「开始更新」才把结果回给调用方（Ok, 是否同步中文名）。
    /// </summary>
    private TaskCompletionSource<(bool Ok, bool WithSteam)>? _syncStart;

    /// <summary>左侧分组菜单切换显示时用的闸，避免选中与显隐来回触发。</summary>

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

    /// <summary>下载队列页的副标题（「暂无下载任务 / 3 个任务 · 1 个进行中」），由 RefreshQueue 写入。</summary>
    private string _queueSubtitle = "暂无下载任务";

    private bool _searching;
    private string? _searchError;

    /// <summary>本次搜索结果是不是直接来自本地库（true 时副标题 / 状态栏会标「本地」）。</summary>
    private bool _searchFromLocal;

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

    // ==================== 顶栏「刷新」按钮上的同步菜单 ====================

    /// <summary>
    /// 鼠标在按钮和菜单之间穿行时的缓冲：离开后等一小会儿再判断，
    /// 手一抖扫过去菜单不会立刻消失（进到菜单里就取消收起）。
    /// </summary>
    private readonly DispatcherTimer _syncMenuTimer = new() { Interval = TimeSpan.FromMilliseconds(240) };

    // ==================== 游戏名库同步（官网全量 + Steam 中文名） ====================


    private ScrollViewer? _listScroll;

    // 托盘三件套：都在首帧之后才建（见 EnsureTray）。图标要从 exe 里抽、菜单要解析一份 XAML，
    // 放在构造函数里等于让用户多等一截，而这东西启动时又没人看
    private WinForms.NotifyIcon? _trayIcon;
    private TrayMenuWindow? _trayMenu;
    private Drawing.Icon? _trayIconImage;

    private bool _exitRequested;

    /// <summary>缩进托盘之前的窗口状态（普通 / 最大化），从托盘回来时按它还原。</summary>
    private WindowState _stateBeforeTray = WindowState.Normal;

    public MainWindow() : this(BeginStartupDataLoad())
    {
    }

    /// <summary>
    /// 真正的构造函数。启动数据由 App 在 <c>new MainWindow()</c> 之前就先开跑
    /// （见 <see cref="BeginStartupDataLoad"/>），这样读库和「解析 XAML + 建窗口」是并行的，
    /// 等 OnLoaded 时数据通常已经好了，第一帧画出来就带着卡片。
    /// </summary>
    internal MainWindow(Task<StartupData> startupData)
    {
        _startupData = startupData;

        BootTrace.Mark("MainWindow.InitializeComponent 之前");
        InitializeComponent();
        BootTrace.Mark("MainWindow.InitializeComponent 完成");

        // 界面上的 {Binding SectionTitle} 之类全部来自 LibraryViewModel；
        // 卡片模板里的 Binding 会被列表项自己的 DataContext（TrainerItem）接管，互不影响
        DataContext = ViewModel;

        // 更新进度弹框是窗口里的一块独立视图：把自己的 VM 挂上去，
        // 里面所有 {Binding Data.Ratio} 之类都对着 SyncViewModel 解析，代码里不再直接改控件
        SyncLayer.DataContext = Sync;
        Sync.StartRequested = OnSyncStartRequested;
        Sync.CancelRequested = OnSyncCancelRequested;

        // 设置页同样是独立的一块：菜单和内容各挂一份 SettingsViewModel，
        // 外面那层 SettingsPanel 仍用 LibraryViewModel 决定整页显隐，互不干扰
        SettingsMenu.DataContext = Settings;
        SettingsScroll.DataContext = Settings;

        // 构造函数里仍然不碰数据库：数据已经在别的线程上读着，这里只挂一个空壳占位
        _library = new TrainerLibrary();
        TrainerDataService.CurrentLibrary = _library;

        QueueList.ItemsSource = DownloadManager.Instance.Tasks;
        NavList.SelectedIndex = 0;

        // 设置页「关于」那一组先把当前版本写上去，不用等联网检查
        RefreshUpdateRow();

        // 列表可视高度是「3 行装不装得下」的依据，但它要等排版跑完才有值：
        // 挂一次 SizeChanged，让卡片尺寸在拿到真实视口后再收敛一次，
        // 免得启动时按「视口 0 → 不压高度」算出个偏大的卡片，之后一直不复原
        CardList.SizeChanged += (_, _) => UpdateCardMetrics();

        DownloadManager.Instance.Changed += (_, _) => OnDownloadsChanged();

        _suggestTimer.Tick += SuggestTimer_Tick;
        _syncMenuTimer.Tick += SyncMenuTimer_Tick;

        Loaded += OnLoaded;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        SizeChanged += OnSizeChanged;
        LocationChanged += OnWindowLocationChanged;
        StateChanged += OnWindowStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        Deactivated += OnWindowDeactivated;

        ContentRendered += (_, _) =>
        {
            BootTrace.Mark("首帧已呈现（ContentRendered）");

            // 首帧已经在屏幕上，托盘才建：图标晚几十毫秒出现没人会注意，但启动路径干净了
            EnsureTray();
        };

        BootTrace.Mark("MainWindow 构造函数结束");
    }

    /// <summary>
    /// 启动数据（卡片库 + 收藏 + 缓存条数）的读取任务，由 App 在创建窗口之前就启动，
    /// 和「解析 XAML / 建窗口 / 首帧排版」并行跑，窗口出现时数据已经就绪。
    /// 读库、收藏还原、首屏封面解码全在后台线程，不占 UI 线程。
    /// </summary>
    public static Task<StartupData> BeginStartupDataLoad() => Task.Run(() =>
    {
        var library = TrainerDataService.LoadCachedLibrary()
                      ?? new TrainerLibrary { Source = TrainerWeb.Site, Pages = 1 };
        var favorites = TrainerDataService.LoadFavorites(library);
        var cached = AppDatabase.Instance.IsAvailable
            ? AppDatabase.Instance.CacheCount()
            : library.Items.Count;

        // 首屏那几张封面顺手解码好：不预热的话，绑定求值时会在 UI 线程上
        // 一张张读盘 + 解码，卡片冒出来的那一下明显顿一下
        NetImages.WarmLocal(library.Items.Take(TrainerWeb.PageSize).Select(i => i.CoverSource));

        return new StartupData(library, favorites, cached);
    });

    /// <summary>启动阶段并行读出来的数据。</summary>
    public sealed record StartupData(TrainerLibrary Library, List<TrainerItem> Favorites, int CachedCount);

    /// <summary>
    /// 建托盘图标 + 右键菜单（可重复调用，只建一次）。
    /// 从构造函数里挪出来，让「窗口出现」这一步不用等 XAML 解析和 exe 图标抽取。
    /// </summary>
    private void EnsureTray()
    {
        if (_trayIcon is not null) return;

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
            Text = "风灵月影宗藏宝阁",
            Visible = true
        };
        _trayIcon.MouseUp += (_, e) =>
        {
            // 右键 → 小菜单（打开主界面 / 退出程序）；左键 → 直接把主界面叫回来
            if (e.Button == WinForms.MouseButtons.Right)
                Dispatcher.BeginInvoke(new Action(() => _trayMenu?.ShowAtCursor()));
            else if (e.Button == WinForms.MouseButtons.Left)
                Dispatcher.BeginInvoke(new Action(ShowMainWindow));
        };
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
        // 点窗口里的别处就收起顶栏的同步菜单；点菜单本身交给条目自己处理
        if (SyncMenuPopup.IsOpen &&
            e.OriginalSource is DependencyObject hit &&
            !RefreshHost.IsAncestorOf(hit) &&
            !IsInsidePopup(SyncMenuPopup, hit))
        {
            CloseSyncMenu();
        }

        if (!SuggestPopup.IsOpen) return;

        if (e.OriginalSource is DependencyObject src)
        {
            if (SearchShell.IsAncestorOf(src)) return;

            if (IsInsidePopup(SuggestPopup, src)) return;
        }

        CheckLog("OnPreviewMouseDown → CloseSuggest");
        CloseSuggest();
    }

    /// <summary>点击目标是不是落在这个 Popup 的内容里（Popup 挂在独立窗口上，祖先链里找不到它）。</summary>
    /// <param name="popup">要判断的弹出层。</param>
    /// <param name="source">鼠标点到的元素。</param>
    private static bool IsInsidePopup(System.Windows.Controls.Primitives.Popup popup, DependencyObject? source)
    {
        if (source is null || popup.Child is not Visual root) return false;
        return ReferenceEquals(root, source) || (source is Visual visual && root.IsAncestorOf(visual));
    }

    /// <summary>
    /// 切到别的程序时把提示收掉。Popup 是独立窗口，点它的那一瞬间主窗口也会收到一次 Deactivated，
    /// 所以不能立刻关 —— 等一拍再看前台到底是谁：还是自己家的（主窗口或 Popup 窗口）就不动。
    /// </summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (SyncMenuPopup.IsOpen)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!SyncMenuPopup.IsOpen) return;

                var foreground = GetForegroundWindow();
                if (foreground == new WindowInteropHelper(this).Handle) return;
                if (foreground == PopupHandle(SyncMenuPopup)) return;

                CloseSyncMenu();
            }));
        }

        if (!SuggestPopup.IsOpen) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!SuggestPopup.IsOpen) return;

            var foreground = GetForegroundWindow();
            var main = new WindowInteropHelper(this).Handle;

            var popup = PopupHandle(SuggestPopup);

            if (foreground == main || (popup != IntPtr.Zero && foreground == popup)) return;

            CheckLog($"Deactivated → CloseSuggest（前台 {foreground}，主窗口 {main}，Popup {popup}）");
            CloseSuggest();
        }));
    }

    /// <summary>取一个 Popup 内容所在的窗口句柄，没建出来就是 0。</summary>
    /// <param name="popup">要取句柄的弹出层。</param>
    private static IntPtr PopupHandle(System.Windows.Controls.Primitives.Popup popup) =>
        popup.Child is FrameworkElement child &&
        PresentationSource.FromVisual(child) is HwndSource source
            ? source.Handle
            : IntPtr.Zero;

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
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point32 point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

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
        BootTrace.Mark("OnSourceInitialized 进入");
        ApplyRoundedCorners(WindowState != WindowState.Maximized);
        ApplyWindowBounds();

        UpdateShellClip();

        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);

        BootTrace.Mark("OnSourceInitialized 结束");
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

            // 换到缩放比例不同的显示器后，贴在按钮下面的弹出层要重新摆一次
            TrackOpenPopups();

            if (_detailOpen && DetailHost.Content is TrainerDetailView view)
                view.FitTo(RootGrid.ActualWidth, RootGrid.ActualHeight);
        }));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BootTrace.Mark("Loaded 进入");
        UpdateCardMetrics();

        // 读库的任务在 App 里就开跑了，和解析 XAML、建窗口并行；走到这里基本已经完成，
        // 所以第一帧画出来就是满的卡片，不会先闪一下空状态
        ApplyStartupData();
        BootTrace.Mark("本地数据就绪");

        Render();
        UpdateStats();
        UpdateQueueBadge();
        BootTrace.Mark("首屏卡片填充完成");

        // 游戏名库为空就顺手抓一份（有数据直接跳过），搜索框的智能提示靠它
        _ = SeedGameNamesAsync();

        // 每次启动自动查一次更新（后台跑，查到新版本才弹窗）
        CheckUpdatesOnStartup();
    }

    /// <summary>
    /// 取启动数据的读取结果并装进窗口。任务正常早就跑完了，这里等不到什么；
    /// 万一首次运行建库偏慢，宁可让窗口晚几十毫秒出现，也不要先给用户看一屏空卡片。
    /// </summary>
    private void ApplyStartupData()
    {
        try
        {
            var data = _startupData.GetAwaiter().GetResult();
            ApplyLoadedData(data.Library, data.Favorites, data.CachedCount);
        }
        catch (Exception ex)
        {
            _firstLoading = false;
            _firstError = ex.Message;
        }
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
        var library = TrainerDataService.LoadCachedLibrary()
                      ?? new TrainerLibrary { Source = TrainerWeb.Site, Pages = 1 };
        var favorites = TrainerDataService.LoadFavorites(library);
        var cached = AppDatabase.Instance.IsAvailable ? AppDatabase.Instance.CacheCount() : library.Items.Count;

        ApplyLoadedData(library, favorites, cached);
    }

    /// <summary>
    /// 把读出来的本地库 / 收藏夹装进窗口（必须在 UI 线程调用）。
    /// 启动流程在后台线程读完库后走这里，同步完重读也走这里，两条路的状态处理只写一份。
    /// </summary>
    private void ApplyLoadedData(TrainerLibrary library, List<TrainerItem> favorites, int cachedCount)
    {
        _library = library;
        TrainerDataService.CurrentLibrary = library;
        _cachedGameCount = cachedCount;

        // 读本地是同步的，不存在「正在读取」的中间态
        _firstLoading = false;
        _firstError = null;

        // 库换了对象，收藏里的引用也要换成新实例，否则详情页拿到的还是启动时的空快照
        _favorites.Clear();
        _favorites.AddRange(favorites);

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
}
