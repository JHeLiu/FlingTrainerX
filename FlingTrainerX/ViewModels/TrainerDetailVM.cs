using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using FlingTrainerX.Models;
using FlingTrainerX.Services;

namespace FlingTrainerX.ViewModels;

/// <summary>
/// 官网详情弹框（Views/TrainerDetailView.xaml）的视图模型：
/// 三个选项卡、选项筛选、下载表、截图列表、收藏与启动状态，全部状态都在这里。
/// <para>
/// 弹框自己只剩「排练版」那点事：尺寸计算、横幅圆角裁剪、开场关闭动画、
/// 转圈和提示文字的淡入淡出 —— 这些必须碰控件，留在 code-behind 里。
/// VM 不引用任何控件：要弹提示 / 收遮层就抛事件，由弹框去演。
/// </para>
/// </summary>
public sealed partial class TrainerDetailVM : ObservableObject
{
    private readonly TrainerItem _item;

    /// <summary>下载表正在从官网读（避免重复请求）。</summary>
    private bool _downloadsLoading;

    /// <summary>截图列表是否已经建过（第一次切到那个页签才建）。</summary>
    private bool _shotsLoaded;

    /// <summary>选项列表是否已经建过（弹框开场动画结束后才建一次）。</summary>
    private bool _optionsLoaded;

    /// <summary>下载表里选中的那一行，右侧「启动程序」启动的就是它。</summary>
    private TrainerDetailRowVM? _selectedRow;

    public TrainerDetailVM(TrainerItem item, bool isFavorite)
    {
        _item = item;
        _isFavorite = isFavorite;

        RebuildDownloadRows();
        UpdateCounts();

        // 默认停在「软件截图」页签
        SelectedTab = 0;
        LoadScreenshots();
    }

    // ==================== 事件（由弹框订阅后去演） ====================

    /// <summary>要关弹框。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>收藏状态变了（外面要写库 + 刷新收藏夹）。</summary>
    public event EventHandler? FavoriteChanged;

    /// <summary>修改器已经启动成功（主窗口该缩进托盘了）。</summary>
    public event EventHandler? Launched;

    /// <summary>要在弹框右下角弹一句提示。</summary>
    public event EventHandler<string>? ToastRequested;

    /// <summary>加载遮层开关（弹框负责转圈动画）。</summary>
    public event EventHandler<bool>? LoadingOverlayChanged;

    // ==================== 数据 ====================

    /// <summary>这个弹框对应的游戏（标题、横幅、官网链接这些仍然直接绑它）。</summary>
    public TrainerItem Item => _item;

    /// <summary>下载表的数据源。</summary>
    public ObservableCollection<TrainerDetailRowVM> Rows { get; } = new();

    /// <summary>筛选后的修改选项。</summary>
    public ObservableCollection<TrainerOption> Options { get; } = new();

    /// <summary>官网介绍图。</summary>
    public ObservableCollection<TrainerDetailShotVM> Shots { get; } = new();

    // ==================== 选项卡 ====================

    /// <summary>当前页签：0 软件截图 / 1 选项功能 / 2 文件下载。</summary>
    [ObservableProperty]
    private int _selectedTab;

    public bool ShotsPaneVisible => SelectedTab == 0;

    public bool OptionsPaneVisible => SelectedTab == 1;

    public bool DownloadPaneVisible => SelectedTab == 2;

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(ShotsPaneVisible));
        OnPropertyChanged(nameof(OptionsPaneVisible));
        OnPropertyChanged(nameof(DownloadPaneVisible));

        if (value == 0) LoadScreenshots();
    }

    /// <summary>页签上的数量角标。</summary>
    [ObservableProperty]
    private string _shotsCount = "0";

    [ObservableProperty]
    private string _optionsCount = "0";

    [ObservableProperty]
    private string _downloadsCount = "0";

    // ==================== 选项面板 ====================

    /// <summary>筛选框里的字（和输入框双向绑定）。</summary>
    [ObservableProperty]
    private string _filterText = "";

    /// <summary>筛选框空的时候显示占位提示。</summary>
    [ObservableProperty]
    private bool _filterHintVisible = true;

    /// <summary>「正在加载修改选项…」那句。</summary>
    [ObservableProperty]
    private bool _optionsLoadingVisible = true;

    /// <summary>筛完一条都不剩时显示「没有匹配的选项」。</summary>
    [ObservableProperty]
    private bool _optionsEmptyVisible;

    partial void OnFilterTextChanged(string value)
    {
        FilterHintVisible = string.IsNullOrEmpty(value);
        RebuildOptions();
    }

    // ==================== 下载面板 ====================

    /// <summary>下载表为空时的说明文字。</summary>
    [ObservableProperty]
    private string _downloadEmptyText = "官网暂无下载文件";

    /// <summary>空说明是否显示。</summary>
    [ObservableProperty]
    private bool _downloadEmptyVisible;

    /// <summary>「重新读取下载信息」那颗按钮能不能点。</summary>
    [ObservableProperty]
    private bool _reloadEnabled = true;

    /// <summary>「启动程序」能不能点。</summary>
    [ObservableProperty]
    private bool _launchEnabled;

    /// <summary>「启动程序」的悬停提示。</summary>
    [ObservableProperty]
    private string _launchToolTip = "下载完成后可以直接在这里启动修改器";

    // ==================== 截图面板 ====================

    /// <summary>没有配图时的说明。</summary>
    [ObservableProperty]
    private string _shotsEmptyText = "官网这篇文章没有配图";

    [ObservableProperty]
    private bool _shotsEmptyVisible;

    // ==================== 收藏 / 提示 ====================

    /// <summary>是否已收藏。</summary>
    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>收藏按钮上的字。</summary>
    public string FavoriteText => IsFavorite ? "取消收藏" : "加入收藏夹";

    partial void OnIsFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteText));

    /// <summary>右下角那句提示的文字。</summary>
    [ObservableProperty]
    private string _toastText = "";

    /// <summary>加载遮层是否盖着。</summary>
    [ObservableProperty]
    private bool _loadingOverlayVisible;

    partial void OnLoadingOverlayVisibleChanged(bool value) =>
        LoadingOverlayChanged?.Invoke(this, value);

    // ==================== 命令 ====================

    /// <summary>切到「软件截图」。</summary>
    [RelayCommand]
    private void ShowShots() => SelectedTab = 0;

    /// <summary>切到「选项功能」。</summary>
    [RelayCommand]
    private void ShowOptions() => SelectedTab = 1;

    /// <summary>切到「文件下载」，本地没数据就顺手抓一次。</summary>
    [RelayCommand]
    private void ShowDownloads()
    {
        SelectedTab = 2;
        if (!_item.HasDownloads && !_downloadsLoading) _ = LoadDownloadsAsync(false);
    }

    /// <summary>加入 / 取消收藏。</summary>
    [RelayCommand]
    private void ToggleFavorite()
    {
        IsFavorite = !IsFavorite;
        FavoriteChanged?.Invoke(this, EventArgs.Empty);
        ShowToast(IsFavorite ? "已加入收藏夹" : "已从收藏夹移除");
    }

    /// <summary>右上角关闭按钮。</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>右侧「下载最新文件」：下第一条（官网按时间倒序）。</summary>
    [RelayCommand]
    private void DownloadLatest()
    {
        if (_item.Downloads.Count == 0)
        {
            ShowToast("还没有读到下载文件，正在从官网获取…");
            _ = LoadDownloadsAsync(false);
            SelectedTab = 2;
            return;
        }

        SelectedTab = 2;
        var row = Rows.FirstOrDefault();
        if (row is null) return;

        SelectRow(row);

        // 第一个文件已经下过就不再重复下一份，直接启动
        if (row.HasLocalFile && row.Task is null) LaunchLocal(row.LocalPath);
        else StartDownload(row);
    }

    /// <summary>右边「启动程序」。</summary>
    [RelayCommand]
    private void Launch()
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

        LaunchLocal(path);
    }

    /// <summary>打开官网详情页。</summary>
    [RelayCommand]
    private void OpenSite() => OpenUrl(_item.Url);

    /// <summary>复制页面链接。</summary>
    [RelayCommand]
    private void CopyLink()
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

    /// <summary>复制全部下载地址。</summary>
    [RelayCommand]
    private void CopyAllDownloads()
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

    /// <summary>重新从官网读下载表。</summary>
    [RelayCommand]
    private async Task ReloadDownloadsAsync() => await LoadDownloadsAsync(true);

    /// <summary>点某一行截图：本地有就开本地文件，没有就开官网原图。</summary>
    /// <param name="shot">被点的那张图。</param>
    [RelayCommand]
    private void OpenShot(TrainerDetailShotVM? shot)
    {
        if (shot is null) return;

        if (!string.IsNullOrWhiteSpace(shot.Source) && File.Exists(shot.Source))
        {
            if (TrainerFiles.Launch(shot.Source)) return;
        }

        OpenUrl(shot.Url);
    }

    /// <summary>下载行行尾那颗按钮：没任务就下载，下载中取消，失败重试，完成打开文件夹。</summary>
    /// <param name="row">被点的那一行。</param>
    [RelayCommand]
    private void RowAction(TrainerDetailRowVM? row)
    {
        if (row is null) return;

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

    /// <summary>行尾「定位」：在资源管理器里选中这个文件。</summary>
    /// <param name="row">被点的那一行。</param>
    [RelayCommand]
    private void RowOpen(TrainerDetailRowVM? row)
    {
        if (row is null) return;

        if (row.Task is { } task && task.SavePath.Length > 0) TrainerFiles.ShowInFolder(task.SavePath);
        else if (row.HasLocalFile) TrainerFiles.ShowInFolder(row.LocalPath);
        else DownloadManager.Instance.OpenFolder(null);
    }

    // ==================== 对外方法（弹框 / 主窗口会调） ====================

    /// <summary>选中下载表里的一行（鼠标划过的行即为选中行）。</summary>
    /// <param name="row">要选中的行；null 不动。</param>
    public void SelectRow(TrainerDetailRowVM? row)
    {
        if (row is null) return;

        foreach (var r in Rows) r.IsSelected = ReferenceEquals(r, row);
        _selectedRow = row;
        RefreshLaunchState();
    }

    /// <summary>
    /// 选项列表在弹框动画结束后再构建：33~46 行的模板 + 文本排版约 80ms，
    /// 放在开场动画之前会明显卡一下。
    /// </summary>
    public void LoadOptions()
    {
        if (_optionsLoaded) return;

        _optionsLoaded = true;
        OptionsLoadingVisible = false;
        RebuildOptions();
    }

    /// <summary>下载队列有变化（下完了 / 失败了）就重新判断「启动」能不能点。</summary>
    public void RefreshLaunchState()
    {
        string? path = null;

        if (_selectedRow is not null)
            path = _selectedRow.HasLocalFile ? _selectedRow.LocalPath : null;
        else if (TrainerFiles.LocalFile(_item) is { } file)
            path = file.FullPath;

        LaunchEnabled = path is not null && File.Exists(path);
        LaunchToolTip = path is not null
            ? $"启动 {Path.GetFileName(path)}"
            : _selectedRow is not null
                ? "选中的这一行还没下载，先点行尾的「下载」"
                : "下载完成后可以直接在这里启动修改器";
    }

    /// <summary>本地没有下载表时补抓一次（弹框 Loaded 之后调，别和开场动画抢线程）。</summary>
    /// <param name="force">true = 用户点了刷新，忽略本地缓存。</param>
    public Task EnsureDownloadsAsync(bool force = false) => LoadDownloadsAsync(force);

    /// <summary>读到新的下载表 / 图片之后，刷新页签上的数量。</summary>
    public void UpdateCounts()
    {
        OptionsCount = _item.RealOptionCount.ToString();
        DownloadsCount = _item.Downloads.Count.ToString();
        ShotsCount = _item.Images.Count.ToString();

        RefreshLaunchState();
    }

    /// <summary>截图落盘后重绑一次，之后即使断网也能看图。</summary>
    public async Task CacheScreenshotsAsync()
    {
        var added = await TrainerFiles.CacheScreenshotsAsync(_item);
        if (added == 0) return;

        TrainerDataService.SaveCache(_item);
        RebuildShots();
    }

    // ==================== 内部实现 ====================

    /// <summary>按筛选词重排选项列表。</summary>
    private void RebuildOptions()
    {
        var filter = FilterText.Trim();

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

        Options.Clear();
        foreach (var option in list) Options.Add(option);

        OptionsLoadingVisible = false;
        OptionsEmptyVisible = list.Count == 0;
    }

    /// <summary>
    /// 官网正文里夹着的说明段落（比如「本作使用 EasyAntiCheat，请这样启动…」）会被解析成一条假选项，
    /// 特征是一整句话、带引号且很长，这里把它们滤掉，列表末尾就不会多出一段描述文字。
    /// </summary>
    /// <param name="option">待判断的一条选项。</param>
    private static bool LooksLikeNote(TrainerOption option)
    {
        var name = option.Name.Trim();
        if (name.Length > 70) return true;
        return name.Length > 40 &&
               (name.EndsWith('.') || name.Contains('\u201C') || name.Contains('\u201D'));
    }

    /// <summary>重建下载表，并默认选中「已经下好的那一行」。</summary>
    private void RebuildDownloadRows()
    {
        Rows.Clear();
        foreach (var file in _item.Downloads)
            Rows.Add(new TrainerDetailRowVM(file));

        // 官网按时间倒序，第一行就是最新文件
        SelectRow(Rows.FirstOrDefault(r => r.HasLocalFile) ?? Rows.FirstOrDefault());
    }

    /// <summary>第一次切到截图页签时才把图挂上去。</summary>
    private void LoadScreenshots()
    {
        if (_shotsLoaded) return;

        _shotsLoaded = true;
        RebuildShots();
    }

    /// <summary>按当前图集重建截图列表。</summary>
    private void RebuildShots()
    {
        if (!_shotsLoaded) return;

        Shots.Clear();

        if (_item.Images.Count == 0)
        {
            ShotsEmptyVisible = true;
            return;
        }

        for (var i = 0; i < _item.Images.Count; i++)
            Shots.Add(new TrainerDetailShotVM(i, _item.Images[i], TrainerFiles.ShotSource(_item, i)));

        ShotsEmptyVisible = false;
    }

    /// <summary>
    /// 读下载表（需要联网才调）。本地已有就不重复抓；读之前把遮层打开，
    /// 太快返回时补到 0.4 秒，观感上是一次完整的加载。
    /// </summary>
    /// <param name="force">true = 用户点了刷新，忽略本地缓存。</param>
    private async Task LoadDownloadsAsync(bool force)
    {
        if (_downloadsLoading) return;
        if (!force && _item.HasDownloads) return;

        _downloadsLoading = true;
        ReloadEnabled = false;

        var startedAt = Environment.TickCount64;
        LoadingOverlayVisible = true;

        if (!_item.HasDownloads)
        {
            DownloadEmptyText = "正在从官网读取下载信息…";
            DownloadEmptyVisible = true;
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
                DownloadEmptyVisible = false;
            }
            else if (!_item.HasDownloads)
            {
                DownloadEmptyText = "官网暂无下载文件";
                DownloadEmptyVisible = true;
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
                DownloadEmptyText = "读取失败，点击右上角刷新重试";
                DownloadEmptyVisible = true;
            }
            else if (force)
            {
                ShowToast("读取失败，请检查网络");
            }
        }
        finally
        {
            _downloadsLoading = false;
            ReloadEnabled = true;

            var left = 400 - (Environment.TickCount64 - startedAt);
            if (left > 0) await Task.Delay((int)left);
            LoadingOverlayVisible = false;
        }
    }

    /// <summary>行里那颗按钮对应的下载：没有任务就入队。</summary>
    /// <param name="row">要下载的那一行。</param>
    private DownloadTask StartDownload(TrainerDetailRowVM row)
    {
        var file = _item.Downloads.FirstOrDefault(d => d.Url == row.Url);
        if (file is null)
            return DownloadManager.Instance.Enqueue(_item, new TrainerDownload { Url = row.Url, File = row.FileName });

        var task = DownloadManager.Instance.Enqueue(_item, file);
        row.Attach(task);

        if (task.State is DownloadState.Completed)
            ShowToast("该文件已经下载完成，点「打开」可定位到文件");
        else
            ShowToast($"已加入下载队列 · {DownloadManager.Instance.Tasks.Count} 个任务");

        return task;
    }

    /// <summary>启动本地文件（压缩包只帮定位，不硬跑）。</summary>
    /// <param name="path">文件的绝对路径。</param>
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

    /// <summary>用系统默认浏览器打开链接。</summary>
    /// <param name="url">要打开的地址。</param>
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

    /// <summary>弹一句提示（文字交给弹框淡入淡出显示）。</summary>
    /// <param name="message">要显示的话。</param>
    private void ShowToast(string message)
    {
        ToastText = message;
        ToastRequested?.Invoke(this, message);
    }

#if DEBUG
    /// <summary>自检用：走一遍「启动程序」的完整流程。</summary>
    /// <param name="path">要启动的文件。</param>
    public void LaunchFileForCheck(string path) => LaunchLocal(path);

    /// <summary>自检用：下载第一条文件。</summary>
    public void DownloadFirstForCheck() => DownloadLatest();

    /// <summary>自检用：等同于点右上角那个刷新按钮。</summary>
    public async Task ReloadForCheckAsync() => await LoadDownloadsAsync(true);

    /// <summary>自检用：强制打开加载遮层。</summary>
    public void ShowLoadingForCheck() => LoadingOverlayVisible = true;
#endif
}
