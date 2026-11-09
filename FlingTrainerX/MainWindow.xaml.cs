using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FlingTrainerX.Controls;
using FlingTrainerX.Interop;
using FlingTrainerX.Models;
using FlingTrainerX.Services;
using FlingTrainerX.ViewModels;
using FlingTrainerX.Views;

namespace FlingTrainerX;

/// <summary>
/// 主窗口。这里是「视图」：构造时把 ViewModel / 弹窗服务挂好，之后只做
/// 窗口自身的事 —— 外框圆角、跟随 DPI 的尺寸、卡片排版尺寸、托盘、键盘快捷键。
/// <para>
/// 业务状态全在 <see cref="ViewModels.MainWindowVM"/> 及其子 VM 里，界面文字、显隐、
/// 选中项全部走绑定，按钮全部走命令；这个文件里再没有「xxx.Text = ...」这种回填。
/// </para>
/// <para>
/// 留在这里的都是「必须碰控件才算数」的活：拖动 / 双击最大化、卡片封面悬停放大、
/// 浮层与详情动画（<see cref="Views.AppOverlays"/>）、鼠标悬停展开同步菜单、
/// 方向键与焦点、点遮罩关闭浮层、以及 Esc 时先看哪一层开着。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 固定版心：窗口默认 1280×780，右栏可用约 1018 → 版心 1020，一行 5 张、每张 193；
    /// 每页 15 条正好 3 行铺满一屏（不出滚动条）。窗口更宽时版心最多放到 1460。
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
    /// 卡片下半部的标题区高度：两行 14 号标题（行高 20 = 40）+ 一行标签（约 22）+ 上下内边距 16 ≈ 84。
    /// 封面高度 = 卡宽 × 9/16，正好和官网头图同比例，不裁不变形。
    /// </summary>
    private const double CardInfoHeight = 84;

    /// <summary>窗口四角圆角半径（DWM 原生圆角 + 内容裁剪用同一套数值）。</summary>
    private const double ShellCornerRadius = 12;

    /// <summary>本地库 / 收藏夹 / 本地分页 / 搜索数据。</summary>
    private readonly LibraryCatalog _catalog = new();

    /// <summary>「清除缓存」那套流程（自检要读它的完成标记）。</summary>
    private readonly CacheResetService _cacheReset = new();

    /// <summary>启动阶段在后台并行读的数据；OnLoaded 时取结果，第一帧就带着卡片。</summary>
    private readonly Task<LibraryCatalog.StartupSnapshot> _startupData;

    /// <summary>托盘图标（右键菜单在里面）。</summary>
    private readonly TrayService _tray = new();

    /// <summary>关窗口时把 1 分钟的自动检查更新这类后台动作一起取消。</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>鼠标在「刷新」按钮和同步菜单之间穿行时的缓冲，手抖扫过去菜单不会立刻消失。</summary>
    private readonly DispatcherTimer _syncMenuTimer = new() { Interval = TimeSpan.FromMilliseconds(240) };

    private double _cardW;
    private double _cardH;

    private ScrollViewer? _listScroll;

    private bool _exitRequested;

    /// <summary>缩进托盘之前的窗口状态（普通 / 最大化），从托盘回来时按它还原。</summary>
    private WindowState _stateBeforeTray = WindowState.Normal;

    /// <summary>根 ViewModel：XAML 的 DataContext 就是它。</summary>
    public MainWindowVM VM { get; }

    /// <summary>四层浮层的开合动画 + 确认框（<see cref="IAppDialogService"/> 的实现）。</summary>
    public AppOverlays Overlays { get; }

    /// <summary>首页 VM 的快捷入口（自检和各处回调都直接用它）。</summary>
    public MainWindowLibraryVM Library => VM.Library;

    /// <summary>更新弹框 VM 的快捷入口。</summary>
    public MainWindowSyncVM Sync => VM.Sync;

    public MainWindow() : this(BeginStartupDataLoad())
    {
    }

    /// <summary>
    /// 真正的构造函数。启动数据由 App 在 <c>new MainWindow()</c> 之前就先开跑，
    /// 这样读库和「解析 XAML + 建窗口」是并行的，等 OnLoaded 时数据通常已经好了。
    /// </summary>
    internal MainWindow(Task<LibraryCatalog.StartupSnapshot> startupData)
    {
        _startupData = startupData;

        BootTrace.Mark("MainWindow.InitializeComponent 之前");
        InitializeComponent();
        BootTrace.Mark("MainWindow.InitializeComponent 完成");

        // 三层依赖装好：服务 → 弹窗服务（要控件） → 根 ViewModel（要弹窗服务）
        var updates = new UpdateCoordinator();

        var confirm = new ConfirmDialogVM();
        var updateDialog = new UpdateDialogVM();
        var updateDownload = new UpdateDownloadVM();

        Overlays = new AppOverlays(this, CollectOverlayElements(), confirm, updateDialog, updateDownload,
            () => _listScroll ??= FindScrollViewer(CardList));

        VM = new MainWindowVM(_catalog, updates, _cacheReset, confirm, updateDialog, updateDownload, Overlays);

        // 整个窗口的 DataContext 是根 VM：界面上的 {Binding StatusText}、
        // {Binding Library.SectionTitle} 之类全部对着它解析；
        // 卡片模板里的 Binding 会被列表项自己的 DataContext（TrainerItem）接管，互不影响
        DataContext = VM;

        // 窗口级动作：VM 抛事件，这里执行（拖动 / 隐藏 / 真关都只有窗口能做）
        VM.HideToTrayRequested += (_, _) => HideToTray();
        VM.CloseRequested += (_, _) => Close();
        VM.HideForLaunchRequested += (_, _) => HideForLaunchedApp();

        // 更新弹窗点了「立即更新」：收场动画走完之后由设置页 VM 去装
        Overlays.UpdateAccepted += async (_, _) => await VM.Settings.StartInstallAsync();

        // 托盘：左键 / 「打开主界面」叫回窗口，右键菜单里的「退出程序」真退
        _tray.OpenRequested += (_, _) => ShowMainWindow();
        _tray.ExitRequested += (_, _) => ExitFromTray();

        // 栏里的「更新于 …」先用当前版本占位，不用等联网检查
        VM.Settings.RefreshUpdateRow();

        // 列表可视高度是「3 行装不装得下」的依据，但它要等排版跑完才有值
        CardList.SizeChanged += (_, _) => UpdateCardMetrics();
        VM.Library.ItemsChanged += (_, _) => UpdateCardMetrics();

        _syncMenuTimer.Tick += SyncMenuTimer_Tick;

        Loaded += OnLoaded;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        SizeChanged += OnSizeChanged;
        LocationChanged += (_, _) => TrackOpenPopups();
        StateChanged += OnWindowStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        Deactivated += OnWindowDeactivated;

        ContentRendered += (_, _) =>
        {
            BootTrace.Mark("首帧已呈现（ContentRendered）");

            // 首帧已经在屏幕上，托盘才建：图标晚几十毫秒出现没人会注意，但启动路径干净了
            _tray.EnsureCreated("风灵月影宗藏宝阁");
        };

        BootTrace.Mark("MainWindow 构造函数结束");
    }

    /// <summary>
    /// 启动数据（卡片库 + 收藏 + 缓存条数）的读取任务，由 App 在创建窗口之前就启动。
    /// </summary>
    public static Task<LibraryCatalog.StartupSnapshot> BeginStartupDataLoad() => LibraryCatalog.LoadStartupAsync();

    // ==================== 启动 ====================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BootTrace.Mark("Loaded 进入");
        UpdateCardMetrics();

        // 读库的任务在 App 里就开跑了，和解析 XAML、建窗口并行；走到这里基本已经完成
        ApplyStartupData();
        BootTrace.Mark("本地数据就绪");

        var tasks = DownloadManager.Instance.Tasks;
        VM.UpdateQueueBadge(tasks.Count, tasks.Count(t => t.IsActive));

        BootTrace.Mark("首屏卡片填充完成");

        // 静默准备：游戏名库空了就抓一份；启动 1 分钟后自动查一次更新
        _ = VM.InitializeAsync(_lifetime.Token);
    }

    /// <summary>
    /// 取启动数据的读取结果并装进库。任务正常早就跑完了；
    /// 万一首次运行建库偏慢，宁可让窗口晚几十毫秒出现，也不要先给用户看一屏空卡片。
    /// </summary>
    private void ApplyStartupData()
    {
        try
        {
            _catalog.Apply(_startupData.GetAwaiter().GetResult());
            VM.Library.OnLibraryLoaded();
        }
        catch (Exception ex)
        {
            VM.Library.OnLibraryLoadFailed(ex.Message);
        }
    }

    // ==================== 浮层控件收集 ====================

    /// <summary>把四层浮层里要播动画的控件交给 <see cref="AppOverlays"/>。</summary>
    private OverlayElements CollectOverlayElements() => new()
    {
        AboutLayer = AboutLayer,
        AboutBackdrop = AboutBackdrop,
        AboutCard = AboutCard,
        AboutScale = AboutScale,
        AboutShift = AboutShift,
        AboutBody = AboutBody,

        SupportLayer = SupportLayer,
        SupportBackdrop = SupportBackdrop,
        SupportCard = SupportCard,
        SupportScale = SupportScale,
        SupportShift = SupportShift,
        AlipayFrame = AlipayFrame,
        WxpayFrame = WxpayFrame,
        AlipayBrush = AlipayBrush,
        WxpayBrush = WxpayBrush,

        UpdateLayer = UpdateLayer,
        UpdateBackdrop = UpdateBackdrop,
        UpdateCard = UpdateCard,
        UpdateScale = UpdateScale,
        UpdateShift = UpdateShift,

        DownloadLayer = DownloadLayer,
        DownloadBackdrop = DownloadBackdrop,
        DownloadCard = DownloadCard,
        DownloadScale = DownloadScale,
        DownloadShift = DownloadShift,

        ConfirmOkButton = ConfirmOkButton,
        ConfirmLayer = ConfirmLayer,
        ConfirmBackdrop = ConfirmBackdrop,
        ConfirmCard = ConfirmCard,
        ConfirmScale = ConfirmScale,
        ConfirmShift = ConfirmShift,

        DetailLayer = DetailLayer,
        DetailBackdrop = DetailBackdrop,
        DetailHost = DetailHost,
        DetailScale = DetailScale,
        DetailSlide = DetailSlide
    };

    // ==================== 窗口外框 ====================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        BootTrace.Mark("OnSourceInitialized 进入");

        WindowChrome.ApplyRoundedCorners(this, WindowState != WindowState.Maximized);
        WindowChrome.ApplyWindowBounds(this);
        UpdateShellClip();

        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);

        BootTrace.Mark("OnSourceInitialized 结束");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024)   // WM_GETMINMAXINFO
            WindowChrome.ApplyMaximizedBounds(this, hwnd, lParam, ref handled);

        return IntPtr.Zero;
    }

    /// <summary>内容跟着外框一起裁圆角，侧栏和图片不会溢出到圆角外面。</summary>
    private void UpdateShellClip()
    {
        // 最大化时窗口铺满工作区，四角是直角
        var radius = WindowState == WindowState.Maximized ? 0 : ShellCornerRadius;
        WindowChrome.UpdateShellClip(Shell, radius);
    }

    /// <summary>窗口尺寸固定，状态变化只处理圆角与描边（缩进托盘 / 恢复时保持圆角）。</summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var maxed = WindowState == WindowState.Maximized;

        // 铺满屏幕时圆角和描边都要收掉，否则屏幕边缘会露出一圈亮线
        Shell.CornerRadius = new CornerRadius(maxed ? 0 : ShellCornerRadius);
        Shell.BorderThickness = new Thickness(maxed ? 0 : 1);

        WindowChrome.ApplyRoundedCorners(this, !maxed);
        UpdateShellClip();
    }

    /// <summary>窗口被拖到另一块缩放比例不同的显示器上时，重新按新显示器收一次尺寸。</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // 消息到达时窗口位置可能还在切换途中，等这一轮布局走完再按新显示器算
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            WindowChrome.ApplyWindowBounds(this);

            // 换到缩放比例不同的显示器后，贴在按钮下面的弹出层要重新摆一次
            TrackOpenPopups();

            Overlays.FitDetail(RootGrid.ActualWidth, RootGrid.ActualHeight);
        }));
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateShellClip();
        UpdateCardMetrics();

        // 弹框尺寸是窗口的百分比，窗口一变就跟着重算
        Overlays.FitDetail(RootGrid.ActualWidth, RootGrid.ActualHeight);

        // 窗口尺寸变了，贴在按钮下面的弹出层也要重新摆一次
        TrackOpenPopups();
    }

    // ==================== 卡片尺寸 ====================

    /// <summary>
    /// 固定版心布局：内容区最宽 <see cref="MaxContentWidth"/>，多出来的宽度平均留在两侧。
    /// 一行恒定 5 张，卡片宽度随窗口宽度等比变化（1080 窗口 ≈ 156，1280 窗口 ≈ 193），
    /// 高度按封面 16:9 + 固定标题区跟随，永不变形。
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

        // 列表右侧给滚动条留了 6px（CardListBox 的 Padding），剩下的整条宽度由 5 张卡片 + 4 个间距精确分完
        var avail = Math.Max(0, contentW - CardListGutter);
        var w = CardRowPanel.CardWidthOf(avail, CardRow.Capacity, CardGap);
        w = Math.Clamp(w, MinCardWidth, MaxCardWidth);

        // 每页 15 条 = 3 行：卡片再宽也不能让 3 行撑出列表可视高度，
        // 否则默认一屏就会冒出滚动条。窗口很高时才允许跟着放大（上限仍是 MaxCardWidth）。
        // 超过一页（收藏夹、以后要是有更大的页）就不压了：按宽度自然排，让列表自己滚动。
        var viewport = CardList.ActualHeight;
        if (viewport > 120 && VM.Library.Items.Count <= TrainerWeb.PageSize)
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
        if (VM.Library.Items.Count == 0 || oldRowHeight <= 0) return;

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

    // ==================== 滚动 ====================

    /// <summary>把卡片列表滚回顶部。</summary>
    public void ScrollToTop()
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

    // ==================== 同步菜单 ====================

    /// <summary>鼠标移到刷新按钮上：展开同步菜单。</summary>
    private void RefreshHost_MouseEnter(object sender, MouseEventArgs e) => OpenSyncMenu();

    /// <summary>展开同步菜单：正在同步时两项置灰，免得点了没反应。</summary>
    private void OpenSyncMenu()
    {
        _syncMenuTimer.Stop();

        var busy = VM.Sync.IsRunning;
        SyncMenuExisting.IsEnabled = !busy;
        SyncMenuAll.IsEnabled = !busy;

        SyncMenuPopup.IsOpen = true;
    }

    /// <summary>鼠标离开按钮：先起计时，进了菜单就不收。</summary>
    private void RefreshHost_MouseLeave(object sender, MouseEventArgs e) => _syncMenuTimer.Start();

    /// <summary>鼠标进到菜单里：取消收起。</summary>
    private void SyncMenu_MouseEnter(object sender, MouseEventArgs e) => _syncMenuTimer.Stop();

    /// <summary>鼠标离开菜单：同样起计时，回到按钮上会自己取消。</summary>
    private void SyncMenu_MouseLeave(object sender, MouseEventArgs e) => _syncMenuTimer.Start();

    /// <summary>计时到点：鼠标真的不在按钮 / 菜单上了才收起。</summary>
    private void SyncMenuTimer_Tick(object? sender, EventArgs e)
    {
        if (RefreshHost.IsMouseOver || SyncMenuBorder.IsMouseOver)
        {
            _syncMenuTimer.Stop();
            return;
        }

        // 按住左键多半正在拖窗口：这会儿先不收起，松手之后再判一次
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            _syncMenuTimer.Start();
            return;
        }

        _syncMenuTimer.Stop();
        SyncMenuPopup.IsOpen = false;
    }

    /// <summary>收起同步菜单（点条目、点窗口别处、切走窗口时都调它）。</summary>
    private void CloseSyncMenu()
    {
        if (!SyncMenuPopup.IsOpen) return;

        _syncMenuTimer.Stop();
        SyncMenuPopup.IsOpen = false;
    }

    /// <summary>
    /// 窗口被拖动时，把已经打开的弹出层重新摆一次位置。
    ///
    /// Popup 是挂在<b>独立窗口</b>上的，它的位置只在「目标元素自己发生布局变化」时才重算；
    /// 拖整个窗口并不会让按钮重新布局，于是菜单就留在屏幕原地。解决办法是把偏移量
    /// 抖 0.01 像素再抖回来，逼 Popup 按目标的新屏幕坐标重新定位（肉眼看不出来）。
    /// </summary>
    private void TrackOpenPopups()
    {
        Nudge(SyncMenuPopup);
        Nudge(SuggestPopup);

        static void Nudge(Popup? popup)
        {
            if (popup is null || !popup.IsOpen) return;

            var offset = popup.HorizontalOffset;
            popup.HorizontalOffset = offset + 0.01;
            popup.HorizontalOffset = offset;
        }
    }

    // ==================== 输入 ====================

    /// <summary>
    /// 提示列表改成 StaysOpen=True 之后，点窗口别处要自己收。
    /// 两种点击要放行：搜索框（继续输入）和提示列表本身（要选中那一条）。
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
        VM.Library.CloseSuggest();
    }

    /// <summary>点击目标是不是落在这个 Popup 的内容里（Popup 挂在独立窗口上，祖先链里找不到它）。</summary>
    private static bool IsInsidePopup(Popup popup, DependencyObject? source)
    {
        if (source is null || popup.Child is not Visual root) return false;
        return ReferenceEquals(root, source) || (source is Visual visual && root.IsAncestorOf(visual));
    }

    /// <summary>
    /// 切到别的程序时把提示收掉。Popup 是独立窗口，点它的那一瞬间主窗口也会收到一次 Deactivated，
    /// 所以不能立刻关 —— 等一拍再看前台到底是谁。
    /// </summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (SyncMenuPopup.IsOpen)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!SyncMenuPopup.IsOpen) return;

                var foreground = WindowChrome.ForegroundWindow;
                if (foreground == new WindowInteropHelper(this).Handle) return;
                if (foreground == WindowChrome.PopupHandle(SyncMenuPopup)) return;

                CloseSyncMenu();
            }));
        }

        if (!SuggestPopup.IsOpen) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!SuggestPopup.IsOpen) return;

            var foreground = WindowChrome.ForegroundWindow;
            var main = new WindowInteropHelper(this).Handle;
            var popup = WindowChrome.PopupHandle(SuggestPopup);

            if (foreground == main || (popup != IntPtr.Zero && foreground == popup)) return;

            CheckLog($"Deactivated → CloseSuggest（前台 {foreground}，主窗口 {main}，Popup {popup}）");
            VM.Library.CloseSuggest();
        }));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 同步框是模态的：Esc 不关它，要停只能点「取消同步」
        if (e.Key == Key.Escape && VM.Sync.IsOpen)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && AboutLayer.Visibility == Visibility.Visible)
        {
            Overlays.CloseAbout();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && UpdateLayer.Visibility == Visibility.Visible)
        {
            // 正在下载 / 安装时不许用 Esc 糊掉弹窗，安装流程一旦开始就不能半途而废
            if (!VM.Settings.IsUpdateInstalling) Overlays.CloseUpdate();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SupportLayer.Visibility == Visibility.Visible)
        {
            Overlays.CloseSupport();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && VM.Confirm.Answer is not null)
        {
            Overlays.CloseConfirm(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Overlays.IsDetailOpen)
        {
            Overlays.CloseDetail();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SuggestPopup.IsOpen)
        {
            // 提示列表开着时，Esc 只收提示，不动搜索框里的字
            VM.Library.CloseSuggest();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && VM.Library.IsSearching)
        {
            // 搜索框有焦点时自己的 KeyDown 已经处理过，这里兜住「焦点不在搜索框」的情况
            VM.Library.ExitSearch(clearText: true);
            e.Handled = true;
        }
        else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // 弹窗开着时 Ctrl+K 不去抢焦点：用户多半是想在上面复制条款
            if (AboutLayer.Visibility == Visibility.Visible || VM.Confirm.Answer is not null || Overlays.IsDetailOpen) return;

            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 窗口是固定尺寸（不能拉大缩小），双击标题栏也不做最大化，只负责拖动
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* 忽略拖动异常 */ }
        }
    }

    /// <summary>点遮罩（弹框外那片半透明区域）关闭弹框；点在弹框内部不触发。</summary>
    private void DetailLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!Overlays.IsDetailOpen) return;

        // 只有事件源就是那层遮罩矩形才算「点在外面」，点到弹框的任何内容都不关
        if (!ReferenceEquals(e.OriginalSource, DetailBackdrop)) return;

        e.Handled = true;
        Overlays.CloseDetail();
    }

    private void AboutBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Overlays.CloseAbout();

    private void SupportBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Overlays.CloseSupport();

    private void UpdateBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Overlays.CloseUpdate();

    private void ConfirmBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Overlays.CloseConfirm(false);

    // ==================== 卡片交互 ====================

    /// <summary>卡片封面那个 Border 在模板里的名字。</summary>
    private const string CoverArtName = "CoverArt";

    private void Card_MouseEnter(object sender, MouseEventArgs e) => ZoomCover(sender as DependencyObject, 1.08);

    private void Card_MouseLeave(object sender, MouseEventArgs e) => ZoomCover(sender as DependencyObject, 1.0);

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

    /// <summary>搜索框里的方向键 / 回车 / Esc：光标与焦点是视图的事，选哪一条、搜什么交给 VM。</summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SuggestPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    e.Handled = true;
                    VM.Library.MoveSuggest(+1);
                    return;
                case Key.Up:
                    e.Handled = true;
                    VM.Library.MoveSuggest(-1);
                    return;
                case Key.Escape:
                    e.Handled = true;
                    VM.Library.CloseSuggest();
                    return;
                case Key.Enter:
                    e.Handled = true;
                    // 选中了就用选中的那一条，没选就直接搜输入框里的字
                    if (SuggestList.SelectedItem is GameNameRow row)
                    {
                        VM.Library.ApplySuggest(row);
                    }
                    else
                    {
                        VM.Library.CloseSuggest();
                        _ = VM.Library.SubmitSearchAsync();
                    }
                    return;
            }
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            VM.Library.CloseSuggest();
            _ = VM.Library.SubmitSearchAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            VM.Library.ExitSearch(clearText: true);
        }
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
        VM.Library.ApplySuggest(row);
    }

    private static GameNameRow? RowFromElement(object? source)
    {
        var dep = source as DependencyObject;
        while (dep is not null and not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);

        return (dep as ListBoxItem)?.DataContext as GameNameRow;
    }

    /// <summary>提示列表打开后把焦点还给搜索框。</summary>
    private void SuggestPopup_Opened(object? sender, EventArgs e)
    {
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    /// <summary>设置页换了一组：换组后滚回顶部（动作本身由 SelectedIndex 双向绑定完成）。</summary>
    private void SettingsMenu_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SettingsScroll.ScrollToTop();
    }

    // ==================== 托盘与关窗 ====================

    /// <summary>
    /// 收进托盘：不是「最小化」，是把窗口真正藏起来（SW_HIDE 那一档）——
    /// 任务栏按钮、Alt+Tab 条目一起消失，只剩右下角的托盘图标。
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
    /// 二来等修改器的窗口先拿到前台，不跟它抢焦点。
    /// </summary>
    private void HideForLaunchedApp()
    {
        if (_exitRequested) return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_exitRequested) return;

            HideToTray();
        };
        timer.Start();
    }

    /// <summary>把主界面亮出来并置前。托盘菜单用它；「双击第二次图标」时也走这里。</summary>
    public void ShowMainWindow()
    {
        if (!IsVisible) Show();

        // 隐藏期间窗口状态可能被系统压成 Minimized，按藏之前的样子还原
        if (WindowState == WindowState.Minimized) WindowState = _stateBeforeTray;

        Activate();
        Topmost = true;      // 抢一次前台：Windows 不允许后台进程直接抢焦点，先置顶再放开
        Topmost = false;
        Focus();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        _tray.HideIcon();

        // 后台还在同步的话，先把它掐掉再退，别留一堆 http 请求挂着
        try { VM.Sync.Cts?.Cancel(); } catch { }

        Application.Current.Shutdown();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
        {
            _tray.HideIcon();
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _lifetime.Cancel();
        _tray.Dispose();
    }

    // ==================== 自检用的薄适配层 ====================

    /// <summary>自检里点卡片就是打开详情。</summary>
    private void OpenDetail(TrainerItem item) => VM.Library.OpenDetailCommand.Execute(item);

    /// <summary>自检里关详情。</summary>
    private void CloseDetail() => Overlays.CloseDetail();

    /// <summary>自检里打开「关于」。</summary>
    private void ShowAbout() => VM.ShowAboutCommand.Execute(null);

    /// <summary>自检里关「关于」。</summary>
    private void CloseAbout() => Overlays.CloseAbout();

    /// <summary>自检里直接摆一个更新弹窗。</summary>
    private void ShowUpdateDialog(UpdateRelease release, bool prompt) => Overlays.ShowUpdate(release, prompt);

    /// <summary>自检里关更新弹窗。</summary>
    private void CloseUpdateDialog() => Overlays.CloseUpdate();

    /// <summary>自检里整页重排。</summary>
    private void Render() => VM.Library.Refresh();

    /// <summary>自检里替换当前页的卡片。</summary>
    private void SetItems(IEnumerable<TrainerItem> items)
    {
        VM.Library.SetItems(items);
        Render();
    }

    private Task SubmitSearchAsync() => VM.Library.SubmitSearchAsync();

    private void ExitSearch(bool clearText = false) => VM.Library.ExitSearch(clearText);

    /// <summary>自检才有的调试日志：记录提示弹窗是谁关掉的，正式运行不会写。</summary>
#if DEBUG
    private string? _selfCheckLog;

    /// <summary>自检日志要写到哪个文件（由自检入口设好）。</summary>
    internal void EnableSelfCheckLog(string path) => _selfCheckLog = path;
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
}
