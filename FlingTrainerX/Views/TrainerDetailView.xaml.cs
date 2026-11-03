using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FlingTrainerX.Models;
using FlingTrainerX.Services;
using FlingTrainerX.ViewModels;

namespace FlingTrainerX.Views;

/// <summary>官网详细页的弹框：介绍图 + 修改选项 / 下载文件 选项卡 + 内置下载进度。</summary>
public partial class TrainerDetailView : System.Windows.Controls.UserControl
{
    private readonly TrainerItem _item;
    private readonly ObservableCollection<DownloadRowVm> _rows = new();

    /// <summary>下载表里选中的那一行（鼠标划过的行即为选中行），右侧「启动程序」启动它。</summary>
    private DownloadRowVm? _selectedRow;

    private bool _favorite;
    private bool _detailLoading;
    private bool _hooked;
    private bool _shotsLoaded;

    public event EventHandler? CloseRequested;
    public event EventHandler? FavoriteChanged;

    /// <summary>修改器已经成功启动（本程序该让位了：收到后缩到右下角托盘）。</summary>
    public event EventHandler? Launched;

    public TrainerDetailView(TrainerItem item, bool isFavorite)
    {
        InitializeComponent();
        _item = item;
        _favorite = isFavorite;
        DataContext = item;

        UpdateFavoriteButton();
        UpdateCounts();

        DownloadsList.ItemsSource = _rows;
        RebuildDownloadRows();
        SelectTab(0);

        Loaded += OnLoaded;
    }

    /// <summary>
    /// 弹框尺寸按窗口的百分比算（宽 84%、高 86%），窗口放大缩小它跟着变，
    /// 不再是固定 1000×664；同时保一个下限，窗口很小时也不至于挤成一团。
    /// </summary>
    public void FitTo(double hostWidth, double hostHeight)
    {
        const double widthRatio = 0.84;
        const double heightRatio = 0.86;
        const double minW = 700;
        const double minH = 500;

        // 先留 24px 边距再保下限；窗口比下限还小时以窗口为准（宁可缩也不溢出屏幕）
        var maxW = Math.Max(320, hostWidth - 24);
        var maxH = Math.Max(280, hostHeight - 24);

        var w = Math.Clamp(hostWidth * widthRatio, Math.Min(minW, maxW), maxW);
        var h = Math.Clamp(hostHeight * heightRatio, Math.Min(minH, maxH), maxH);

        w = Math.Round(w);
        h = Math.Round(h);

        if (Math.Abs(Panel.Width - w) > 0.5) Panel.Width = w;
        if (Math.Abs(Panel.Height - h) > 0.5) Panel.Height = h;

        // 横幅高度跟着宽度走，但压矮一些，把竖向空间多留给下面的选项 / 下载表；
        // 同时不超过弹框高度的 34%，小窗口下不会把内容区挤没
        var maxBanner = Math.Max(120, h * 0.34);
        var banner = Math.Round(Math.Clamp(w * 0.24, 128, Math.Min(240, maxBanner)));
        if (Math.Abs(BannerRow.Height.Value - banner) > 0.5)
            BannerRow.Height = new GridLength(banner);
    }

    /// <summary>
    /// 选项列表在弹框动画结束后再构建：33~46 行的模板 + 文本排版约 80ms，
    /// 放在开场动画之前会明显卡一下。
    /// </summary>
    public void LoadOptions()
    {
        if (OptionsList.ItemsSource is not null) return;

        OptionsLoading.Visibility = Visibility.Collapsed;
        RebuildOptions();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FilterBox.TextChanged += (_, _) => RebuildOptions();
        FilterHint.Visibility = string.IsNullOrEmpty(FilterBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        RefreshLaunchState();
        UpdateBannerClip();
        BannerShell.SizeChanged += (_, _) => UpdateBannerClip();

        if (!_hooked)
        {
            _hooked = true;
            DownloadManager.Instance.Changed += OnDownloadsChanged;
            Unloaded += (_, _) => DownloadManager.Instance.Changed -= OnDownloadsChanged;
        }

        if (!_item.HasDownloads)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => _ = LoadDownloadsAsync(false)));
        }
    }

    /// <summary>
    /// 横幅只圆上面两个角，并且是真裁剪：模糊底衬（BlurEffect）的半透明像素会糊到
    /// 圆弧外面，把方角填成灰块 —— 只靠各层 Border 的 CornerRadius 挡不住，
    /// 所以按实际尺寸生成「上圆下方」的几何做 Clip。
    /// </summary>
    private void UpdateBannerClip()
    {
        var w = BannerShell.ActualWidth;
        var h = BannerShell.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 面板有 1px 描边，横幅在里面，半径比面板的 16 小 1
        var r = Math.Min(15, Math.Min(w, h) / 2);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, r), true, true);
            ctx.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(w - r, 0), true, false);
            ctx.ArcTo(new Point(w, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(w, h), true, false);
            ctx.LineTo(new Point(0, h), true, false);
        }

        geo.Freeze();
        BannerShell.Clip = geo;
    }

    /// <summary>横幅（本地有封面文件就直接读盘，没有才走网络），不阻塞弹框。</summary>
    public Task LoadImagesAsync()
    {
        if (NetImages.Get(_item.BannerSource) is { } banner)
        {
            BannerBrush.ImageSource = banner;
            BannerBackBrush.ImageSource = banner;
        }

        RefreshLaunchState();
        return Task.CompletedTask;
    }

    /// <summary>下载队列有变化（下完了 / 失败了）就重新判断「启动」能不能点。</summary>
    private void OnDownloadsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshLaunchState));

    // ==================== 选项卡 ====================

    private void SelectTab(int index)
    {
        ShotsPane.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        OptionsPane.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        DownloadPane.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        var activeBg = (Brush)FindResource("B.AccentDim");
        var activeText = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xFB));
        var idleText = new SolidColorBrush(Color.FromRgb(0x8C, 0xA2, 0xB9));

        ShotsTab.Background = index == 0 ? activeBg : Brushes.Transparent;
        OptionsTab.Background = index == 1 ? activeBg : Brushes.Transparent;
        DownloadTab.Background = index == 2 ? activeBg : Brushes.Transparent;
        ShotsTabText.Foreground = index == 0 ? activeText : idleText;
        OptionsTabText.Foreground = index == 1 ? activeText : idleText;
        DownloadTabText.Foreground = index == 2 ? activeText : idleText;

        if (index == 0) LoadScreenshots();
    }

    private void OptionsTab_Click(object sender, RoutedEventArgs e) => SelectTab(1);

    private void DownloadTab_Click(object sender, RoutedEventArgs e)
    {
        SelectTab(2);
        if (!_item.HasDownloads && !_detailLoading) _ = LoadDownloadsAsync(false);
    }

    private void ShotsTab_Click(object sender, RoutedEventArgs e) => SelectTab(0);

    // ==================== 软件截图 ====================

    /// <summary>第一次切到这个选项卡时把图挂上去，同时把官方的介绍图存到 f\&lt;id&gt;\。</summary>
    private void LoadScreenshots()
    {
        if (_shotsLoaded) return;

        _shotsLoaded = true;
        RebuildShots();

        // 落盘放到弹框开场动画之后再做，别和动画抢 UI 线程
        Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => _ = CacheScreenshotsAsync()));
    }

    private void RebuildShots()
    {
        // 选项卡没打开过就不用建列表，等切过去再算
        if (!_shotsLoaded) return;

        if (_item.Images.Count == 0)
        {
            ShotsList.ItemsSource = null;
            ShotsEmpty.Text = "官网这篇文章没有配图";
            ShotsEmpty.Visibility = Visibility.Visible;
            ShotsCountText.Text = "0";
            return;
        }

        var shots = new List<ShotVm>(_item.Images.Count);
        for (var i = 0; i < _item.Images.Count; i++)
            shots.Add(new ShotVm(i, _item.Images[i], TrainerFiles.ShotSource(_item, i)));

        ShotsList.ItemsSource = shots;
        ShotsEmpty.Visibility = Visibility.Collapsed;
        ShotsCountText.Text = shots.Count.ToString();
    }

    /// <summary>截图落盘后重绑一次，之后即使断网也能看图。</summary>
    private async Task CacheScreenshotsAsync()
    {
        var added = await TrainerFiles.CacheScreenshotsAsync(_item);
        if (added == 0) return;

        TrainerDataService.SaveCache(_item);
        RebuildShots();
    }

    private void Shot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ShotVm shot }) return;

        // 本地有就打开本地文件，否则退回官网原图
        if (!string.IsNullOrWhiteSpace(shot.Source) && File.Exists(shot.Source))
        {
            if (TrainerFiles.Launch(shot.Source)) return;
        }

        OpenUrl(shot.Url);
    }

    // ==================== 选项 ====================

    private void RebuildOptions()
    {
        var filter = FilterBox.Text.Trim();
        FilterHint.Visibility = string.IsNullOrEmpty(FilterBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        // 选项现在是一张平铺的列表：不显示分组标题，也不显示「N 项」这类说明
        var all = _item.Groups
            .SelectMany(g => g.Items)
            .Where(o => !string.IsNullOrWhiteSpace(o.Key) && !string.IsNullOrWhiteSpace(o.Name))
            .Where(o => !LooksLikeNote(o))
            .ToList();

        var list = string.IsNullOrEmpty(filter)
            ? all
            : all.Where(o =>
                o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                o.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        OptionsList.ItemsSource = list;
        CountText.Text = list.Count.ToString();
        OptionsLoading.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 官网正文里夹着的说明段落（比如「本作使用 EasyAntiCheat，请这样启动…」）会被解析成一条假选项，
    /// 特征是一整句话、带引号且很长，这里把它们滤掉，列表末尾就不会多出一段描述文字。
    /// </summary>
    private static bool LooksLikeNote(TrainerOption option)
    {
        var name = option.Name.Trim();
        if (name.Length > 70) return true;
        return name.Length > 40 &&
               (name.EndsWith('.') || name.Contains('\u201C') || name.Contains('\u201D'));
    }

    private void UpdateCounts()
    {
        var options = _item.RealOptionCount;
        var files = _item.Downloads.Count;

        OptionsTabCount.Text = options.ToString();
        DownloadTabCount.Text = files.ToString();
        ShotsTabCount.Text = _item.Images.Count.ToString();
        CountText.Text = options.ToString();

        RefreshLaunchState();
    }

    /// <summary>「启动程序」跟着表格里的选中行走：选中的那行下好了才能点。</summary>
    private void RefreshLaunchState()
    {
        string? path = null;

        if (_selectedRow is not null)
            path = _selectedRow.HasLocalFile ? _selectedRow.LocalPath : null;
        else if (TrainerFiles.LocalFile(_item) is { } file)
            path = file.FullPath;

        LaunchButton.IsEnabled = path is not null && File.Exists(path);
        LaunchButton.ToolTip = path is not null
            ? $"启动 {Path.GetFileName(path)}"
            : _selectedRow is not null
                ? "选中的这一行还没下载，先点行尾的「下载」"
                : "下载完成后可以直接在这里启动修改器";
    }

    // ==================== 下载 ====================

    private void RebuildDownloadRows()
    {
        _rows.Clear();
        foreach (var file in _item.Downloads)
            _rows.Add(new DownloadRowVm(file));

        // 默认选中已经下好的那一行，没有就选第一行（官网按时间倒序，第一行就是最新文件）
        SelectRow(_rows.FirstOrDefault(r => r.HasLocalFile) ?? _rows.FirstOrDefault());
    }

    // ==================== 行选中 ====================

    private void SelectRow(DownloadRowVm? row)
    {
        if (row is null) return;

        foreach (var r in _rows) r.IsSelected = ReferenceEquals(r, row);
        _selectedRow = row;
        RefreshLaunchState();
    }

    /// <summary>鼠标移到某一行就等于选中它（移出表格后保持，方便去点右侧的「启动程序」）。</summary>
    private void Row_Enter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DownloadRowVm row }) SelectRow(row);
    }

    /// <summary>点一行同样会选中它。</summary>
    private void Row_Select(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DownloadRowVm row }) SelectRow(row);
    }

    private async Task LoadDownloadsAsync(bool force)
    {
        if (_detailLoading) return;
        if (!force && _item.HasDownloads) return;

        _detailLoading = true;
        ReloadDownloadsButton.IsEnabled = false;

        if (!_item.HasDownloads)
        {
            DownloadEmpty.Text = "正在从官网读取下载信息…";
            DownloadEmpty.Visibility = Visibility.Visible;
        }

        try
        {
            var (downloads, images) = await TrainerWeb.FetchDetailAsync(_item.Url);

            if (downloads.Count > 0)
            {
                // 新抓到的表格里没有本地路径，按 URL 把已经下好的文件补回去
                TrainerDataService.MergeLocalDownloads(_item, downloads);
                _item.Downloads = downloads;
                RebuildDownloadRows();
                DownloadEmpty.Visibility = Visibility.Collapsed;
            }
            else if (!_item.HasDownloads)
            {
                DownloadEmpty.Text = "官网暂无下载文件";
                DownloadEmpty.Visibility = Visibility.Visible;
            }

            if (_item.Images.Count == 0 && images.Count > 0)
            {
                _item.Images = images;
                RebuildShots();
            }

            UpdateCounts();

            // 下载表也是缓存的一部分，抓完就落库
            TrainerDataService.SaveCache(_item);

            if (force) ShowToast($"已更新下载列表 · {downloads.Count} 个文件");
        }
        catch
        {
            if (!_item.HasDownloads)
            {
                DownloadEmpty.Text = "读取失败，点击右上角刷新重试";
                DownloadEmpty.Visibility = Visibility.Visible;
            }
            else if (force)
            {
                ShowToast("读取失败，请检查网络");
            }
        }
        finally
        {
            _detailLoading = false;
            ReloadDownloadsButton.IsEnabled = true;
        }
    }

    private async void ReloadDownloads_Click(object sender, RoutedEventArgs e)
        => await LoadDownloadsAsync(true);

    /// <summary>行右侧按钮：没任务就下载，下载中取消，失败重试，完成打开所在文件夹。</summary>
    private void RowAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DownloadRowVm row }) return;

        SelectRow(row);

        var task = row.Task;
        if (task is null)
        {
            // 重启后队列是空的，但本地已经有文件：直接启动，不再重复下载
            if (row.HasLocalFile) LaunchLocal(row.LocalPath);
            else StartDownload(row);
        }
        else if (task.IsActive)
        {
            DownloadManager.Instance.Cancel(task);
        }
        else if (task.CanRetry)
        {
            DownloadManager.Instance.Retry(task);
        }
        else if (task.CanLaunch)
        {
            // 下载完成：这个按钮直接变成「启动」
            LaunchLocal(task.SavePath);
        }
        else
        {
            DownloadManager.Instance.OpenFolder(task);
        }
    }

    /// <summary>行尾的「定位」小按钮：在资源管理器里选中这个文件。</summary>
    private void RowOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DownloadRowVm row }) return;

        if (row.Task is { } task && task.SavePath.Length > 0) TrainerFiles.ShowInFolder(task.SavePath);
        else if (row.HasLocalFile) TrainerFiles.ShowInFolder(row.LocalPath);
        else DownloadManager.Instance.OpenFolder(null);
    }

    // ==================== 启动 ====================

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        // 优先启动表格里选中的那一行，没选中才退回本地任意一个已下载文件
        var path = _selectedRow is not null && _selectedRow.HasLocalFile
            ? _selectedRow.LocalPath
            : TrainerFiles.LocalFile(_item)?.FullPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            ShowToast("选中的这一行还没下载好，先点行尾的「下载」");
            return;
        }

        // 存的是相对程序目录的路径，启动前已经拼成绝对路径
        LaunchLocal(path);
    }

    private void LaunchLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ShowToast("文件不存在，可能已被删除");
            RefreshLaunchState();
            return;
        }

        // 压缩包不能直接运行，帮用户定位到文件，免得点了没反应
        if (TrainerFiles.IsArchive(path))
        {
            TrainerFiles.ShowInFolder(path);
            ShowToast("这是压缩包，解压后再运行；已在资源管理器里定位到它");
            return;
        }

        if (!TrainerFiles.Launch(path))
        {
            ShowToast("启动失败，可点「定位」手动运行");
            return;
        }

        ShowToast($"已启动 {Path.GetFileName(path)}");

        // 修改器弹到前台了，本程序自动缩进托盘，别挡着游戏
        Launched?.Invoke(this, EventArgs.Empty);
    }

    private DownloadTask StartDownload(DownloadRowVm row)
    {
        var file = _item.Downloads.FirstOrDefault(d => d.Url == row.Url);
        if (file is null) return DownloadManager.Instance.Enqueue(_item, new TrainerDownload { Url = row.Url, File = row.FileName });

        var task = DownloadManager.Instance.Enqueue(_item, file);
        row.Attach(task);

        if (task.State is DownloadState.Completed)
            ShowToast("该文件已经下载完成，点「打开」可定位到文件");
        else
            ShowToast($"已加入下载队列 · {DownloadManager.Instance.Tasks.Count} 个任务");

        return task;
    }

#if DEBUG
    /// <summary>自检用：切到文件下载选项卡。</summary>
    public void ShowDownloadTab() => SelectTab(2);

    /// <summary>自检用：切到选项功能选项卡。</summary>
    public void ShowOptionsTab() => SelectTab(1);

    /// <summary>自检用：切到软件截图选项卡。</summary>
    public void ShowShotsTab() => SelectTab(0);

    /// <summary>自检用：下载第一条文件。</summary>
    public void DownloadFirst() => Download_Click(this, new RoutedEventArgs());

    /// <summary>自检用：走一遍「启动程序」的完整流程（含启动成功后的收进托盘）。</summary>
    public void LaunchFile(string path) => LaunchLocal(path);
#endif

    /// <summary>右侧「下载最新文件」：下第一条（官网按时间倒序）。</summary>
    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_item.Downloads.Count == 0)
        {
            ShowToast("还没有读到下载文件，正在从官网获取…");
            _ = LoadDownloadsAsync(false);
            SelectTab(2);
            return;
        }

        SelectTab(2);
        var row = _rows.FirstOrDefault();
        if (row is null) return;

        SelectRow(row);

        // 第一个文件已经下过就不再重复下一份，直接启动
        if (row.HasLocalFile && row.Task is null) LaunchLocal(row.LocalPath);
        else StartDownload(row);
    }

    private void CopyAllDownloads_Click(object sender, RoutedEventArgs e)
    {
        var urls = _item.Downloads.Select(d => d.Url).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (urls.Count == 0)
        {
            ShowToast("暂无下载地址");
            return;
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, urls));
            ShowToast($"已复制 {urls.Count} 个下载地址");
        }
        catch
        {
            ShowToast("复制失败");
        }
    }

    // ==================== 其它操作 ====================

    private void UpdateFavoriteButton()
    {
        FavButton.Content = _favorite ? "取消收藏" : "加入收藏夹";
        FavButton.Foreground = _favorite
            ? (Brush)FindResource("B.AccentLight")
            : (Brush)FindResource("B.TextDim");
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        _favorite = !_favorite;
        UpdateFavoriteButton();
        FavoriteChanged?.Invoke(this, EventArgs.Empty);
        ShowToast(_favorite ? "已加入收藏夹" : "已从收藏夹移除");
    }

    private void OpenSite_Click(object sender, RoutedEventArgs e) => OpenUrl(_item.Url);

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_item.Url);
            ShowToast("链接已复制到剪贴板");
        }
        catch
        {
            ShowToast("复制失败");
        }
    }

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            ShowToast("无法打开链接");
        }
    }

    private async void ShowToast(string message)
    {
        ToastText.Text = message;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140));
        ToastText.BeginAnimation(OpacityProperty, fadeIn);
        await Task.Delay(1600);
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(360));
        ToastText.BeginAnimation(OpacityProperty, fadeOut);
    }

    public bool IsFavorite => _favorite;
}
