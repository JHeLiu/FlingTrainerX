using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FlingTrainerX.Models;
using FlingTrainerX.Services;
using FlingTrainerX.ViewModels;

namespace FlingTrainerX.Views;

/// <summary>官网详细页的弹框：介绍图 + 修改选项 / 下载文件 选项卡 + 内置下载进度。</summary>
public partial class TrainerDetailView : UserControl
{
    private readonly TrainerItem _item;
    private readonly ObservableCollection<DownloadRowVm> _rows = new();

    private bool _favorite;
    private bool _detailLoading;

    public event EventHandler? CloseRequested;
    public event EventHandler? FavoriteChanged;

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

    /// <summary>弹框按窗口可用空间自适应（高 DPI / 小屏都不会超出屏幕）。</summary>
    public void FitTo(double availableWidth, double availableHeight)
    {
        var w = Math.Min(1000, Math.Max(560, availableWidth));
        var h = Math.Min(664, Math.Max(420, availableHeight));

        if (Math.Abs(Panel.Width - w) > 0.5) Panel.Width = w;
        if (Math.Abs(Panel.Height - h) > 0.5) Panel.Height = h;
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

        if (!_item.HasDownloads)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => _ = LoadDownloadsAsync(false)));
        }
    }

    /// <summary>封面与介绍图在动画结束后再解码上屏，避免和开场动画抢帧。</summary>
    public async Task LoadImagesAsync()
    {
        try
        {
            // 介绍图先上（内置数据是本地文件，几乎无延迟），横幅再补
            if (_item.HasIntroImage)
            {
                var intro = await TrainerDataService.LoadImageAsync(_item.IntroImage, 1400);
                if (intro is not null)
                {
                    IntroBrush.ImageSource = intro;
                    IntroHost.Visibility = Visibility.Visible;
                }
            }

            var banner = await TrainerDataService.LoadImageAsync(_item.BannerSource, 1200);
            if (banner is not null) BannerBrush.ImageSource = banner;
        }
        catch
        {
            // 图片加载失败不影响文字内容
        }
    }

    // ==================== 选项卡 ====================

    private void SelectTab(int index)
    {
        OptionsPane.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadPane.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;

        var activeBg = (Brush)FindResource("B.AccentDim");
        var activeText = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xFB));
        var idleText = new SolidColorBrush(Color.FromRgb(0x8C, 0xA2, 0xB9));

        OptionsTab.Background = index == 0 ? activeBg : Brushes.Transparent;
        DownloadTab.Background = index == 1 ? activeBg : Brushes.Transparent;
        OptionsTabText.Foreground = index == 0 ? activeText : idleText;
        DownloadTabText.Foreground = index == 1 ? activeText : idleText;

        TabHint.Text = index == 0 ? "与官网修改项列表一致" : "点「下载」直接存入本机";
    }

    private void OptionsTab_Click(object sender, RoutedEventArgs e) => SelectTab(0);

    private void DownloadTab_Click(object sender, RoutedEventArgs e)
    {
        SelectTab(1);
        if (!_item.HasDownloads && !_detailLoading) _ = LoadDownloadsAsync(false);
    }

    // ==================== 选项 ====================

    private void RebuildOptions()
    {
        var filter = FilterBox.Text.Trim();
        FilterHint.Visibility = string.IsNullOrEmpty(FilterBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        var groups = _item.Groups
            .Select(g => new TrainerGroup
            {
                Title = g.Title,
                Items = string.IsNullOrEmpty(filter)
                    ? g.Items
                    : g.Items.Where(o =>
                        o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        o.NameCn.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        o.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList()
            })
            .Where(g => g.Items.Count > 0)
            .ToList();

        OptionsList.ItemsSource = groups;
        CountText.Text = groups.Sum(g => g.Items.Count).ToString();
        GroupSummary.Text = $"· {groups.Count} 组";
        OptionsLoading.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCounts()
    {
        var options = _item.RealOptionCount;
        var files = _item.Downloads.Count;

        OptionsTabCount.Text = options.ToString();
        DownloadTabCount.Text = files.ToString();
        CountText.Text = options.ToString();
        DownloadCountText.Text = files == 0 ? "—" : $"{files} 个";
        LatestFileText.Text = _item.LatestDownloadDate;
    }

    // ==================== 下载 ====================

    private void RebuildDownloadRows()
    {
        _rows.Clear();
        foreach (var file in _item.Downloads)
            _rows.Add(new DownloadRowVm(file));
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
                if (IntroBrush.ImageSource is null) _ = LoadImagesAsync();
            }

            UpdateCounts();

            if (TrainerDataService.CurrentLibrary is { } lib)
                TrainerDataService.SaveCache(lib);

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

        var task = row.Task;
        if (task is null)
        {
            StartDownload(row);
        }
        else if (task.IsActive)
        {
            DownloadManager.Instance.Cancel(task);
        }
        else if (task.CanRetry)
        {
            DownloadManager.Instance.Retry(task);
        }
        else
        {
            DownloadManager.Instance.OpenFolder(task);
        }
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
    /// <summary>自检用：切到下载选项卡。</summary>
    public void ShowDownloadTab() => SelectTab(1);

    /// <summary>自检用：下载第一条文件。</summary>
    public void DownloadFirst() => Download_Click(this, new RoutedEventArgs());
#endif

    /// <summary>右侧「下载最新文件」：下第一条（官网按时间倒序）。</summary>
    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_item.Downloads.Count == 0)
        {
            ShowToast("还没有读到下载文件，正在从官网获取…");
            _ = LoadDownloadsAsync(false);
            SelectTab(1);
            return;
        }

        SelectTab(1);
        var row = _rows.FirstOrDefault();
        if (row is not null) StartDownload(row);
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
