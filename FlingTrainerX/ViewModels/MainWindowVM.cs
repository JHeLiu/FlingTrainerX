using System.Collections.ObjectModel;
using FlingTrainerX.Models;
using FlingTrainerX.Services;

namespace FlingTrainerX.ViewModels;

/// <summary>
/// 主窗口（MainWindow.xaml）的根视图模型：窗口外壳那一层的状态 —— 左侧导航选中项、
/// 底部状态栏文字与指示灯、导航上的队列角标、标题栏两颗按钮的命令，
/// 以及首页 / 设置 / 同步三块子 ViewModel 的入口。
/// <para>
/// 分块约定：外壳（本文件）只管「窗口长什么样」，各分区自己的状态在
/// <see cref="Library"/> / <see cref="Settings"/> / <see cref="Sync"/> 里，
/// 由窗口把对应面板的 DataContext 指过去。
/// </para>
/// <para>
/// 窗口真正要做的事（拖动、动画、托盘、开关窗口）不在这里：VM 只抛事件，
/// 由窗口订阅后执行，VM 不直接引用任何控件。
/// </para>
/// </summary>
public sealed partial class MainWindowVM : ObservableObject, IStatusSink
{
    private readonly LibraryCatalog _catalog;
    private readonly UpdateCoordinator _updates;

    /// <summary>防止「导航 → 页面 → 导航」来回触发。</summary>
    private bool _syncingNav;

    public MainWindowVM(
        LibraryCatalog catalog,
        UpdateCoordinator updates,
        CacheResetService reset,
        ConfirmDialogVM confirm,
        UpdateDialogVM updateDialog,
        UpdateDownloadVM updateDownload,
        IAppDialogService dialogs)
    {
        _catalog = catalog;
        _updates = updates;
        Confirm = confirm;
        UpdateDialog = updateDialog;
        UpdateDownload = updateDownload;
        Dialogs = dialogs;

        Library = new MainWindowLibraryVM(catalog, Dialogs, this);
        Sync = new MainWindowSyncVM(this);
        Settings = new MainWindowSettingsVM(Library, Dialogs, this, Sync, updates, reset);

        // 更新开始 / 结束 → 设置页三颗按钮置忙；更新完 → 首页重读本地库
        Sync.BusyChanged = Settings.ShowRunning;
        Sync.LibraryReloaded = Library.ReloadFromLocal;

        // 搜索命中要切回游戏库；侧栏数字跟着收藏 / 库变化；启动程序成功后要把窗口收进托盘
        Library.ModeChangeRequested += (_, mode) => SwitchTo(mode);
        Library.StatsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FavoritesLabel));
            OnPropertyChanged(nameof(SyncTimeText));
        };
        Library.LaunchCompleted += (_, _) => HideForLaunchRequested?.Invoke(this, EventArgs.Empty);

        DownloadManager.Instance.Changed += (_, _) => OnDownloadsChanged();
    }

    // ==================== 子 ViewModel ====================

    /// <summary>首页（游戏库 / 收藏夹 / 队列 / 设置）那一块的视图模型。</summary>
    public MainWindowLibraryVM Library { get; }

    /// <summary>设置页的视图模型。</summary>
    public MainWindowSettingsVM Settings { get; }

    /// <summary>数据更新弹框的视图模型。</summary>
    public MainWindowSyncVM Sync { get; }

    /// <summary>确认框的视图模型（ConfirmLayer 的 DataContext）。</summary>
    public ConfirmDialogVM Confirm { get; }

    /// <summary>更新说明弹窗的视图模型（UpdateLayer 的 DataContext）。</summary>
    public UpdateDialogVM UpdateDialog { get; }

    /// <summary>更新包下载进度层的视图模型（DownloadLayer 的 DataContext）。</summary>
    public UpdateDownloadVM UpdateDownload { get; }

    /// <summary>弹窗 / 滚动 / 外链的门面，由窗口实现。</summary>
    public IAppDialogService Dialogs { get; }

    // ==================== 外壳状态 ====================

    /// <summary>底部状态栏那句话。</summary>
    [ObservableProperty]
    private string _statusText = "数据已就绪";

    /// <summary>状态栏指示灯的颜色档位（XAML 用 DataTrigger 换成画笔）。</summary>
    [ObservableProperty]
    private StatusLevel _statusTone = StatusLevel.Idle;

    /// <summary>左侧导航当前选中项（0 游戏库 / 1 收藏夹 / 2 下载队列 / 3 设置）。</summary>
    [ObservableProperty]
    private int _navIndex;

    /// <summary>导航上「下载队列」后面跟的角标文字（有任务时带数量）。</summary>
    [ObservableProperty]
    private string _queueLabel = "下载队列";

    /// <summary>队列为空时显示那块占位提示。</summary>
    [ObservableProperty]
    private bool _queueEmptyVisible = true;

    /// <summary>队列里有任务时才显示列表。</summary>
    [ObservableProperty]
    private bool _queueListVisible;

    /// <summary>侧栏「更新于 …」。</summary>
    public string SyncTimeText => Library.SyncTimeText;

    /// <summary>侧栏「收藏夹 · N」。</summary>
    public string FavoritesLabel => Library.FavoritesLabel;

    /// <summary>状态栏写一句话；<paramref name="level"/> 为空时保持当前灯色。</summary>
    /// <param name="text">要说的话。</param>
    /// <param name="level">指示灯档位。</param>
    public void Report(string text, StatusLevel? level = null)
    {
        StatusText = text;
        if (level is { } tone) StatusTone = tone;
    }

    partial void OnNavIndexChanged(int value)
    {
        if (_syncingNav) return;

        var mode = value switch
        {
            1 => ViewMode.Favorites,
            2 => ViewMode.Queue,
            3 => ViewMode.Settings,
            _ => ViewMode.Library
        };

        // 每次进设置页都从第一组开始：菜单归零、只显示「数据更新」那一块
        if (mode == ViewMode.Settings) Settings.GroupIndex = MainWindowSettingsVM.Groups.Data;

        Library.ShowMode(mode);
    }

    /// <summary>把导航切到某一页（搜索命中 / 空状态按钮都走它）。</summary>
    /// <param name="mode">目标页。</param>
    private void SwitchTo(ViewMode mode)
    {
        _syncingNav = true;
        NavIndex = mode switch
        {
            ViewMode.Favorites => 1,
            ViewMode.Queue => 2,
            ViewMode.Settings => 3,
            _ => 0
        };
        _syncingNav = false;

        if (mode == ViewMode.Settings) Settings.GroupIndex = MainWindowSettingsVM.Groups.Data;

        Library.ShowMode(mode);
    }

    // ==================== 窗口级动作 ====================

    /// <summary>窗口级动作由窗口订阅后执行（VM 不碰控件和窗口对象）。</summary>
    public event EventHandler? HideToTrayRequested;

    /// <summary>点了标题栏的关闭按钮。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>启动修改器成功后把窗口收进托盘。</summary>
    public event EventHandler? HideForLaunchRequested;

    /// <summary>最小化到托盘（不是任务栏最小化，是真正藏起来）。</summary>
    [RelayCommand]
    private void HideToTray() => HideToTrayRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>关闭窗口（按「收进托盘」的规则处理，见窗口的 Closing）。</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>打开「关于 / 免责声明」。</summary>
    [RelayCommand]
    private void ShowAbout() => Dialogs.ShowAbout();

    /// <summary>收起「关于」浮层。</summary>
    [RelayCommand]
    private void CloseAbout() => Dialogs.CloseAbout();

    /// <summary>打开「灵石结缘」。</summary>
    [RelayCommand]
    private void ShowSupport() => Dialogs.ShowSupport();

    /// <summary>收起「灵石结缘」浮层。</summary>
    [RelayCommand]
    private void CloseSupport() => Dialogs.CloseSupport();

    /// <summary>点左下角的 flingtrainer.com：用系统默认浏览器打开官网。</summary>
    [RelayCommand]
    private void OpenSite() => Dialogs.OpenExternal("https://flingtrainer.com/");

    /// <summary>空状态里的「更新数据」：和设置里的「更新全部游戏数据」是同一条路。</summary>
    [RelayCommand]
    private Task StartFullSync() => Sync.RunAsync(SyncKind.All);

    // ==================== 启动 ====================

    /// <summary>
    /// 首帧之后的静默准备：游戏名库空了就抓一份；启动 4 秒后自动查一次更新
    /// （只在真发现新版本时弹窗，断网 / 接口抽风一律安静）。
    /// </summary>
    /// <param name="ct">关窗口时取消。</param>
    public async Task InitializeAsync(CancellationToken ct)
    {
        // 搜索框的智能提示靠游戏名库；有数据直接跳过
        _ = GameNameSyncService.SeedIfEmptyAsync();

        // 每次启动只自动查一次更新
        if (_updates.MarkAutoChecked()) return;

        try
        {
            // 等 4 秒再查，别和「读本地库 / 拉封面」抢带宽，也别让开机第一眼就看到弹窗
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var result = await Settings.CheckAsync(manual: false);
            if (result is { State: UpdateCheckState.UpdateAvailable, Release: { } release })
                Dialogs.ShowUpdate(release, prompt: true);
        }
        catch
        {
            // 自动检查是锦上添花，出任何问题都不能影响正常使用
        }
    }

    // ==================== 下载队列 ====================

    /// <summary>队列列表的数据源：直接复用下载管理器里那份集合（增删自己会通知界面）。</summary>
    public ObservableCollection<DownloadTask> QueueTasks => DownloadManager.Instance.Tasks;

    private void OnDownloadsChanged()
    {
        var tasks = DownloadManager.Instance.Tasks;
        UpdateQueueBadge(tasks.Count, tasks.Count(t => t.IsActive));

        if (Library.Mode == ViewMode.Queue) Library.Refresh();
    }

    /// <summary>按当前下载任务刷新导航角标与队列占位。</summary>
    /// <param name="taskCount">队列里的任务总数。</param>
    /// <param name="activeCount">正在下载的任务数。</param>
    public void UpdateQueueBadge(int taskCount, int activeCount)
    {
        QueueLabel = activeCount > 0 ? $"下载队列 · {activeCount}" : "下载队列";

        var empty = taskCount == 0;
        QueueEmptyVisible = empty;
        QueueListVisible = !empty;
    }

    /// <summary>取消一个正在下载的任务。</summary>
    /// <param name="task">队列里那一行对应的任务。</param>
    [RelayCommand]
    private void CancelTask(DownloadTask? task)
    {
        if (task is not null) DownloadManager.Instance.Cancel(task);
    }

    /// <summary>重试一个失败的任务。</summary>
    /// <param name="task">队列里那一行对应的任务。</param>
    [RelayCommand]
    private void RetryTask(DownloadTask? task)
    {
        if (task is not null) DownloadManager.Instance.Retry(task);
    }

    /// <summary>打开任务文件所在的文件夹。</summary>
    /// <param name="task">队列里那一行对应的任务。</param>
    [RelayCommand]
    private void OpenTaskFolder(DownloadTask? task)
    {
        if (task is not null) DownloadManager.Instance.OpenFolder(task);
    }

    /// <summary>把任务从队列里移除。</summary>
    /// <param name="task">队列里那一行对应的任务。</param>
    [RelayCommand]
    private void RemoveTask(DownloadTask? task)
    {
        if (task is not null) DownloadManager.Instance.Remove(task);
    }

    /// <summary>打开下载目录（队列页右上角）。</summary>
    [RelayCommand]
    private void OpenDownloadFolder() => DownloadManager.Instance.OpenFolder(null);

    /// <summary>清理已完成 / 已取消的任务，顺手把结果写进状态栏。</summary>
    /// <returns>清掉的任务数。</returns>
    public int ClearFinishedTasks()
    {
        var count = DownloadManager.Instance.ClearFinished();
        StatusText = count == 0 ? "没有可清理的任务" : $"已清理 {count} 个任务";
        return count;
    }

    /// <summary>队列页「清理已完成」。</summary>
    [RelayCommand]
    private void ClearFinished()
    {
        ClearFinishedTasks();
        Library.Refresh();
    }
}
