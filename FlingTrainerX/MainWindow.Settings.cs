// 由 MainWindow.xaml.cs 按功能拆出来的分部类（partial），与主文件同属一个类。
// 这样每个文件只放一类逻辑，改「搜索」不用在六千行里翻。

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

/// <summary>
/// 主窗口的一部分：设置页：分组菜单、清缓存、关于与在线更新、打赏。
/// </summary>
public partial class MainWindow
{
    // ==================== 设置 · 左侧分组菜单 ====================

    /// <summary>
    /// 点菜单：哪一组显示由 <see cref="SettingsViewModel.GroupIndex"/> 决定，
    /// 四块内容各自绑了一个 bool（见 XAML），这里只补一件事 —— 换组后滚回顶部。
    /// <para>
    /// 收起用 Collapsed 而不是 Hidden —— Collapsed 不参与布局，切过去不会在顶部留一截空白。
    /// </para>
    /// </summary>
    private void SettingsMenu_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsMenu.SelectedIndex < 0) return;

        // 菜单和 ViewModel 是双向绑的，这里兜个底，省得以后改成只读时漏掉
        Settings.GroupIndex = SettingsMenu.SelectedIndex;

        // 换了一组就从顶部看起（有的组内容短、滚不动，这个调用没有副作用）
        SettingsScroll.ScrollToTop();
    }

    /// <summary>代码里切组（比如每次进设置页都回到第一组）。</summary>
    private void ShowSettingsGroup(int index)
    {
        Settings.GroupIndex = index;
        SettingsScroll.ScrollToTop();
    }

    // ==================== 设置 · 清除缓存 ====================

    /// <summary>正在等用户回话的确认框；同一时间只允许一个。</summary>
    private TaskCompletionSource<ConfirmAnswer>? _confirm;

    /// <summary>一键清除缓存：先问一句，确认后清 SQLite 的卡片缓存 + 删掉 f 目录下的所有内容。</summary>
    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (_confirm is not null) return;   // 连点两下只弹一个框
        if (!_resetDone) return;            // 上一次清完还在重新拉首页，别叠着来一遍

        var active = DownloadManager.Instance.Tasks.Count(t => t.IsActive);
        var body = "这样操作会删除本地的缓存数据。" +
                   (active > 0 ? $"\n\n当前还有 {active} 个下载任务，会先被取消。" : "");

        if (!await ConfirmAsync("清除本地缓存？", body, "清除")) return;

        _resetDone = false;                 // 从这里开始，整套清除 + 重新加载都没走完
        ClearCacheButton.IsEnabled = false;
        AppDatabase.MuteCache();            // 掐断在途下载，别让缓存写入和落盘又填回来
        try
        {
            // 1) 先停掉正在下的任务，免得一边删一边又写回来
            foreach (var task in DownloadManager.Instance.Tasks.Where(t => t.IsActive).ToList())
                DownloadManager.Instance.Cancel(task);

            // 2) 等首页加载 / 翻页请求 / 封面下载 / 被取消的任务收尾（否则刚删完又被写回去）
            for (var i = 0; i < 40 &&
                 (_firstLoading || _libraryPager.Loading || _searchPager.Loading ||
                  DownloadManager.Instance.ActiveCount > 0); i++)
                await Task.Delay(100);

            // 3) 清空 SQLite 里的卡片缓存表（收藏夹那张表不动）+ 删掉 f 目录下的所有内容
            var rows = AppDatabase.Instance.ClearCache();
            var (files, bytes, failed) = DeleteContents(AppPaths.FilesDir);

            // 4) 已经在传输中的封面可能还会落盘，多扫几轮直到连续一轮没东西可删
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(400);
                rows += AppDatabase.Instance.ClearCache();

                var (extraFiles, extraBytes, extraFailed) = DeleteContents(AppPaths.FilesDir);
                files += extraFiles;
                bytes += extraBytes;
                failed = extraFailed;

                if (extraFiles == 0 && extraFailed == 0) break;   // 这一轮没东西可删 = 真的空了
            }

            var summary = $"已清除缓存 · {rows} 条缓存记录 · {files} 个文件（{DownloadTask.Human(bytes)}）" +
                          (failed > 0 ? $" · {failed} 项被占用，已跳过" : "");
            StatusText.Text = summary;
            _pendingClearSummary = summary;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"清除失败：{ex.Message}";
        }
        finally
        {
            // 再静音一小会儿，别让马上又开始的下一次下载把刚清掉的东西立刻写回来
            await Task.Delay(800);
            AppDatabase.UnmuteCache();
            ClearCacheButton.IsEnabled = true;
        }

        ResetAfterClear();
    }

    /// <summary>清完缓存后要长时间显示的提示，重新加载列表时会被覆盖，加载完再贴回去。</summary>
    private string _pendingClearSummary = "";

    /// <summary>清完缓存 + 重新加载这一整套流程是否走完（只给自检用来等一个稳定状态）。</summary>
    private bool _resetDone = true;

    /// <summary>
    /// 清完缓存之后，内存里那份列表也得跟着丢掉：
    /// 否则界面还挂着几百张卡片，而「已经收录」按 SQLite 算已经是 0，两个数字互相打脸。
    /// 首页只读本地，所以这里重读一遍库（现在是空的），不再去官网拉数据。
    /// </summary>
    private void ResetAfterClear()
    {
        _resetDone = false;
        try
        {
            if (Searching) ExitSearch(clearText: true);

            // 换代序号 + 分页重置：清空过程中在飞的翻页响应回来后直接丢弃
            _librarySeq++;
            LoadCachedCards();

            Render();
            UpdateStats();
            UpdateQueueBadge();

            // 重读会把状态栏刷成条数信息，把这次清掉多少再贴回去，别让提示一闪就没
            if (!string.IsNullOrEmpty(_pendingClearSummary))
            {
                StatusText.Text = _pendingClearSummary;
                _pendingClearSummary = "";
            }
        }
        finally
        {
            _resetDone = true;
        }
    }

    /// <summary>
    /// 删掉目录下的全部内容（保留目录本身）。单个文件被占用时跳过、不中断，
    /// 返回删掉的文件数、总字节数和失败项数。
    /// </summary>
    private static (int files, long bytes, int failed) DeleteContents(string root)
    {
        if (!Directory.Exists(root)) return (0, 0, 0);

        var files = 0;
        var bytes = 0L;
        var failed = 0;

        // 子目录（f\<id>\）整棵删掉
        foreach (var dir in Directory.EnumerateDirectories(root).ToList())
        {
            try
            {
                var info = new DirectoryInfo(dir);
                var count = 0;
                var size = 0L;
                foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    count++;
                    size += file.Length;
                }

                info.Delete(recursive: true);
                files += count;
                bytes += size;
            }
            catch
            {
                failed++;
            }
        }

        // 根目录下散着的文件
        foreach (var path in Directory.EnumerateFiles(root).ToList())
        {
            try
            {
                bytes += new FileInfo(path).Length;
                File.Delete(path);
                files++;
            }
            catch
            {
                failed++;
            }
        }

        return (files, bytes, failed);
    }

    /// <summary>确认框的回答：点了「确定」没有、附带的可选项勾没勾。</summary>
    /// <param name="Ok">true = 用户点了确定；false = 取消 / 点遮罩 / Esc。</param>
    /// <param name="Checked">可选项是否勾选（没有可选项时恒为 false）。</param>
    private readonly record struct ConfirmAnswer(bool Ok, bool Checked);

    /// <summary>弹出确认框，等用户点「确定」（true）或「取消 / 点遮罩 / Esc」（false）。</summary>
    /// <param name="primary">确定按钮用主色（普通操作）；默认用警示色（删除 / 清空这类不可逆操作）。</param>
    private async Task<bool> ConfirmAsync(string title, string body, string okText, bool primary = false)
    {
        var answer = await ShowConfirmAsync(title, body, okText, primary, null);
        return answer.Ok;
    }

    /// <summary>
    /// 带一个可选项的确认框（例如「快速更新游戏名」里问要不要顺手更新中文名）。
    /// 可选项<b>默认不勾</b>，用户自己勾了才会连带执行。
    /// </summary>
    /// <param name="title">标题。</param>
    /// <param name="body">正文说明。</param>
    /// <param name="okText">确定按钮上的字。</param>
    /// <param name="optionText">可选项的主文案。</param>
    /// <param name="optionHint">可选项下面那行小字说明。</param>
    private Task<ConfirmAnswer> ConfirmWithOptionAsync(
        string title, string body, string okText, string optionText, string optionHint)
        => ShowConfirmAsync(title, body, okText, true, (optionText, optionHint));

    /// <summary>确认框的公共实现：<paramref name="option"/> 为 null 时不显示可选项。</summary>
    /// <param name="option">可选项的（主文案, 说明）；null = 不要可选项。</param>
    private Task<ConfirmAnswer> ShowConfirmAsync(
        string title, string body, string okText, bool primary, (string Text, string Hint)? option)
    {
        // 已经有一个框开着（连点两下）就当作取消，别叠第二个
        if (_confirm is not null) return Task.FromResult(new ConfirmAnswer(false, false));

        ConfirmTitle.Text = title;
        ConfirmBody.Text = body;
        ConfirmOkButton.Content = okText;
        ConfirmOkButton.Style = (Style)FindResource(primary ? "PrimaryBtn" : "DangerBtn");

        // 可选项：有就摆出来并清零，没有就整块收起（上一个框勾过的状态不会带过来）
        ConfirmOptionPanel.Visibility = option is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmOptionCheck.IsChecked = false;
        ConfirmOptionText.Text = option?.Text ?? "";
        ConfirmOptionHint.Text = option?.Hint ?? "";
        ConfirmOptionHint.Visibility = (option?.Hint ?? "").Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        ConfirmLayer.Visibility = Visibility.Visible;

        _confirm = new TaskCompletionSource<ConfirmAnswer>();
        ConfirmOkButton.Focus();
        return _confirm.Task;
    }

    private void CloseConfirm(bool result)
    {
        ConfirmLayer.Visibility = Visibility.Collapsed;

        var pending = _confirm;
        _confirm = null;
        pending?.TrySetResult(new ConfirmAnswer(result, ConfirmOptionCheck.IsChecked == true));
    }

    private void ConfirmOk_Click(object sender, RoutedEventArgs e) => CloseConfirm(true);

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => CloseConfirm(false);

    /// <summary>点遮罩（弹框外面那片半透明区域）= 取消。遮罩是独立一层，点卡片碰不到它。</summary>
    private void ConfirmBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseConfirm(false);

    // ==================== 关于 / 声明弹窗 ====================

    // 新版 QQ 那套观感：淡入 + 从 0.92 轻微放大（带一点回弹），关闭时略微缩一下再淡出。
    // 用 Animation 而不是 Visibility 硬切，时间都压得比较长，看起来"慢慢打开/合上"。
    private static readonly TimeSpan AboutOpenTime = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan AboutCloseTime = TimeSpan.FromMilliseconds(220);

    /// <summary>每次开关自增，用来识别"关闭动画播到一半又被重新打开"的情况。</summary>
    private int _aboutAnimToken;

    private void About_Click(object sender, RoutedEventArgs e) => ShowAbout();

    /// <summary>
    /// 把 Assets\免责声明.txt 的全文直接铺到「关于」弹窗里：
    /// 换行、空行、缩进完全跟着文本走，代码这边不解析、不判断哪一行是标题，
    /// 也不给某一行单独上色 —— 想改排版就直接改文本。
    /// </summary>
    private void BuildDisclaimerBody()
    {
        AboutBody.Text = DisclaimerText.Load();
    }

    private void ShowAbout()
    {
        ++_aboutAnimToken;
        AboutLayer.Visibility = Visibility.Visible;

        // 正文来自文本文件，每次打开都重读一遍
        BuildDisclaimerBody();

        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        // 回弹：Amplitude 越大弹得越明显；0.5 实测大约多冲出去 0.5% 再回落，肉眼刚好能察觉
        var spring = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };

        AboutBackdrop.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(240))) { EasingFunction = fade });

        AboutCard.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170))) { EasingFunction = fade });

        AboutScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.93, 1, new Duration(AboutOpenTime)) { EasingFunction = spring });
        AboutScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.93, 1, new Duration(AboutOpenTime)) { EasingFunction = spring });

        AboutShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, new Duration(AboutOpenTime)) { EasingFunction = fade });
    }

    private void CloseAbout()
    {
        if (AboutLayer.Visibility != Visibility.Visible)
        {
            return;
        }

        var token = ++_aboutAnimToken;
        var fade = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(AboutCloseTime);

        var cardFade = new DoubleAnimation(1, 0, duration) { EasingFunction = fade };

        // 注意：Completed 必须在 BeginAnimation 之前挂，先启动再挂回调的话永远不会触发
        cardFade.Completed += (_, _) =>
        {
            // 播完时如果用户又点开了（token 变了），就别收层、也别清动画
            if (token != _aboutAnimToken)
            {
                return;
            }

            AboutLayer.Visibility = Visibility.Collapsed;

            // 清掉动画值，让元素回到 XAML 里的初始态（0.92 / 透明），下次打开才能从头播
            AboutCard.BeginAnimation(OpacityProperty, null);
            AboutBackdrop.BeginAnimation(OpacityProperty, null);
            AboutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            AboutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            AboutShift.BeginAnimation(TranslateTransform.YProperty, null);
        };

        // 卡片先淡出、缩一点点（0.97）再收层，比直接消失柔和
        AboutCard.BeginAnimation(OpacityProperty, cardFade);
        AboutScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        AboutScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        AboutShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, 6, duration) { EasingFunction = fade });
        AboutBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration) { EasingFunction = fade });
    }

    private void AboutClose_Click(object sender, RoutedEventArgs e) => CloseAbout();

    private void AboutBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseAbout();

    // ==================== 设置 · 关于 / 在线更新 ====================

    // 和「关于」弹窗同一套观感：淡入 + 轻微放大回弹，关闭时先缩一点再收层
    private static readonly TimeSpan UpdateOpenTime = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan UpdateCloseTime = TimeSpan.FromMilliseconds(220);

    /// <summary>每次开关自增，用来识别「关闭动画播到一半又被重新打开」。</summary>
    private int _updateAnimToken;

    /// <summary>最近一次查到的发行版；拿到就缓存，「查看更新」不用再查一遍。</summary>
    private UpdateRelease? _updateRelease;

    /// <summary>本次运行已经自动查过一次更新（启动检查只跑一次）。</summary>
    private bool _updateChecked;

    /// <summary>正在下载 / 安装更新：这期间不再响应检查按钮，也不让 Esc 关弹窗。</summary>
    private bool _updating;

    /// <summary>正在检查更新：同一时间只跑一次，避免连点几个请求叠在一起。</summary>
    private bool _updateChecking;

    /// <summary>弹窗现在是不是「发现新版本」（true）——false 表示只是「查看更新内容」。</summary>
    private bool _updatePromptMode;

    /// <summary>检查更新的取消源，窗口关掉时顺手掐断。</summary>
    private CancellationTokenSource? _updateCts;

    /// <summary>
    /// 启动后自动检查一次更新：等 4 秒再查，别和「读本地库 / 拉封面」抢带宽，
    /// 也别让开机第一眼就看到一个弹窗。
    /// </summary>
    private async void CheckUpdatesOnStartup()
    {
        if (_updateChecked) return;
        _updateChecked = true;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
        }
        catch
        {
            // 关窗口时 Delay 会被打断，直接走人
            return;
        }

        if (_exitRequested) return;

        try
        {
            var result = await CheckForUpdateAsync(manual: false);
            if (_exitRequested) return;

            // 自动检查只在「真的发现新版本」时打扰用户；断网 / 接口抽风一律安静
            if (result is { State: UpdateCheckState.UpdateAvailable, Release: { } release })
                ShowUpdateDialog(release, prompt: true);
        }
        catch
        {
            // 自动检查是锦上添花，出任何问题都不能影响正常使用
        }
    }

    /// <summary>设置页「检查更新」按钮。</summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var result = await CheckForUpdateAsync(manual: true);
        if (result is { State: UpdateCheckState.UpdateAvailable, Release: { } release })
            ShowUpdateDialog(release, prompt: true);
    }

    /// <summary>设置页「查看更新」按钮：只看说明，不安装。</summary>
    private async void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        // 还没查过就先查一次（设置页可能刚打开就点了）
        if (_updateRelease is null) await CheckForUpdateAsync(manual: false);

        if (_updateRelease is { } release)
        {
            ShowUpdateDialog(release, prompt: false);
            return;
        }

        StatusText.Text = "暂时拿不到更新说明，稍后再试";
    }

    /// <summary>
    /// 查一次更新：更新设置页那一行文案，并把结果交回去给调用方决定弹不弹窗。
    /// 失败不抛异常，原因写在状态栏和设置页说明里。
    /// </summary>
    /// <param name="manual">是不是用户手动点的（手动点才在状态栏回话）。</param>
    private async Task<UpdateCheckResult> CheckForUpdateAsync(bool manual)
    {
        if (_updating) return UpdateCheckResult.Fail("正在更新，等这次装完再检查");
        if (_updateChecking) return UpdateCheckResult.Fail("正在检查更新…");

        _updateChecking = true;
        Settings.ShowChecking(UpdateService.CurrentVersionText);
        if (manual) StatusText.Text = "正在检查更新…";

        UpdateCheckResult result;
        try
        {
            // 20 秒还没结果就当超时，免得按钮一直转
            _updateCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            result = await UpdateService.CheckAsync(_updateCts.Token);
        }
        finally
        {
            _updateCts?.Dispose();
            _updateCts = null;
            _updateChecking = false;
            Settings.UpdateButtonEnabled = !_updating;
        }

        RefreshUpdateRow(result);

        if (!manual) return result;

        StatusText.Text = result.State switch
        {
            UpdateCheckState.UpdateAvailable => result.Message,
            UpdateCheckState.UpToDate => $"已经是最新版本 v{UpdateService.CurrentVersionText}",
            _ => result.Message
        };

        return result;
    }

    /// <summary>把检查结果写回设置页那两行说明（当前版本 / 最新版本 / 失败原因）。</summary>
    private void RefreshUpdateRow(UpdateCheckResult? result = null)
    {
        // 有发行版信息就缓存下来，「查看更新」直接用
        if (result?.Release is { } release) _updateRelease = release;

        if (result is null) Settings.ShowIdle(UpdateService.CurrentVersionText);
        else Settings.ShowResult(UpdateService.CurrentVersionText, result);
    }

    /// <summary>
    /// 打开更新弹窗。<paramref name="prompt"/> 为 true 时是「发现新版本」（带立即更新），
    /// 为 false 时只展示更新说明（按钮变成「知道了」）。
    /// </summary>
    private void ShowUpdateDialog(UpdateRelease release, bool prompt)
    {
        ++_updateAnimToken;

        _updateRelease = release;
        _updatePromptMode = prompt;

        UpdateTitleText.Text = prompt ? $"发现新版本 v{release.Version}" : $"更新内容 · v{release.Version}";

        var parts = new List<string> { $"当前版本 v{UpdateService.CurrentVersionText}" };
        if (prompt) parts.Add($"最新版本 v{release.Version}");
        if (release.PublishedText.Length > 0) parts.Add(release.PublishedText);
        if (prompt && release.SizeText.Length > 0) parts.Add($"更新包 {release.SizeText}");
        UpdateVersionText.Text = string.Join(" · ", parts);

        UpdateNotesBox.Text = release.Notes.Length > 0
            ? release.Notes
            : "这个版本没有填写更新说明。";
        UpdateNotesBox.ScrollToHome();

        UpdateLaterButton.Visibility = prompt ? Visibility.Visible : Visibility.Collapsed;
        UpdateNowButton.Content = prompt ? "立即更新" : "知道了";
        UpdateHintText.Visibility = prompt ? Visibility.Visible : Visibility.Collapsed;

        UpdateLayer.Visibility = Visibility.Visible;

        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        var spring = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };

        UpdateBackdrop.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(240))) { EasingFunction = fade });

        UpdateCard.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170))) { EasingFunction = fade });

        UpdateScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.93, 1, new Duration(UpdateOpenTime)) { EasingFunction = spring });
        UpdateScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.93, 1, new Duration(UpdateOpenTime)) { EasingFunction = spring });

        UpdateShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, new Duration(UpdateOpenTime)) { EasingFunction = fade });
    }

    /// <summary>收起更新弹窗（点遮罩、Esc、「稍后再说」「知道了」都走这里）。</summary>
    private void CloseUpdateDialog()
    {
        if (UpdateLayer.Visibility != Visibility.Visible) return;

        var token = ++_updateAnimToken;
        var fade = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(UpdateCloseTime);

        var cardFade = new DoubleAnimation(1, 0, duration) { EasingFunction = fade };

        // Completed 必须挂在 BeginAnimation 之前，先启动再挂回调的话永远不会触发
        cardFade.Completed += (_, _) =>
        {
            // 播完时如果用户又点开了（token 变了），就别收层、也别清动画
            if (token != _updateAnimToken) return;

            UpdateLayer.Visibility = Visibility.Collapsed;

            UpdateCard.BeginAnimation(OpacityProperty, null);
            UpdateBackdrop.BeginAnimation(OpacityProperty, null);
            UpdateScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            UpdateScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            UpdateShift.BeginAnimation(TranslateTransform.YProperty, null);
        };

        UpdateCard.BeginAnimation(OpacityProperty, cardFade);
        UpdateScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        UpdateScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        UpdateShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, 6, duration) { EasingFunction = fade });
        UpdateBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration) { EasingFunction = fade });
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e) => CloseUpdateDialog();

    private void UpdateBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseUpdateDialog();

    /// <summary>「立即更新」/「知道了」：前者收起弹窗后开始下载，后者只关闭。</summary>
    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (!_updatePromptMode)
        {
            CloseUpdateDialog();
            return;
        }

        CloseUpdateDialog();

        // 等收场动画走完再弹下载窗口，否则两层窗口会叠在一起
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            StartUpdateInstall();
        };
        timer.Start();
    }

    /// <summary>
    /// 交给 AutoUpdater.NET 下载并安装：zip 会解压覆盖（装完自动拉起新版本），
    /// exe 安装包则静默安装。这里只负责把状态摆好、失败时把按钮点回来。
    /// </summary>
    private void StartUpdateInstall()
    {
        if (_updating || _updateRelease is not { } release) return;

        // 发行版里没挂更新包：没法自动装，直接把发行版页面打开让用户自己下
        if (string.IsNullOrWhiteSpace(release.DownloadUrl))
        {
            StatusText.Text = "这个版本没有可下载的更新包，已打开发行版页面";
            OpenExternal(release.PageUrl);
            return;
        }

        _updating = true;
        CheckUpdateButton.IsEnabled = false;
        StatusText.Text = "开始下载更新包…";

        // 让状态栏先刷出来，再进 AutoUpdater 的模态下载窗口（它会一直占着 UI 线程）
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            bool started;
            try
            {
                started = UpdateService.Apply(release, this);
            }
            catch (Exception ex)
            {
                _updating = false;
                CheckUpdateButton.IsEnabled = true;
                StatusText.Text = $"更新失败：{ex.Message}";
                return;
            }

            if (!started)
            {
                // 用户在下载窗口里点了取消
                _updating = false;
                CheckUpdateButton.IsEnabled = true;
                StatusText.Text = "已取消更新";
                return;
            }

            StatusText.Text = "更新包已就位，正在重启完成安装…";
            ExitForUpdate();
        }));
    }

    /// <summary>
    /// 更新前的退出：AutoUpdater 的解压器会等本进程完全退出后才替换文件，
    /// 所以这里不能走「收进托盘」，必须真退（留半秒把状态栏刷出来）。
    /// </summary>
    private void ExitForUpdate()
    {
        _exitRequested = true;
        if (_trayIcon is not null) _trayIcon.Visible = false;

        try { _syncCts?.Cancel(); } catch { }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            System.Windows.Application.Current.Shutdown();
        };
        timer.Start();
    }

    // ==================== 灵石结缘 · 随喜投喂 ====================

    /// <summary>每次开关自增，用来识别"关闭动画播到一半又被重新打开"的情况。</summary>
    private int _supportAnimToken;

    /// <summary>流光只在第一次打开弹窗时挂一次（省得程序一启动就白白转两圈动画）。</summary>
    private bool _supportPostersReady;

    private void Support_Click(object sender, RoutedEventArgs e) => ShowSupport();

    /// <summary>第一次打开弹窗时把两张收款码读进画笔；读不到就把对应那格收掉。</summary>
    private void EnsureSupportPosters()
    {
        if (_supportPostersReady) return;

        _supportPostersReady = true;

        if (!LoadPoster(AlipayBrush, "Assets/alipay.jpeg")) AlipayFrame.Visibility = Visibility.Collapsed;
        if (!LoadPoster(WxpayBrush, "Assets/wxpay.jpeg")) WxpayFrame.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 把收款码读进画笔。读不到就当作没有这张图（返回 false，调用方把整块收掉），
    /// 绝不让「图片资源没打进程序集」变成启动就崩。
    /// </summary>
    private static bool LoadPoster(ImageBrush brush, string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/{path}", UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;   // 立刻解码，缺资源在这里就能抛出来
            image.EndInit();
            image.Freeze();

            brush.ImageSource = image;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[灵石结缘] 收款码加载失败 {path}：{ex.Message}");
            return false;
        }
    }

    /// <summary>结缘弹窗的出入场节奏与「关于」弹窗保持一致（回弹开、缩一点收）。</summary>
    private void ShowSupport()
    {
        ++_supportAnimToken;
        SupportLayer.Visibility = Visibility.Visible;
        EnsureSupportPosters();

        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        var spring = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };

        SupportBackdrop.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(240))) { EasingFunction = fade });

        SupportCard.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170))) { EasingFunction = fade });

        SupportScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.93, 1, new Duration(AboutOpenTime)) { EasingFunction = spring });
        SupportScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.93, 1, new Duration(AboutOpenTime)) { EasingFunction = spring });

        SupportShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, new Duration(AboutOpenTime)) { EasingFunction = fade });
    }

    private void CloseSupport()
    {
        if (SupportLayer.Visibility != Visibility.Visible)
        {
            return;
        }

        var token = ++_supportAnimToken;
        var fade = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(AboutCloseTime);

        var cardFade = new DoubleAnimation(1, 0, duration) { EasingFunction = fade };

        // Completed 必须挂在 BeginAnimation 之前（同「关于」弹窗的注释）
        cardFade.Completed += (_, _) =>
        {
            if (token != _supportAnimToken)
            {
                return;
            }

            SupportLayer.Visibility = Visibility.Collapsed;
            SupportCard.BeginAnimation(OpacityProperty, null);
            SupportBackdrop.BeginAnimation(OpacityProperty, null);
            SupportScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            SupportScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            SupportShift.BeginAnimation(TranslateTransform.YProperty, null);
        };

        SupportCard.BeginAnimation(OpacityProperty, cardFade);
        SupportScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        SupportScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        SupportShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, 6, duration) { EasingFunction = fade });
        SupportBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration) { EasingFunction = fade });
    }

    private void SupportClose_Click(object sender, RoutedEventArgs e) => CloseSupport();

    private void SupportBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseSupport();
}
