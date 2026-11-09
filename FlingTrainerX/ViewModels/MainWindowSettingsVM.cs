using FlingTrainerX.Services;

namespace FlingTrainerX.ViewModels;

/// <summary>
/// 设置页的视图模型：左侧分组菜单选到哪一组、右侧只显示那一块，
/// 「关于」里版本与更新状态那两行字，以及三颗更新按钮 / 清除缓存 / 检查更新这些动作。
/// <para>
/// 分组显隐以前是代码里循环改四个 StackPanel 的 Visibility，现在只改一个
/// <see cref="GroupIndex"/>，四块各自绑一个 bool 出去。
/// 动作本身都交给了服务（<see cref="MainWindowSyncVM"/> / <see cref="UpdateCoordinator"/> /
/// <see cref="CacheResetService"/>），弹窗与滚动走 <see cref="IAppDialogService"/>。
/// </para>
/// </summary>
public sealed partial class MainWindowSettingsVM : ObservableObject
{
    private readonly MainWindowLibraryVM _library;
    private readonly IAppDialogService _dialogs;
    private readonly MainWindowSyncVM _sync;
    private readonly UpdateCoordinator _updates;
    private readonly CacheResetService _reset;

    public MainWindowSettingsVM(
        MainWindowLibraryVM library,
        IAppDialogService dialogs,
        MainWindowSyncVM sync,
        UpdateCoordinator updates,
        CacheResetService reset)
    {
        _library = library;
        _dialogs = dialogs;
        _sync = sync;
        _updates = updates;
        _reset = reset;
    }

    /// <summary>菜单顺序，和界面上的四项一一对应。</summary>
    public static class Groups
    {
        public const int Data = 0;
        public const int General = 1;
        public const int About = 2;
        public const int Donate = 3;
    }

    /// <summary>当前选中的分组（0 数据管理 / 1 免责声明 / 2 关于 / 3 打赏赞助）。</summary>
    [ObservableProperty]
    private int _groupIndex = Groups.Data;

    public bool ShowData => GroupIndex == Groups.Data;

    public bool ShowGeneral => GroupIndex == Groups.General;

    public bool ShowDonate => GroupIndex == Groups.Donate;

    public bool ShowAbout => GroupIndex == Groups.About;

    // ==================== 数据更新 · 三颗入口按钮 ====================

    /// <summary>没有更新在跑时三颗按钮都可点。</summary>
    [ObservableProperty]
    private bool _updateButtonsEnabled = true;

    [ObservableProperty]
    private string _updateAllText = "开始更新";

    [ObservableProperty]
    private string _updateExistingText = "开始更新";

    [ObservableProperty]
    private string _quickNamesText = "开始更新";

    // ==================== 关于 · 版本与更新 ====================

    /// <summary>「当前版本 v1.0.2」这一行。</summary>
    [ObservableProperty]
    private string _versionLine = "";

    /// <summary>检查更新那颗按钮能不能点（正在检查 / 正在安装时点不动）。</summary>
    [ObservableProperty]
    private bool _updateButtonEnabled = true;

    /// <summary>「清除缓存」那颗按钮能不能点（正在清的时候点不动）。</summary>
    [ObservableProperty]
    private bool _clearCacheEnabled = true;

    partial void OnGroupIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowData));
        OnPropertyChanged(nameof(ShowGeneral));
        OnPropertyChanged(nameof(ShowDonate));
        OnPropertyChanged(nameof(ShowAbout));
    }

    /// <summary>
    /// 三颗入口按钮统一置忙 / 复原：正在跑的那颗显示「更新中…」。
    /// </summary>
    /// <param name="running">哪一颗在跑；null = 一起复原。</param>
    public void ShowRunning(SyncKind? running)
    {
        UpdateButtonsEnabled = running is null;

        UpdateAllText = running == SyncKind.All ? "更新中…" : "开始更新";
        UpdateExistingText = running == SyncKind.Existing ? "更新中…" : "开始更新";
        QuickNamesText = running == SyncKind.Names ? "更新中…" : "开始更新";
    }

    /// <summary>还没检查过：只写当前版本（更新内容那一行是固定文案，不用刷）。</summary>
    public void ShowIdle(string currentVersion)
    {
        VersionLine = $"当前版本 v{currentVersion}";
    }

    /// <summary>正在查：按钮先锁住；版本那一行不动。</summary>
    public void ShowChecking(string currentVersion)
    {
        VersionLine = $"当前版本 v{currentVersion}";
        UpdateButtonEnabled = false;
    }

    /// <summary>
    /// 检查结果不写进「软件更新」那一行 —— 这里只保证版本号是最新的。
    /// 有新版本 / 已是最新 / 检查失败的提示走弹窗，不占用底部状态栏。
    /// </summary>
    /// <param name="currentVersion">当前版本号（不带 v），比如 <c>1.0.2</c>。</param>
    /// <param name="result">检查结果（保留参数，调用方不必分两种写法）。</param>
    public void ShowResult(string currentVersion, UpdateCheckResult result)
    {
        VersionLine = $"当前版本 v{currentVersion}";
    }

    // ==================== 数据更新 ====================

    /// <summary>「更新全部游戏数据」：官网列表 + 游戏名录 + 中文名。</summary>
    [RelayCommand]
    private Task SyncAll() => _sync.RunAsync(SyncKind.All);

    /// <summary>「更新已有游戏数据」：按本地库逐款重抓详情，再补中文名。</summary>
    [RelayCommand]
    private Task SyncExisting() => _sync.RunAsync(SyncKind.Existing);

    /// <summary>「快速搜索游戏名」：先立准备框，勾好再开跑。</summary>
    [RelayCommand]
    private Task QuickNames() => _sync.QuickNamesAsync();

    // ==================== 清除缓存 ====================

    /// <summary>一键清除缓存：先问一句，确认后清 SQLite 的卡片缓存 + 删掉 f 目录下的所有内容。</summary>
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        if (_reset.IsRunning || !_reset.IsDone) return;   // 上一次还没走完，别叠着来一遍

        var active = DownloadManager.Instance.Tasks.Count(t => t.IsActive);
        var body = "这样操作会删除本地的缓存数据。" +
                   (active > 0 ? $"\n\n当前还有 {active} 个下载任务，会先被取消。" : "");

        if (!await _dialogs.ConfirmAsync("清除本地缓存？", body, "清除")) return;

        ClearCacheEnabled = false;

        try
        {
            await _reset.ClearAsync(() => _library.SearchInFlight || _library.IsBusyLoading);

            // 不弹「清完了」：列表当场变空就是最好的反馈，弹窗还得再点一下
            ResetAfterClear();
        }
        catch (Exception ex)
        {
            await _dialogs.NotifyAsync("清除失败", ex.Message);
        }
        finally
        {
            ClearCacheEnabled = true;
        }
    }

    /// <summary>
    /// 清完缓存之后，内存里那份列表也得跟着丢掉：
    /// 否则界面还挂着几百张卡片，而「已经收录」按 SQLite 算已经是 0，两个数字互相打脸。
    /// 首页只读本地，所以这里重读一遍库（现在是空的），不再去官网拉数据。
    /// </summary>
    private void ResetAfterClear()
    {
        try
        {
            if (_library.IsSearching) _library.ExitSearch(clearText: true);

            _library.ReloadFromLocal();
        }
        finally
        {
            _reset.MarkDone();
        }
    }

    // ==================== 在线更新 ====================

    /// <summary>设置页「检查更新」：有新版本就弹更新框，否则弹一句「已经是最新」。</summary>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        var result = await CheckAsync(manual: true);
        await PresentAsync(result, auto: false);
    }

    /// <summary>
    /// 把一次检查结果呈现出来。手动点的每一次都有反馈；启动时自动查的那次**只在发现新版本时弹窗**，
    /// 其余（已是最新、断网超时、404/403 拿不到发行版）一律安静。
    /// </summary>
    /// <param name="result">检查结果。</param>
    /// <param name="auto">是不是启动后的自动检查。</param>
    public async Task PresentAsync(UpdateCheckResult result, bool auto)
    {
        switch (result.State)
        {
            case UpdateCheckState.UpdateAvailable when result.Release is { } release:
                _dialogs.ShowUpdate(release, prompt: true);
                break;

            case UpdateCheckState.UpToDate when !auto:
                await _dialogs.NotifyAsync(
                    "已是最新版本",
                    $"当前版本 v{UpdateService.CurrentVersionText}，无需更新。");
                break;

            // 404 / 401 / 403 / 410：仓库或发行版读不到，在线更新这条路直接断了。
            // 不把状态码摆给用户看，只给一句能照做的：去网盘下。
            // 自动检查碰到这种情况**不弹**，静默跳过。
            case UpdateCheckState.Failed when !auto && result.RepoUnreachable:
                await _dialogs.NotifyAsync("在线更新不可用", $"{UpdateCheckResult.UnavailableText}。");
                break;

            case UpdateCheckState.Failed when !auto:
                await _dialogs.NotifyAsync("检查更新失败", result.Message);
                break;
        }
    }

    /// <summary>
    /// 设置页「查看更新」：只看说明，不安装。
    /// 正文读本地 <c>Assets\changelog.txt</c>，不走网络，断网也能看。
    /// </summary>
    [RelayCommand]
    private void ViewUpdateNotes()
    {
        _dialogs.ShowUpdateNotes(
            $"更新内容 · v{UpdateService.CurrentVersionText}",
            $"当前版本 v{UpdateService.CurrentVersionText}",
            ChangelogText.Load());
    }

    /// <summary>
    /// 查一次更新：更新设置页那一行文案，并把结果交回去给调用方决定弹不弹窗。
    /// 失败不抛异常，原因由调用方用弹窗说明（不走底部状态栏）。
    /// </summary>
    /// <param name="manual">是不是用户手动点的（保留参数：两条路径都不写状态栏了）。</param>
    public async Task<UpdateCheckResult> CheckAsync(bool manual)
    {
        ShowChecking(UpdateService.CurrentVersionText);

        UpdateCheckResult result;
        try
        {
            result = await _updates.CheckAsync();
        }
        finally
        {
            UpdateButtonEnabled = !_updates.IsUpdating;
        }

        RefreshUpdateRow(result);
        return result;
    }

    /// <summary>把检查结果写回设置页那两行说明；null = 还没查过。</summary>
    public void RefreshUpdateRow(UpdateCheckResult? result = null)
    {
        if (result is null) ShowIdle(UpdateService.CurrentVersionText);
        else ShowResult(UpdateService.CurrentVersionText, result);
    }

    /// <summary>
    /// 「立即更新」：程序内下载安装包（带真实进度条），下完拉起安装程序。
    /// 弹窗已经收起、收场动画走完之后才调这里（见 AppOverlays）。
    /// </summary>
    public async Task StartInstallAsync()
    {
        if (_updates.IsUpdating || _updates.Release is not { } release) return;

        // 发行版里没挂更新包：没法自动装，直接把发行版页面打开让用户自己下
        if (string.IsNullOrWhiteSpace(release.DownloadUrl))
        {
            await _dialogs.NotifyAsync(
                "这个版本没有可下载的更新包",
                "已经打开 Gitee 发行版页面，可以手动下载。");
            _dialogs.OpenExternal(release.PageUrl);
            return;
        }

        UpdateButtonEnabled = false;

        string? path;
        try
        {
            // 下载浮层在 AppOverlays 里开，进度条由 UpdateDownloadVM 驱动
            path = await _updates.RunAsync(release, _dialogs.DownloadUpdateAsync);
        }
        catch (UpdateUnavailableException)
        {
            // 更新包地址 404 / 被拒：不给用户看状态码，直接指去网盘
            UpdateButtonEnabled = true;
            await _dialogs.NotifyAsync("在线更新不可用", $"{UpdateCheckResult.UnavailableText}。");
            return;
        }
        catch (Exception ex)
        {
            UpdateButtonEnabled = true;
            await _dialogs.NotifyAsync("下载更新失败", ex.Message);
            return;
        }

        if (path is null)
        {
            // 用户在下载浮层里点了取消：不用再弹一句，他自己知道
            UpdateButtonEnabled = true;
            return;
        }

        try
        {
            _dialogs.LaunchInstaller(path);
        }
        catch (Exception ex)
        {
            UpdateButtonEnabled = true;
            await _dialogs.NotifyAsync("打开安装程序失败", $"{ex.Message}\n\n安装包已下载到：\n{Path.GetDirectoryName(path) ?? UpdateService.DownloadDir}");
            _dialogs.OpenExternal(Path.GetDirectoryName(path) ?? UpdateService.DownloadDir);
            return;
        }

        // 安装程序要替换正在使用的文件，本进程必须真退（不能收进托盘）
        _dialogs.ShutdownForUpdate();
    }

    /// <summary>正在下载 / 安装更新（这期间 Esc 关不掉更新弹窗）。</summary>
    public bool IsUpdateInstalling => _updates.IsUpdating;

    // ==================== 网盘下载 ====================

    /// <summary>夸克网盘分享地址：Gitee 附件下不动或额度用满时的备用下载通道。</summary>
    private const string CloudDriveUrl = "https://pan.quark.cn/s/ed32311b1952";

    /// <summary>「点击进入」：用系统默认浏览器打开夸克网盘分享页，程序内不参与下载。</summary>
    [RelayCommand]
    private void OpenCloudDrive() => _dialogs.OpenExternal(CloudDriveUrl);

    /// <summary>「关于」弹窗。</summary>
    [RelayCommand]
    private void OpenAbout() => _dialogs.ShowAbout();

    /// <summary>「灵石结缘」弹窗。</summary>
    [RelayCommand]
    private void OpenSupport() => _dialogs.ShowSupport();
}
