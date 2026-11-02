using System.Collections.ObjectModel;
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
using FlingTrainerX.Models;
using FlingTrainerX.Services;
using FlingTrainerX.Views;

namespace FlingTrainerX;

public partial class MainWindow : Window
{
    private enum ViewMode { Library, Favorites, Queue, Settings }

    /// <summary>一行放 5 张卡片。</summary>
    private const int CardsPerRow = 5;

    /// <summary>卡片之间留的空隙（像素）。</summary>
    private const double CardGap = 12;

    /// <summary>卡片高宽比，按 178x244 的设计比例，窗口变化时等比缩放，卡片不会变形。</summary>
    private const double CardAspect = 244d / 178d;

    private TrainerLibrary _library = new();

    /// <summary>当前视图实际展示的数据（翻页会往后追加）。</summary>
    private readonly List<TrainerItem> _items = new();

    /// <summary>虚拟滚动的最小单位：一行 5 张卡片。</summary>
    private readonly ObservableCollection<CardRow> _rows = new();

    /// <summary>收藏列表，按收藏时间倒序（来自 SQLite）。</summary>
    private readonly List<TrainerItem> _favorites = new();

    private ViewMode _mode = ViewMode.Library;
    private bool _detailOpen;
    private bool _busy;

    private int _nextPage = 2;
    private bool _loadingMore;
    private bool _noMore;
    private bool _loadFailed;

    private double _cardW;
    private double _cardH;
    private ScrollViewer? _listScroll;

    public MainWindow()
    {
        InitializeComponent();

        _library = TrainerDataService.LoadLocal();
        TrainerDataService.CurrentLibrary = _library;
        _nextPage = Math.Max(1, _library.Pages) + 1;
        _favorites.AddRange(TrainerDataService.LoadFavorites(_library));

        CardList.ItemsSource = _rows;
        QueueList.ItemsSource = DownloadManager.Instance.Tasks;
        NavList.SelectedIndex = 0;

        DownloadManager.Instance.Changed += (_, _) => OnDownloadsChanged();

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
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

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        var workWidth = (info.Work.Right - info.Work.Left) / scale;
        var workHeight = (info.Work.Bottom - info.Work.Top) / scale;
        if (workWidth < 320 || workHeight < 240) return;

        var width = Math.Min(1440, Math.Max(900, workWidth - 32));
        var height = Math.Min(880, Math.Max(520, workHeight - 32));

        MaxWidth = workWidth;
        MaxHeight = workHeight;
        MinWidth = Math.Min(1200, width);
        MinHeight = Math.Min(720, height);
        Width = width;
        Height = height;
    }

    private const uint MonitorDefaultToNearest = 2;

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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowBounds();
    }

    /// <summary>窗口被拖到另一块缩放比例不同的显示器上时，重新按新显示器收一次尺寸。</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyWindowBounds();

        if (_detailOpen && DetailHost.Content is TrainerDetailView view)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => view.FitTo(RootGrid.ActualWidth - 40, RootGrid.ActualHeight - 40)));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateCardMetrics();
        Render();
        UpdateStats();
        UpdateDisk();
        UpdateQueueBadge();

        // 后台补齐缺封面的卡片（限流 4 并发），加载完自动替换字母水印
        _ = CacheCoversAsync(_library.Items);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCardMetrics();
        UpdateDiskBar();

        // 弹框开着时，窗口尺寸变化也要跟着缩放，避免超出屏幕
        if (_detailOpen && DetailHost.Content is TrainerDetailView view)
            view.FitTo(RootGrid.ActualWidth - 40, RootGrid.ActualHeight - 40);
    }

    // ==================== 卡片尺寸 ====================

    /// <summary>按可用宽度算出「一行 5 张」的卡片尺寸，高度按固定比例跟随，保证不变形。</summary>
    private void UpdateCardMetrics()
    {
        var avail = CardList.ActualWidth - CardList.Padding.Left - CardList.Padding.Right - 8;
        if (avail <= 0) avail = 920;

        var w = Math.Floor((avail - CardGap * (CardsPerRow - 1)) / CardsPerRow);
        w = Math.Clamp(w, 128, 260);
        var h = Math.Round(w * CardAspect);

        if (Math.Abs(w - _cardW) < 0.5 && Math.Abs(h - _cardH) < 0.5) return;

        _cardW = w;
        _cardH = h;
        Resources["CardW"] = w;
        Resources["CardH"] = h;
    }

    // ==================== 数据渲染 ====================

    private IEnumerable<TrainerItem> SourceItems => _mode == ViewMode.Favorites ? _favorites : _library.Items;

    private void Render()
    {
        switch (_mode)
        {
            case ViewMode.Library:
                SectionTitle.Text = "Steam 单机游戏库";
                SectionSubtitle.Text = string.IsNullOrEmpty(_library.FetchedAt)
                    ? "来源 flingtrainer.com"
                    : $"来源 flingtrainer.com · 数据抓取于 {_library.FetchedAt}";
                break;
            case ViewMode.Favorites:
                SectionTitle.Text = "收藏夹";
                SectionSubtitle.Text = "收藏保存在本地 SQLite 数据库，重装/重启都不会丢";
                break;
            case ViewMode.Queue:
                SectionTitle.Text = "下载队列";
                SectionSubtitle.Text = "文件直连官网下载 · 实时进度、速度与剩余时间";
                break;
            case ViewMode.Settings:
                SectionTitle.Text = "设置";
                SectionSubtitle.Text = "数据源与缓存";
                break;
        }

        // 收藏夹视图下，卡片左上角会多出一个「取消收藏」按钮
        CardList.Tag = _mode == ViewMode.Favorites;
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
            SectionCount.Text = "";

            EmptyGlyph.Text = "\uE713";
            EmptyTitle.Text = "数据与缓存";
            EmptyDesc.TextAlignment = TextAlignment.Left;
            EmptyDesc.MaxWidth = 680;
            EmptyDesc.Text =
                $"数据源：{TrainerWeb.Site}\n" +
                $"收藏库：{AppDatabase.DatabaseFile}\n" +
                $"数据库状态：{(AppDatabase.Instance.IsAvailable ? "已就绪" : "不可用 · " + AppDatabase.Instance.LastError)}\n" +
                $"下载目录：{DownloadManager.Instance.DownloadDir}\n" +
                $"浏览器 UA：{UserAgentProvider.UserAgent}\n" +
                $"高 DPI 模式：PerMonitorV2 · 当前缩放 {VisualTreeHelper.GetDpi(this).DpiScaleX * 100:0}%";

            UpdateFooter();
            return;
        }

        var keyword = SearchBox.Text.Trim();
        var items = SourceItems;

        if (!string.IsNullOrEmpty(keyword))
        {
            items = items.Where(i =>
                i.DisplayTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                i.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                i.Version.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                i.Groups.Any(g => g.Items.Any(o =>
                    o.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    o.NameCn.Contains(keyword, StringComparison.OrdinalIgnoreCase))));
        }

        SetItems(items);

        if (_items.Count == 0)
        {
            CardList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            EmptyDesc.TextAlignment = TextAlignment.Center;
            EmptyDesc.MaxWidth = 460;
            EmptyGlyph.Text = _mode == ViewMode.Favorites ? "\uE734" : "\uE721";
            EmptyTitle.Text = _mode == ViewMode.Favorites ? "收藏夹还是空的" : "没有找到匹配的游戏";
            EmptyDesc.Text = _mode == ViewMode.Favorites
                ? "打开任意游戏详情，点击「加入收藏夹」，收藏会写进本地 SQLite，下次打开就能在这里快速找到。"
                : $"没有与“{keyword}”相关的结果，试试游戏名、版本号或修改项名称。";
            SectionCount.Text = "";
        }
        else
        {
            CardList.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
            SectionCount.Text = _mode == ViewMode.Favorites
                ? $"共 {_items.Count} 款收藏 · {_items.Sum(i => i.RealOptionCount)} 个选项"
                : $"共 {_items.Count} 款 · {_items.Sum(i => i.RealOptionCount)} 个选项";
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

    /// <summary>只有「游戏库」列表且没有搜索词、不在收藏夹时才继续翻页。</summary>
    private bool CanPage =>
        _mode == ViewMode.Library && string.IsNullOrWhiteSpace(SearchBox.Text);

    private void UpdateFooter()
    {
        var show = CanPage && _items.Count > 0;
        CardsFooter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        FooterLoading.Visibility = _loadingMore ? Visibility.Visible : Visibility.Collapsed;
        FooterRetry.Visibility = show && _loadFailed && !_loadingMore ? Visibility.Visible : Visibility.Collapsed;
        FooterRetry.IsHitTestVisible = true;
        FooterEnd.Visibility = show && _noMore && !_loadingMore && !_loadFailed
            ? Visibility.Visible
            : Visibility.Collapsed;

        CardsFooter.IsHitTestVisible = FooterRetry.Visibility == Visibility.Visible;
        FooterEndText.Text = _noMore ? $"已经到底了 · 共 {_library.Items.Count} 款" : "继续滚动加载更多";
    }

    private void CardList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 只有用户真的滚动了（或视口变化）才判断，避免布局变化引起连环翻页
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0) return;
        if (!CanPage || _loadingMore || _noMore || _loadFailed) return;
        if (e.ExtentHeight <= e.ViewportHeight + 80) return;
        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 480) return;

        _ = LoadNextPageAsync();
    }

    private void FooterRetry_Click(object sender, RoutedEventArgs e)
    {
        _loadFailed = false;
        _ = LoadNextPageAsync();
    }

    private async Task LoadNextPageAsync()
    {
        if (_loadingMore || _noMore) return;

        _loadingMore = true;
        _loadFailed = false;
        UpdateFooter();
        StatusText.Text = $"正在加载第 {_nextPage} 页…";

        try
        {
            var (items, totalPages) = await TrainerWeb.FetchPageAsync(_nextPage);

            var known = _library.Items.Select(i => i.Id).ToHashSet();
            var added = items.Where(i => known.Add(i.Id)).ToList();

            var append = CanPage;
            foreach (var item in added)
                _library.Items.Add(item);

            if (append && added.Count > 0)
            {
                AppendItems(added);
                SectionCount.Text = $"共 {_items.Count} 款 · {_items.Sum(i => i.RealOptionCount)} 个选项";
                UpdateStats();
                StatusText.Text = $"已加载 {_library.Items.Count} 款游戏";
            }

            _library.Pages = _nextPage;
            _nextPage++;

            if (items.Count == 0 || _nextPage > totalPages) _noMore = true;

            TrainerDataService.SaveCache(_library);
            _ = CacheCoversAsync(added);
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

    /// <summary>把新一页的官网截图缓存到本地，卡片加载完自动替换封面。</summary>
    private static async Task CacheCoversAsync(IEnumerable<TrainerItem> items)
    {
        var pending = items.Where(i => !i.HasPoster && !string.IsNullOrWhiteSpace(i.ImageUrl)).ToList();
        await Task.WhenAll(pending.Select(TrainerDataService.EnsureCoverAsync));
    }

    private void UpdateStats()
    {
        StatGames.Text = _library.Items.Count.ToString();
        StatOptions.Text = _library.Items.Sum(i => i.RealOptionCount).ToString();
        StatLatest.Text = _library.Items
            .Select(i => i.UpdatedText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .OrderByDescending(t => t, StringComparer.Ordinal)
            .FirstOrDefault() ?? "—";

        SidebarSyncText.Text = string.IsNullOrEmpty(_library.FetchedAt)
            ? "本地内置数据"
            : $"同步于 {_library.FetchedAt}";

        SyncStatus.Text = string.IsNullOrEmpty(_library.FetchedAt)
            ? $"使用内置离线数据（{_library.Items.Count} 款游戏）。点击下方按钮从官网同步最新内容。"
            : $"已同步 {_library.FetchedAt}，共 {_library.Items.Count} 款游戏。";

        NavFavLabel.Text = _favorites.Count > 0 ? $"收藏夹 · {_favorites.Count}" : "收藏夹";

        StatusText.Text =
            $"已加载 {_library.Items.Count} 款修改器 · {_library.Items.Sum(i => i.RealOptionCount)} 个选项";
    }

    private void UpdateDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;

            double total = drive.TotalSize, free = drive.AvailableFreeSpace;
            double used = total - free;

            DiskName.Text = $"{drive.Name.TrimEnd('\\')} 系统盘";
            DiskFree.Text = $"可用 {FormatGb(free)}";
            DiskUsed.Text = $"已用 {FormatGb(used)} / 共 {FormatGb(total)}";
            UpdateDiskBar();
        }
        catch
        {
            DiskName.Text = "无法读取磁盘信息";
        }
    }

    private void UpdateDiskBar()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;

            if (DiskBar.Parent is not FrameworkElement track || track.ActualWidth <= 0) return;
            DiskBar.Width = Math.Max(3, track.ActualWidth *
                ((drive.TotalSize - drive.AvailableFreeSpace) / (double)drive.TotalSize));
        }
        catch
        {
            // 忽略
        }
    }

    private static string FormatGb(double bytes) => $"{bytes / 1024d / 1024 / 1024:0} GB";

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
        SectionCount.Text = DownloadManager.Instance.SummaryText;
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
        if (IsLoaded)
        {
            Render();
            ScrollToTop();
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

    private void OpenDetail(TrainerItem item)
    {
        var isFavorite = _favorites.Any(f => f.Id == item.Id);
        var view = new TrainerDetailView(item, isFavorite);
        view.CloseRequested += (_, _) => CloseDetail();
        view.FavoriteChanged += (_, _) => OnFavoriteChanged(item, view.IsFavorite);
        view.FitTo(RootGrid.ActualWidth - 40, RootGrid.ActualHeight - 40);

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

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _detailOpen)
        {
            CloseDetail();
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

        await Task.Delay(800);
        SaveShot(Path.Combine(dir, "01-main.png"));

        var item = _library.Items
            .OrderByDescending(i => i.RealOptionCount)
            .FirstOrDefault(i => i.Groups.Count > 0) ?? _library.Items.FirstOrDefault();
        if (item is null) return;

        NavList.SelectedIndex = 1;
        await Task.Delay(600);
        SaveShot(Path.Combine(dir, "02-favorites.png"));

        NavList.SelectedIndex = 0;
        await Task.Delay(400);

        OpenDetail(item);
        await Task.Delay(1000);
        SaveShot(Path.Combine(dir, "03-detail-options.png"));

        if (DetailHost.Content is TrainerDetailView view)
        {
            view.ShowDownloadTab();
            await Task.Delay(500);
            SaveShot(Path.Combine(dir, "04-detail-download.png"));

            view.DownloadFirst();
            await Task.Delay(1600);
            SaveShot(Path.Combine(dir, "05-detail-progress.png"));
        }

        CloseDetail();
        await Task.Delay(400);

        NavList.SelectedIndex = 2;
        await Task.Delay(900);
        SaveShot(Path.Combine(dir, "06-queue.png"));

        await Task.Delay(6000);
        SaveShot(Path.Combine(dir, "07-queue-done.png"));

        OpenDetail(item);
        await Task.Delay(900);
        if (DetailHost.Content is TrainerDetailView done)
        {
            done.ShowDownloadTab();
            await Task.Delay(500);
            SaveShot(Path.Combine(dir, "08-detail-done.png"));
        }

        CloseDetail();
        await Task.Delay(300);

        NavList.SelectedIndex = 3;
        await Task.Delay(600);
        SaveShot(Path.Combine(dir, "09-settings.png"));

        DownloadManager.Instance.ClearFinished();
        NavList.SelectedIndex = 2;
        await Task.Delay(600);
        SaveShot(Path.Combine(dir, "10-queue-empty.png"));

        // 模拟 200% 缩放的 1080p 屏：窗口只有 960x520 DIP，验证弹框能收进去
        MinWidth = 0;
        MinHeight = 0;
        Width = 960;
        Height = 520;
        await Task.Delay(500);
        NavList.SelectedIndex = 0;
        OpenDetail(item);
        await Task.Delay(900);
        SaveShot(Path.Combine(dir, "11-small-window-detail.png"));
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
