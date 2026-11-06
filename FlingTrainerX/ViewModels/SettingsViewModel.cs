using FlingTrainerX.Services;

namespace FlingTrainerX.ViewModels;

/// <summary>
/// 设置页的视图模型：左侧分组菜单选到哪一组、右侧只显示那一块，
/// 加上「关于」里版本与更新状态那两行字。
/// <para>
/// 分组显隐以前是代码里循环改四个 StackPanel 的 Visibility，现在只改一个
/// <see cref="GroupIndex"/>，四块各自绑一个 bool 出去 —— 谁在显示由状态决定，不用挨个通知。
/// </para>
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>菜单顺序，和界面上的四项一一对应。</summary>
    public static class Groups
    {
        public const int Data = 0;
        public const int General = 1;
        public const int Donate = 2;
        public const int About = 3;
    }

    /// <summary>当前选中的分组（0 数据更新 / 1 通用 / 2 打赏赞助 / 3 关于）。</summary>
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

    /// <summary>「当前版本 v1.0.2 · 已是最新版本」这一行。</summary>
    [ObservableProperty]
    private string _versionLine = "";

    /// <summary>右边「查看更新」下面那句提示。</summary>
    [ObservableProperty]
    private string _notesLine = "看看最新版本都改了什么。";

    /// <summary>检查更新那颗按钮能不能点（正在检查 / 正在安装时点不动）。</summary>
    [ObservableProperty]
    private bool _updateButtonEnabled = true;

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

    /// <summary>还没检查过：只写当前版本，说明启动时会自动查。</summary>
    public void ShowIdle(string currentVersion)
    {
        VersionLine = $"当前版本 v{currentVersion} · 启动时自动检查新版本";
        NotesLine = "看看最新版本都改了什么。";
    }

    /// <summary>正在查：按钮先锁住，那行字换成「正在检查…」。</summary>
    public void ShowChecking(string currentVersion)
    {
        VersionLine = $"当前版本 v{currentVersion} · 正在检查…";
        UpdateButtonEnabled = false;
    }

    /// <summary>把检查结果写回那两行说明（当前版本 / 最新版本 / 失败原因）。</summary>
    /// <param name="currentVersion">当前版本号（不带 v），比如 <c>1.0.2</c>。</param>
    /// <param name="result">检查结果。</param>
    public void ShowResult(string currentVersion, UpdateCheckResult result)
    {
        var current = $"当前版本 v{currentVersion}";

        switch (result.State)
        {
            case UpdateCheckState.UpdateAvailable when result.Release is { } newer:
                VersionLine = $"{current} · 发现新版本 v{newer.Version}";
                NotesLine = $"v{newer.Version} 的更新说明，点右侧查看。";
                break;

            case UpdateCheckState.UpToDate:
                VersionLine = $"{current} · 已是最新版本";
                NotesLine = result.Release is { } latest
                    ? $"最近一版是 v{latest.Version}，点右侧看更新说明。"
                    : "看看最新版本都改了什么。";
                break;

            default:
                VersionLine = $"{current} · {result.Message}";
                break;
        }
    }
}
