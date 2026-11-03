using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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

    /// <summary>一行放 5 张卡片。</summary>
    private const int CardsPerRow = 5;

    /// <summary>卡片之间留的空隙（像素）。</summary>
    private const double CardGap = 12;

    /// <summary>卡片下半部的标题区高度；封面高度 = 卡宽 × 9/16，正好和官网头图同比例，不裁不变形。</summary>
    private const double CardInfoHeight = 58;

    /// <summary>窗口四角圆角半径（DWM 原生圆角 + 内容裁剪用同一套数值）。</summary>
    private const double ShellCornerRadius = 12;

    /// <summary>窗口能被拖到的最小尺寸，再小布局就挤了。</summary>
    private const double MinWindowWidth = 1100;
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
    private bool _busy;

    private int _nextPage = 1;
    private bool _loadingMore;
    private bool _noMore;
    private bool _loadFailed;

    /// <summary>上次触发翻页时的滚动位置；用来保证「一次滚动只翻一页」。</summary>
    private double _lastTriggerOffset;

    /// <summary>当前生效的官网搜索词；空串表示没在搜索，列表回到正常游戏库。</summary>
    private string _keyword = "";

    /// <summary>官网搜索结果（翻页往后追加），和游戏库列表分开存。</summary>
    private readonly List<TrainerItem> _results = new();

    // 搜索结果的分页状态单独一份，退出搜索后游戏库的翻页进度不会被打乱
    private int _searchPage = 1;
    private bool _searchNoMore;
    private bool _searchFailed;
    private bool _searching;
    private string? _searchError;

    /// <summary>连续搜索时用它做序号，晚到的旧响应直接丢掉。</summary>
    private int _searchSeq;

    /// <summary>首屏正在从官网拉取（无本地数据，所以启动时一定是这个状态）。</summary>
    private bool _firstLoading = true;
    private string? _firstError;

    private double _cardW;
    private double _cardH;
    private ScrollViewer? _listScroll;
    private readonly WinForms.NotifyIcon _trayIcon;
    private readonly TrayMenuWindow _trayMenu;
    private readonly Drawing.Icon _trayIconImage;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();

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
            if (e.Button == WinForms.MouseButtons.Right)
                Dispatcher.BeginInvoke(new Action(_trayMenu.ShowAtCursor));
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowMainWindow));

        // 不再读取任何本地数据（无内置种子、无列表缓存），启动后立即向官网请求
        _library = new TrainerLibrary();
        TrainerDataService.CurrentLibrary = _library;
        _nextPage = 1;
        _favorites.AddRange(TrainerDataService.LoadFavorites(_library));

        CardList.ItemsSource = _rows;
        QueueList.ItemsSource = DownloadManager.Instance.Tasks;
        NavList.SelectedIndex = 0;

        DownloadManager.Instance.Changed += (_, _) => OnDownloadsChanged();

        Loaded += OnLoaded;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        SizeChanged += OnSizeChanged;
        StateChanged += OnWindowStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;
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

        var width = Math.Min(1440, Math.Max(900, workWidth - 32));
        var height = Math.Min(880, Math.Max(520, workHeight - 32));

        // 最大化时 WindowChrome 会把窗口向外撑出一个边框宽度（左上角跑到 -7,-7），
        // 上限如果正好卡在工作区大小，撑出来的部分会被截掉，右边和下边就会漏出一条桌面缝隙
        MaxWidth = workWidth + 40;
        MaxHeight = workHeight + 40;
        MinWidth = Math.Min(MinWindowWidth, workWidth);
        MinHeight = Math.Min(MinWindowHeight, workHeight);
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

    /// <summary>最大化 / 还原时同步圆角、描边与右上角图标。</summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var maxed = WindowState == WindowState.Maximized;

        MaxGlyph.Text = maxed ? "\uE923" : "\uE922";
        MaxButton.ToolTip = maxed ? "向下还原" : "最大化";

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

        // 先把上次抓到并落库的卡片铺上去（首屏不用等网络），再向官网刷新
        LoadCachedCards();

        Render();
        UpdateStats();
        UpdateQueueBadge();

        _ = LoadFirstPageAsync();
    }

    /// <summary>从 SQLite 缓存表里读回上次的卡片；首次运行没有缓存就跳过。</summary>
    private void LoadCachedCards()
    {
        var cached = TrainerDataService.LoadCachedLibrary();
        if (cached is null || cached.Items.Count == 0)
        {
            // 没有缓存就从头翻页，别让上次留下的页码把前几页跳过去
            TrainerDataService.SavePageCursor(1);
            return;
        }

        _library = cached;
        TrainerDataService.CurrentLibrary = _library;
        _firstLoading = false;   // 已经有内容，不用再显示「正在读取」的空状态
    }

    /// <summary>
    /// 向官网拉第一页：拿到的 24 条替换同 ID 的缓存条目，缓存里其余的（之前翻过的页）接在后面，
    /// 这样刷新不会把已经看过的内容清空。抓到的数据同时写回缓存表。
    /// </summary>
    private async Task LoadFirstPageAsync()
    {
        var previous = _library.Items.ToList();
        var byId = previous.ToDictionary(i => i.Id);

        _firstLoading = true;
        _firstError = null;
        if (previous.Count == 0) Render();

        var fetched = new List<TrainerItem>();

        try
        {
            var (items, totalPages) = await TrainerWeb.FetchPageAsync(1);
            fetched = items;

            foreach (var item in items)
            {
                if (byId.TryGetValue(item.Id, out var prev)) TrainerDataService.MergeLocal(item, prev);
            }

            var seen = items.Select(i => i.Id).ToHashSet();
            var merged = new List<TrainerItem>(items);
            foreach (var prev in previous)
            {
                if (seen.Add(prev.Id)) merged.Add(prev);
            }

            _library = new TrainerLibrary
            {
                Source = TrainerWeb.Site,
                FetchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Pages = 1,
                Items = merged
            };

            _nextPage = Math.Max(2, TrainerDataService.LoadPageCursor() + 1);
            _noMore = totalPages <= 1;

            TrainerDataService.SaveCache(items);
        }
        catch (Exception ex)
        {
            _firstError = ex.Message;

            // 有缓存就继续用缓存，只把失败写进状态栏；完全没数据才进空状态
            if (previous.Count == 0)
            {
                _library = new TrainerLibrary();
                _nextPage = 1;
                _noMore = true;
            }
        }
        finally
        {
            _firstLoading = false;
        }

        TrainerDataService.CurrentLibrary = _library;

        RelinkFavorites();

        Render();
        UpdateStats();

        if (fetched.Count > 0) _ = CacheCoversAsync(fetched);
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
                if (await TrainerFiles.CacheCoverAsync(item).ConfigureAwait(false))
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

    /// <summary>按可用宽度算出「一行 5 张」的卡片尺寸，高度按固定比例跟随，保证不变形。</summary>
    private void UpdateCardMetrics()
    {
        var avail = CardList.ActualWidth - CardList.Padding.Left - CardList.Padding.Right - 8;
        if (avail <= 0) avail = 920;

        var w = Math.Floor((avail - CardGap * (CardsPerRow - 1)) / CardsPerRow);
        w = Math.Clamp(w, 128, 260);

        // 封面按 16:9 撑满上半部（官网头图就是这个比例），下半部固定放文字
        var h = Math.Round(w * 9d / 16d) + CardInfoHeight;

        if (Math.Abs(w - _cardW) < 0.5 && Math.Abs(h - _cardH) < 0.5) return;

        _cardW = w;
        _cardH = h;
        Resources["CardW"] = w;
        Resources["CardH"] = h;
        Resources["CardInfoH"] = new GridLength(CardInfoHeight);
    }

    // ==================== 数据渲染 ====================

    /// <summary>是否处于「官网搜索结果」状态。</summary>
    private bool Searching => _keyword.Length > 0;

    // 列表接口没有浏览量字段，热门页用每款游戏的修改项目数作排序依据。
    private IEnumerable<TrainerItem> SourceItems =>
        _mode == ViewMode.Favorites ? _favorites
        : Searching ? _results
        : _libraryTab == LibraryTab.Popular
            ? _library.Items.OrderByDescending(i => i.RealOptionCount)
                .ThenByDescending(i => DateOrder(i.UpdatedText))
            : _library.Items.OrderByDescending(i => DateOrder(i.UpdatedText));

    private static DateTime DateOrder(string value) =>
        DateTime.TryParse(value.Replace('.', '-'), out var date) ? date : DateTime.MinValue;

    private void Render()
    {
        switch (_mode)
        {
            case ViewMode.Library when Searching:
                SectionTitle.Text = "搜索结果";
                SectionSubtitle.Text = $"官网搜索“{_keyword}” · 结果来自 flingtrainer.com";
                break;
            case ViewMode.Library:
                SectionTitle.Text = "Steam 单机游戏库";
                SectionSubtitle.Text = $"已经收录 {_library.Items.Count} 款游戏";
                break;
            case ViewMode.Favorites:
                SectionTitle.Text = "收藏夹";
                SectionSubtitle.Text = "收藏保存在本地 SQLite 数据库，重装/重启都不会丢";
                break;
            case ViewMode.Queue:
                SectionTitle.Text = "下载队列";
                // 副标题由 RefreshQueue() 换成真实的任务统计
                SectionSubtitle.Text = "暂无下载任务";
                break;
            case ViewMode.Settings:
                SectionTitle.Text = "设置";
                SectionSubtitle.Text = "数据源与缓存";
                break;
        }

        // 收藏夹视图下，卡片左上角会多出一个「取消收藏」按钮
        CardList.Tag = _mode == ViewMode.Favorites;
        LibraryTabs.Visibility = _mode == ViewMode.Library && !Searching
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecentTabButton.IsEnabled = _libraryTab != LibraryTab.Recent;
        PopularTabButton.IsEnabled = _libraryTab != LibraryTab.Popular;
        QueuePanel.Visibility = _mode == ViewMode.Queue ? Visibility.Visible : Visibility.Collapsed;

        if (_mode == ViewMode.Queue)
        {
            CardList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            RefreshQueue();
            UpdateFooter();
            return;
        }

        if (_mode == ViewMode.Settings)
        {
            CardList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;

            EmptyGlyph.Text = "\uE713";
            EmptyTitle.Text = "数据与缓存";
            EmptyDesc.TextAlignment = TextAlignment.Left;
            EmptyDesc.MaxWidth = 680;
            EmptyDesc.Text =
                $"数据源：{TrainerWeb.Site}（列表与详情实时抓取，抓到的卡片写入本地缓存表）\n" +
                $"卡片缓存：trainer_cache 表 · 已缓存 {AppDatabase.Instance.CacheCount()} 款\n" +
                $"图片与文件：<程序目录>\\f\\<文章ID>\\ · 封面与截图落盘，路径存在缓存表里\n" +
                $"文件目录：{DownloadManager.Instance.DownloadDir}\n" +
                $"数据库：{AppDatabase.DatabaseFile}\n" +
                $"数据库状态：{(AppDatabase.Instance.IsAvailable ? "已就绪" : "不可用 · " + AppDatabase.Instance.LastError)}\n" +
                $"浏览器 UA：{UserAgentProvider.UserAgent}\n" +
                $"高 DPI 模式：PerMonitorV2 · 当前缩放 {VisualTreeHelper.GetDpi(this).DpiScaleX * 100:0}%";

            UpdateFooter();
            return;
        }

        // 搜索走官网接口，结果已经在 _results 里，这里不再做本地过滤
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
                EmptyTitle.Text = "正在从官网读取游戏列表";
                EmptyDesc.Text = "数据实时来自 flingtrainer.com，不读取本地缓存。";
            }
            else
            {
                EmptyGlyph.Text = "\uE783";
                EmptyTitle.Text = "读取失败";
                EmptyDesc.Text = $"连不上 flingtrainer.com：{_firstError}\n\n" +
                                 "检查网络后点右上角的「同步」按钮重试。";
            }

            UpdateFooter();
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
                EmptyTitle.Text = _searching ? "正在搜索官网…" : "官网没有搜到";
                EmptyDesc.Text = _searching
                    ? $"正在向 flingtrainer.com 查询“{_keyword}”"
                    : _searchError is not null
                        ? $"搜索失败：{_searchError}"
                        : $"flingtrainer.com 上没有与“{_keyword}”相关的修改器，试试英文游戏名或换个关键词。";
            }
            else
            {
                EmptyGlyph.Text = "\uE721";
                EmptyTitle.Text = "没有找到匹配的游戏";
                EmptyDesc.Text = "游戏库是空的，点右上角的同步按钮从官网读一次。";
            }
        }
        else
        {
            CardList.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
        }

        UpdateFooter();
    }

    /// <summary>整体重建（搜索 / 切换视图）。</summary>
    private void SetItems(IEnumerable<TrainerItem> items)
    {
        _items.Clear();
        _items.AddRange(items);

        _rows.Clear();
        foreach (var row in CardRow.Split(_items))
            _rows.Add(row);

        // 换了一整套内容，滚动位置与触发点都重新算
        _lastTriggerOffset = 0;
    }

    /// <summary>翻页时只往最后一行塞，行满才新建一行，不重建已生成的卡片。</summary>
    private void AppendItems(IEnumerable<TrainerItem> items)
    {
        foreach (var item in items)
        {
            _items.Add(item);

            if (_rows.Count == 0 || _rows[^1].IsFull)
                _rows.Add(new CardRow());

            _rows[^1].Items.Add(item);
        }
    }

    // ==================== 翻页加载 ====================

    /// <summary>游戏库和搜索结果都支持滚动翻页；收藏夹 / 队列 / 设置不翻页。</summary>
    private bool CanPage => _mode == ViewMode.Library;

    /// <summary>当前这一轮翻页是否已经到底（游戏库与搜索结果各记一份）。</summary>
    private bool AtEnd
    {
        get => Searching ? _searchNoMore : _noMore;
        set { if (Searching) _searchNoMore = value; else _noMore = value; }
    }

    /// <summary>当前这一轮翻页是否失败（同上，各记一份）。</summary>
    private bool PageFailed
    {
        get => Searching ? _searchFailed : _loadFailed;
        set { if (Searching) _searchFailed = value; else _loadFailed = value; }
    }

    private void UpdateFooter()
    {
        var show = CanPage && _items.Count > 0;
        CardsFooter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        FooterLoading.Visibility = _loadingMore ? Visibility.Visible : Visibility.Collapsed;
        FooterRetry.Visibility = show && PageFailed && !_loadingMore ? Visibility.Visible : Visibility.Collapsed;
        FooterRetry.IsHitTestVisible = true;
        FooterEnd.Visibility = show && AtEnd && !_loadingMore && !PageFailed
            ? Visibility.Visible
            : Visibility.Collapsed;

        CardsFooter.IsHitTestVisible = FooterRetry.Visibility == Visibility.Visible;
        FooterEndText.Text = AtEnd ? $"已经到底了 · 共 {_items.Count} 款" : "继续滚动加载更多";
    }

    private void CardList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 只认「用户真的往下滚了」这一种变化。一页刚刚插进来时内容高度会变，
        // ScrollViewer 也会发 ScrollChanged，但 VerticalChange 是 0——旧代码把这种也算进去，
        // 于是插完一页立刻又满足「到底了」，几页连着自动加载，看着像一次性全拉下来。
        if (e.VerticalChange <= 0) return;

        AskForNextPage(e.VerticalOffset, e.ViewportHeight, e.ExtentHeight);
    }

    /// <summary>已经贴着底了还继续滚轮时 Offset 不会变、也就没有 ScrollChanged，这里补一次判断。</summary>
    private void CardList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta >= 0) return;

        var sv = _listScroll ??= FindScrollViewer(CardList);
        if (sv is null || sv.ScrollableHeight - sv.VerticalOffset > 1) return;

        AskForNextPage(sv.VerticalOffset, sv.ViewportHeight, sv.ExtentHeight, userGesture: true);
    }

    /// <summary>
    /// 要不要再要一页。硬约束：上一次请求还没回来不加载、已经到底不加载、
    /// 上次失败等用户点重试、同一次滚动只触发一页（必须再往下滚过上次触发点才会再要）。
    /// </summary>
    /// <param name="userGesture">
    /// 用户主动滚轮到底：这时滚动位置本来就到顶了、不会再变，所以不受「滚动位置必须更靠下」的限制。
    /// </param>
    private void AskForNextPage(double offset, double viewport, double extent, bool userGesture = false)
    {
        if (!CanPage || _loadingMore || AtEnd || PageFailed) return;
        if (extent <= viewport + 40) return;                // 撑不满一屏，没什么可翻的
        if (offset + viewport < extent - 120) return;       // 离底部还差一行以上
        if (!userGesture && offset <= _lastTriggerOffset + 0.5) return;  // 这次的位置没比上次触发点更靠下

        _lastTriggerOffset = offset;
        _ = LoadNextPageAsync();
    }

    private void FooterRetry_Click(object sender, RoutedEventArgs e)
    {
        PageFailed = false;
        _ = LoadNextPageAsync();
    }

    private async Task LoadNextPageAsync()
    {
        if (_loadingMore || AtEnd) return;

        if (Searching)
        {
            await LoadNextSearchPageAsync();
            return;
        }

        _loadingMore = true;
        _loadFailed = false;
        UpdateFooter();
        StatusText.Text = $"正在加载第 {_nextPage} 页…";

        try
        {
            // 一次只请求一页；如果整页都是启动时从缓存里读回来的（内容已经在列表里了），
            // 就顺延到下一页，最多连找 5 页——不然用户滚到底也看不到任何新东西。
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var page = _nextPage;
                var (items, totalPages) = await TrainerWeb.FetchPageAsync(page);

                _library.Pages = page;
                _nextPage = page + 1;
                TrainerDataService.SavePageCursor(page);

                if (items.Count == 0 || _nextPage > totalPages) _noMore = true;

                var known = _library.Items.Select(i => i.Id).ToHashSet();
                var added = items.Where(i => known.Add(i.Id)).ToList();
                if (added.Count == 0)
                {
                    if (_noMore) break;
                    continue;
                }

                foreach (var item in added)
                    _library.Items.Add(item);

                // 新的一页同样落库，下次启动直接读缓存
                TrainerDataService.SaveCache(added);
                _ = CacheCoversAsync(added);

                if (CanPage)
                {
                    if (_libraryTab == LibraryTab.Popular) SetItems(SourceItems);
                    else AppendItems(added);
                    UpdateStats();
                    StatusText.Text = $"已加载 {_library.Items.Count} 款游戏";
                }

                break;
            }
        }
        catch
        {
            _loadFailed = true;
            StatusText.Text = "加载下一页失败，可点击底部重试";
        }
        finally
        {
            _loadingMore = false;
            UpdateFooter();
        }
    }

    /// <summary>搜索结果滚动到底时再向官网要下一页（关键词不变，只翻 page）。</summary>
    private async Task LoadNextSearchPageAsync()
    {
        // 与游戏库翻页共用同一把「正在翻页」的闸：同一时刻只允许一页在飞
        if (_loadingMore || AtEnd) return;

        _loadingMore = true;
        _searchFailed = false;
        UpdateFooter();
        StatusText.Text = $"正在加载搜索结果第 {_searchPage} 页…";

        var keyword = _keyword;

        try
        {
            var (items, totalPages) = await TrainerWeb.SearchAsync(keyword, _searchPage);

            // 期间换了关键词 / 退出搜索：这一页直接丢掉
            if (!Searching || _keyword != keyword) return;

            var known = _results.Select(i => i.Id).ToHashSet();
            var added = items.Where(i => known.Add(i.Id)).ToList();
            _results.AddRange(added);

            if (added.Count > 0)
            {
                AppendItems(added);
                _ = CacheCoversAsync(added);
            }

            _searchPage++;
            if (items.Count == 0 || _searchPage > totalPages) _searchNoMore = true;
        }
        catch
        {
            _searchFailed = true;
            StatusText.Text = "加载搜索下一页失败，可点击底部重试";
        }
        finally
        {
            _loadingMore = false;
            UpdateFooter();
        }
    }

    // ==================== 官网搜索 ====================

    /// <summary>
    /// 按回车才走这里：向 flingtrainer.com 的搜索接口要结果（和官网搜索页同一套结果与顺序）。
    /// 输入过程中不会发请求，也不会动当前列表。
    /// </summary>
    private async Task SubmitSearchAsync()
    {
        var keyword = SearchBox.Text.Trim();

        // 清空后回车 = 退出搜索，回到原来的游戏库
        if (keyword.Length == 0)
        {
            ExitSearch();
            return;
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
        _results.Clear();
        _searchPage = 1;
        _searchNoMore = false;
        _searchFailed = false;
        _searchError = null;
        _searching = true;

        Render();
        ScrollToTop();
        StatusText.Text = $"正在从官网搜索“{keyword}”…";

        try
        {
            var (items, totalPages) = await TrainerWeb.SearchAsync(keyword, 1);

            // 期间又搜了别的关键词：这次的结果作废
            if (seq != _searchSeq) return;

            _results.AddRange(items);
            _searchPage = 2;
            _searchNoMore = items.Count == 0 || totalPages <= 1;

            if (items.Count > 0)
            {
                TrainerDataService.SaveCache(items);
                _ = CacheCoversAsync(items);
            }

            StatusText.Text = $"官网搜索“{keyword}”· {items.Count} 款结果";
        }
        catch (Exception ex)
        {
            if (seq != _searchSeq) return;
            _searchError = ex.Message;
            _searchNoMore = true;
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

    /// <summary>退出搜索：清掉结果，列表回到游戏库（翻页进度还停在原来那一页）。</summary>
    private void ExitSearch(bool clearText = false)
    {
        var was = Searching;

        // 先把状态清干净，再去改输入框内容——否则 TextChanged 会再进来一次
        _searchSeq++;
        _keyword = "";
        _results.Clear();
        _searchPage = 1;
        _searchNoMore = false;
        _searchFailed = false;
        _searching = false;
        _searchError = null;

        if (clearText && SearchBox.Text.Length > 0) SearchBox.Text = "";

        Render();

        if (!was) return;

        ScrollToTop();
        StatusText.Text = $"已退出搜索 · 已加载 {_library.Items.Count} 款游戏";
    }

    /// <summary>刷新左侧栏的同步时间与收藏数量。</summary>
    private void UpdateStats()
    {
        SidebarSyncText.Text = string.IsNullOrEmpty(_library.FetchedAt)
            ? "等待同步"
            : $"同步于 {_library.FetchedAt}";

        NavFavLabel.Text = _favorites.Count > 0 ? $"收藏夹 · {_favorites.Count}" : "收藏夹";

        // 官网没有「一共多少款」这种总数可拿，这里只报已经真实加载到手的数量
        StatusText.Text = Searching
            ? $"官网搜索“{_keyword}”· 已加载 {_results.Count} 款"
            : $"已经收录 {_library.Items.Count} 款游戏";
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
        UpdateFooter();
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

    private void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        var count = DownloadManager.Instance.ClearFinished();
        StatusText.Text = count == 0 ? "没有可清理的任务" : $"已清理 {count} 个任务";
        RefreshQueue();
    }

    private void ScrollToTop()
    {
        _lastTriggerOffset = 0;
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
    }

    /// <summary>回车才向官网搜索（不用边打字边搜）；Esc 清空并退出搜索。</summary>
    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
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
        view.FitTo(RootGrid.ActualWidth, RootGrid.ActualHeight);

        DetailHost.Content = view;
        DetailLayer.Visibility = Visibility.Visible;
        _detailOpen = true;

        // 先把布局算完，动画只做位图合成：期间不重排、不重绘文字，稳定 60 帧
        DetailHost.BeginAnimation(OpacityProperty, null);
        DetailHost.Opacity = 0;
        DetailSlide.BeginAnimation(TranslateTransform.YProperty, null);
        DetailSlide.Y = 10;
        DetailHost.UpdateLayout();

        DetailHost.CacheMode = new BitmapCache();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DetailHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));

        var slide = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
        slide.Completed += (_, _) =>
        {
            // 动画结束后恢复实时渲染，滚动与文字才清晰；内容与图片再依次上屏
            DetailHost.CacheMode = null;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                _ = view.LoadImagesAsync();
                view.LoadOptions();
            }));
        };
        DetailSlide.BeginAnimation(TranslateTransform.YProperty, slide);
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

        // 只有事件源就是遮罩层本身才算「点在外面」，点到弹框的任何内容都不关
        if (!ReferenceEquals(e.OriginalSource, DetailLayer)) return;

        e.Handled = true;
        CloseDetail();
    }

    private void CloseDetail()
    {
        if (!_detailOpen) return;
        _detailOpen = false;

        DetailHost.CacheMode = new BitmapCache();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90));
        fade.Completed += (_, _) =>
        {
            DetailLayer.Visibility = Visibility.Collapsed;
            DetailHost.Content = null;
            DetailHost.CacheMode = null;
            DetailHost.BeginAnimation(OpacityProperty, null);
        };
        DetailHost.BeginAnimation(OpacityProperty, fade);
        DetailSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, 6, TimeSpan.FromMilliseconds(110)));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;

        var rotate = new RotateTransform();
        RefreshGlyph.RenderTransform = rotate;
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });

        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在从 flingtrainer.com 同步…";

        var fresh = await TrainerDataService.RefreshAsync(_library);

        rotate.BeginAnimation(RotateTransform.AngleProperty, null);

        if (fresh is null)
        {
            StatusText.Text = "同步失败，继续使用本地数据";
            StatusDot.Fill = (Brush)FindResource("B.Accent");
        }
        else
        {
            _library = fresh;
            TrainerDataService.CurrentLibrary = fresh;
            RemapFavorites();
            _nextPage = 2;
            _noMore = false;
            _loadFailed = false;
            Render();
            UpdateStats();
            _ = CacheCoversAsync(_library.Items);
            StatusText.Text = $"同步完成 · {_library.Items.Count} 款游戏";
            StatusDot.Fill = (Brush)FindResource("B.Green");
        }

        RefreshButton.IsEnabled = true;
        _busy = false;
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

    // ==================== 窗口 ====================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* 忽略拖动异常 */ }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => MinimizeToTray();

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeToTray()
    {
        ShowInTaskbar = false;
        WindowState = WindowState.Minimized;
    }

    private void ShowMainWindow()
    {
        ShowInTaskbar = true;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        _trayIcon.Visible = false;
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
        MinimizeToTray();
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
        if (e.Key == Key.Escape && _detailOpen)
        {
            CloseDetail();
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
        DetailLayer.RaiseEvent(maskClick);
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
