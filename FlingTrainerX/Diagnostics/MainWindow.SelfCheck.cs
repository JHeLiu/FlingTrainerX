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
using FlingTrainerX.Interop;
using FlingTrainerX.Views;

namespace FlingTrainerX;

/// <summary>
/// 主窗口的一部分：开发期自检入口（仅 Debug 编译）。
/// </summary>
public partial class MainWindow
{
    // ==================== 自检兼容层 ====================
    //
    // 业务状态已经搬进 ViewModel / 服务里了，自检代码仍然按老名字读状态。
    // 这里统一转一层：自检不用为了「字段搬家」整篇改写，正式代码也不会用到这些。

#if DEBUG
    /// <summary>当前页的条目（只读）。</summary>
    private IReadOnlyList<TrainerItem> _items => Library.Items;

    /// <summary>内存里那份库（只读）。</summary>
    private TrainerLibrary _library => _catalog.Library;

    /// <summary>收藏夹（只读）。</summary>
    private List<TrainerItem> _favorites => _catalog.Favorites;

    /// <summary>游戏库分页进度。</summary>
    private PageCursor _libraryPager => Library.LibraryPager;

    /// <summary>搜索分页进度。</summary>
    private PageCursor _searchPager => Library.SearchPager;

    /// <summary>首屏是否还在读本地库。</summary>
    private bool _firstLoading => Library.IsFirstLoading;

    /// <summary>首屏读库失败的原因（没有就是空串）。</summary>
    private string _firstError => Library.FirstError ?? "";

    /// <summary>当前生效的搜索词。</summary>
    private string _keyword => Library.Keyword;

    /// <summary>这次搜索结果是不是来自本地。</summary>
    private bool _searchFromLocal => Library.SearchFromLocal;

    /// <summary>当前显示哪一块。</summary>
    private ViewMode _mode => Library.Mode;

    /// <summary>上一次清缓存 + 重载是否走完。</summary>
    private bool _resetDone => _cacheReset.IsDone;

    /// <summary>搜索提示列表。</summary>
    private ObservableCollection<GameNameRow> _suggests => Library.Suggestions;

    /// <summary>是否正在跑更新（自检会假装置忙）。</summary>
    private bool _syncRunning
    {
        get => Sync.IsRunning;
        set => Sync.IsRunning = value;
    }

    /// <summary>更新任务的取消源（自检会假装开一个）。</summary>
    private CancellationTokenSource? _syncCts
    {
        get => Sync.Cts;
        set => Sync.Cts = value;
    }

    /// <summary>托盘菜单窗口。</summary>
    private TrayMenuWindow? _trayMenu => _tray.Menu;

    /// <summary>托盘图标。</summary>
    private WinForms.NotifyIcon? _trayIcon => _tray.Icon;

    /// <summary>托盘图标在首帧之后已经建好，自检不用再催。</summary>
    private void EnsureTray() => _tray.EnsureCreated("风灵月影宗藏宝阁");

    private void CloseSuggest() => Library.CloseSuggest();

    private void MoveSuggest(int delta) => Library.MoveSuggest(delta);

    // ---- Win32 薄包装：真实现搬到了 Interop/WindowChrome ----

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    private static void SetCursorPos(int x, int y) => WindowChrome.SetCursorPos(x, y);

    private static bool GetWindowRect(IntPtr hwnd, out WindowChrome.PixelRect rect) => WindowChrome.TryGetWindowRect(hwnd, out rect);

    private static uint GetDpiForWindow(IntPtr hwnd) => WindowChrome.DpiOf(hwnd);

    private static bool IsWindowVisible(IntPtr hwnd) => WindowChrome.IsHwndVisible(hwnd);

    private static bool SetForegroundWindow(IntPtr hwnd) => WindowChrome.BringToFront(hwnd);

    private static IntPtr GetForegroundWindow() => WindowChrome.ForegroundWindow;

    private static void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra) =>
        WindowChrome.SendMouse(flags, dx, dy, data, extra);

    /// <summary>屏幕坐标点（老代码按这个名字构造）。</summary>
    private struct Point32
    {
        public int X;
        public int Y;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private static IntPtr MonitorFromPoint(Point32 point, uint flags) =>
        WindowChrome.MonitorHandleAtPoint(point.X, point.Y);

    private static int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY) =>
        WindowChrome.DpiOfMonitor(monitor, out dpiX, out dpiY);

    private static bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags) =>
        WindowChrome.MoveWindow(hwnd, x, y, flags);

    private static IntPtr PopupHandle(System.Windows.Controls.Primitives.Popup popup) =>
        WindowChrome.PopupHandle(popup);

    private void OnWindowLocationChanged(object? sender, EventArgs e) => TrackOpenPopups();

    // ---- 更新弹框：自检要直接摆进度 ----

    private bool SyncDialogOpen => Sync.IsOpen;

    private void ShowSyncDialog(SyncKind kind, bool withSteam = true) => Sync.ShowDialog(kind, withSteam);

    private void CloseSyncDialog() => Sync.CloseDialog();

    private Task<NameLaneResult> RunNameLaneAsync(bool withSteam, CancellationToken ct) =>
        Sync.RunNameLaneAsync(withSteam, ct);

    private void OnSyncProgress(SyncProgress p) => Sync.ApplyProgress(p);

    private const double DetailScaleFrom = 0.96;

    private void ShowNameStageRunning(int total) => Sync.ShowNameStageRunning(total);

    private void ShowNameStageProgress(int done, int total, string english, string chinese) =>
        Sync.ShowNameStageProgress(done, total, english, chinese);

    private void OnFavoriteChanged(TrainerItem item, bool favorite) => Library.ApplyFavorite(item, favorite);
#endif

#if DEBUG
    // ==================== 开发期自检（--capture，仅 Debug 编译） ====================

    /// <summary>
    /// 托盘自检：模拟「启动程序成功 → 收进托盘」、拍托盘右键菜单、再从托盘恢复主界面，
    /// 把每一步的窗口状态写进 tray-state.txt，顺带整屏截一张看托盘图标在不在。
    /// </summary>
    public async Task RunTrayCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "tray-state.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(1500);

        void Note(string step)
        {
            // OS 级可见性：只有它是 False，才算真的「跟系统隐藏一样」，任务栏按钮才会消失
            var hwnd = new WindowInteropHelper(this).Handle;
            var osVisible = hwnd != IntPtr.Zero && IsWindowVisible(hwnd);

            File.AppendAllText(log,
                $"{step}: OsVisible={osVisible} IsVisible={IsVisible} State={WindowState} " +
                $"InTaskbar={ShowInTaskbar} TrayIconVisible={_trayIcon?.Visible}\n");
        }

        Note("01-startup");

        // 1) 详情页点「启动程序」：真的启动一个 exe，成功后主界面应自动缩进托盘
        var item = _library.Items.FirstOrDefault(i => i.Groups.Count > 0) ?? _library.Items.FirstOrDefault();
        if (item is not null)
        {
            OpenDetail(item);
            await Task.Delay(900);
        }

        // 借 cmd.exe 当「修改器」：复制一份改名，启动后按名字精确回收，不碰用户的进程
        var beforeW = ActualWidth;
        var beforeH = ActualHeight;

        var fake = Path.Combine(dir, "TestTrainer.exe");
        var launchedByName = "TestTrainer";
        try
        {
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fake, true);
            if (DetailHost.Content is TrainerDetailView launcher) launcher.LaunchFile(fake);
            else HideForLaunchedApp();
        }
        catch
        {
            HideForLaunchedApp();
        }

        await Task.Delay(1800);
        Note("02-after-launch-hide");

        foreach (var process in System.Diagnostics.Process.GetProcessesByName(launchedByName))
        {
            try { process.Kill(); } catch { /* 已经退了 */ }
        }

        // 2) 右键托盘图标弹出的菜单
        EnsureTray();
        _trayMenu!.ShowAtCursor();
        await Task.Delay(700);
        SaveShotOf(_trayMenu, Path.Combine(dir, "tray-menu.png"));

        // 把鼠标挪到第一项上，拍一张悬停态（拍完还原鼠标位置）
        var saved = WinForms.Cursor.Position;
        var hoverPoint = _trayMenu.PointToScreen(new System.Windows.Point(70, 42));
        SetCursorPos((int)hoverPoint.X, (int)hoverPoint.Y);
        await Task.Delay(400);
        SaveShotOf(_trayMenu, Path.Combine(dir, "tray-menu-hover.png"));
        SetCursorPos(saved.X, saved.Y);

        CaptureScreen(Path.Combine(dir, "tray-screen.png"));
        _trayMenu.Hide();
        await Task.Delay(300);

        // 3) 菜单里的「打开主界面」
        ShowMainWindow();
        await Task.Delay(900);
        Note("03-after-open-main");
        File.AppendAllText(log, $"窗口尺寸 {beforeW:F0}x{beforeH:F0} → {ActualWidth:F0}x{ActualHeight:F0}\n");
        SaveShot(Path.Combine(dir, "tray-restored.png"));
    }

    /// <summary>
    /// 自检：先藏进托盘，再从托盘「退出程序」——验证隐藏状态下点退出，进程真的会退干净
    /// （隐藏窗口如果挡住了关窗流程，这条命令就会一直挂着）。
    /// </summary>
    public async Task RunTrayExitCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "tray-exit.txt");
        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(800);

        HideToTray();
        EnsureTray();
        var hwnd = new WindowInteropHelper(this).Handle;
        File.AppendAllText(log,
            $"hidden: OsVisible={(hwnd != IntPtr.Zero && IsWindowVisible(hwnd))} TrayIconVisible={_trayIcon?.Visible}\n");

        await Task.Delay(600);
        ExitFromTray();
        File.AppendAllText(log, "exit-called\n");
    }

    /// <summary>
    /// 自检：收藏夹 / 设置页的版式，以及「一键清除缓存」整条链路
    /// （点按钮 → 弹确认框 → 点确定 → 真清 SQLite + 真删 f 目录）。
    /// </summary>
    public async Task RunClearCacheCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "clear.txt");
        File.WriteAllText(log, "");

        // 这个自检会真的清空本地库和 f 目录（图片没法恢复）。默认直接拒绝，
        // 必须显式设 FTX_ALLOW_CACHE_WIPE=1 才跑；正式验证请在拷贝出来的目录里跑。
        if (Environment.GetEnvironmentVariable("FTX_ALLOW_CACHE_WIPE") != "1")
        {
            File.AppendAllText(log, "已跳过：该自检会清空本地库与 f 目录，需设 FTX_ALLOW_CACHE_WIPE=1 才执行\n");
            return;
        }

        // 再兜一层：清除前先把数据库备份一份（图片目录太大，只保数据库）
        try
        {
            var db = AppPaths.DatabaseFile;
            if (File.Exists(db))
                File.Copy(db, Path.Combine(AppPaths.DbDir, $"fling.db.bak-{DateTime.Now:yyyyMMdd-HHmmss}"), true);
        }
        catch
        {
            // 备份失败不阻断自检，但会写进日志
            File.AppendAllText(log, "警告：数据库备份失败\n");
        }

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(1500);

        // 首屏的封面还在陆续落盘，先等它们安静下来（正常使用时点按钮也是这个时机）
        for (var i = 0; i < 60 && DownloadManager.Instance.ActiveCount > 0; i++) await Task.Delay(100);
        await Task.Delay(2500);

        // 先造两个假文件，验证 f 目录真的被清空（含子目录与根目录下的散文件）
        var probe = AppPaths.FilesFor(990001);
        File.WriteAllText(Path.Combine(probe, "probe.bin"), new string('x', 4096));
        File.WriteAllText(Path.Combine(AppPaths.FilesDir, "loose.txt"), "loose");

        NavList.SelectedIndex = 1;               // 收藏夹
        await Task.Delay(700);
        SaveShot(Path.Combine(dir, "page-favorites.png"));

        NavList.SelectedIndex = 3;               // 设置
        await Task.Delay(700);
        SaveShot(Path.Combine(dir, "page-settings.png"));

        var rowsBefore = AppDatabase.Instance.CacheCount();
        var filesBefore = Directory.GetFiles(AppPaths.FilesDir, "*", SearchOption.AllDirectories).Length;
        var bytesBefore = Directory.GetFiles(AppPaths.FilesDir, "*", SearchOption.AllDirectories)
            .Sum(p => new FileInfo(p).Length);

        PressButton(ClearCacheButton);
        await Task.Delay(600);
        SaveShot(Path.Combine(dir, "confirm.png"));

        PressButton(ConfirmOkButton);

        // 按钮恢复可用 = 清除流程（含多轮扫尾 + 解除静音）真正跑完，最多等 12 秒
        for (var i = 0; i < 120 && !ClearCacheButton.IsEnabled; i++) await Task.Delay(100);

        // 再等「清完缓存 + 重读本地库」这套流程整走完，否则量到的是中间态。
        // 注意先等一会儿：点「确定」只是把确认框关掉，真正的清除是接着才在调度器上跑起来的。
        await Task.Delay(1000);

        var timeline = new System.Text.StringBuilder();
        for (var i = 0; i < 400; i++)
        {
            if (i % 10 == 0)
                timeline.Append($"{i * 100}ms resetDone={_resetDone} firstLoading={_firstLoading} " +
                                $"items={_library.Items.Count} cache={AppDatabase.Instance.CacheCount()}\n");
            if (_resetDone && !_firstLoading) break;
            await Task.Delay(100);
        }
        await Task.Delay(1200);   // 解除静音后再观察一会儿，确认没有文件被重新写回来

        var rowsAfter = AppDatabase.Instance.CacheCount();
        var filesAfter = Directory.GetFiles(AppPaths.FilesDir, "*", SearchOption.AllDirectories).Length;
        var dirsAfter = Directory.GetDirectories(AppPaths.FilesDir).Length;

        var statusOnSettings = StatusText.Text;

        // 回到游戏库，看「已经收录」这个数字到底取的是哪一份数据
        NavList.SelectedIndex = 0;
        await Task.Delay(700);
        var subtitle = SectionSubtitle.Text;
        var statusOnLibrary = StatusText.Text;

        File.AppendAllText(log,
            $"清除前：缓存 {rowsBefore} 条 / f 目录 {filesBefore} 个文件（{DownloadTask.Human(bytesBefore)}）\n" +
            $"清除后：缓存 {rowsAfter} 条 / f 目录 {filesAfter} 个文件 / {dirsAfter} 个子目录\n" +
            $"确认框：{(ConfirmLayer.Visibility == Visibility.Visible ? "仍打开" : "已关闭")}\n" +
            $"设置页状态栏：{statusOnSettings}\n" +
            $"游戏库副标题：{subtitle}\n" +
            $"游戏库状态栏：{statusOnLibrary}\n" +
            $"内存条数：{_library.Items.Count} / SQLite：{AppDatabase.Instance.CacheCount()}\n" +
            $"加载状态：firstLoading={_firstLoading} page={_libraryPager.Page}/{_libraryPager.TotalPages} " +
            $"total={_libraryPager.TotalCount} loading={_libraryPager.Loading}\n" +
            $"错误：{_firstError ?? "无"}\n" +
            $"时间线：\n{timeline}");

        SaveShot(Path.Combine(dir, "after-clear.png"));
    }

    /// <summary>
    /// 自检：第二次启动时那个「已经启动」提示框，单独拍一张图看版式。
    /// </summary>
    public async Task RunTipCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        Hide();
        await Task.Delay(400);

        var tip = new AlreadyRunningWindow();
        tip.Show();
        await Task.Delay(700);
        SaveShotOf(tip, Path.Combine(dir, "already-running.png"));

        tip.Close();
        await Task.Delay(200);
    }

    /// <summary>
    /// 自检：详情页的开关动画。验证三件事——开场缩放/淡入真的在动、动画播完能清掉位图缓存
    /// 接着把选项加载出来、点遮罩能关掉且关完真的收了层（不是"透明但挡着点击"）。
    /// </summary>
    /// <summary>
    /// 自检：字号统一到 12/14 之后，把几处文字最密的界面各拍一张，
    /// 人眼过一遍有没有被挤坏（标题截断、列宽不够、按钮顶边）。
    /// </summary>
    public async Task RunFontCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "font.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(1500);

        // 本地库空的就先搜一把（搜过的会写回本地库，之后再搜就不联网了）
        if (_items.Count == 0)
        {
            SearchBox.Text = "assassin";
            await SubmitSearchAsync();
            await Task.Delay(1500);
        }

        File.AppendAllText(log, $"卡片 {_items.Count} 条，卡片尺寸 {_cardW:0}x{_cardH:0}\n");
        SaveShot(Path.Combine(dir, "01-library.png"));

        var item = _items.FirstOrDefault();
        if (item is null)
        {
            File.AppendAllText(log, "没有数据，跳过详情页\n");
            return;
        }

        OpenDetail(item);
        await Task.Delay(900);
        if (DetailHost.Content is TrainerDetailView view)
        {
            SaveShot(Path.Combine(dir, "02-shots.png"));

            view.ShowOptionsTab();
            await Task.Delay(500);
            SaveShot(Path.Combine(dir, "03-options.png"));

            view.ShowDownloadTab();
            await Task.Delay(700);
            SaveShot(Path.Combine(dir, "04-downloads.png"));

            // 加载遮层（详情页联网取数据时的样子）
            File.AppendAllText(log, $"加载遮层可见 {view.ShowLoadingForCheck()}\n");
            await Task.Delay(300);
            SaveShot(Path.Combine(dir, "05-overlay.png"));
        }

        CloseDetail();
        await Task.Delay(400);

        ShowAbout();
        await Task.Delay(900);
        SaveShot(Path.Combine(dir, "06-about.png"));
        CloseAbout();
        await Task.Delay(300);

        File.AppendAllText(log, "已出图：游戏库 / 详情三页 / 关于弹窗\n");
    }

    public async Task RunDetailCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "detail.txt");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        await Task.Delay(600);

        var item = _library.Items[0];
        var timeline = new System.Text.StringBuilder();

        // ---- 开场：从 0.96 起跳，看缩放/透明度随时间怎么走 ----
        OpenDetail(item);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var peak = 0.0;
        var openedAt = -1L;
        for (var i = 0; i < 120; i++)
        {
            var s = DetailScale.ScaleX;
            peak = Math.Max(peak, s);
            timeline.Append($"{watch.ElapsedMilliseconds,4}ms scale={s:0.0000} op={DetailHost.Opacity:0.00} " +
                            $"mask={DetailBackdrop.Opacity:0.00} cache={(DetailHost.CacheMode is null ? "无" : "有")}\n");

            // 位图缓存被清掉 = 动画真的播完、Completed 回调真的跑了（先启动后挂回调的话这里永远是"有"）
            if (openedAt < 0 && DetailHost.CacheMode is null && s > 0.999)
            {
                openedAt = watch.ElapsedMilliseconds;
                break;
            }

            await Task.Delay(20);
        }

        await Task.Delay(300);
        SaveShot(Path.Combine(dir, "01-detail-open.png"));

        // ---- 右侧按钮：能启动时要换成蓝色主按钮，不能启动时是灰底幽灵按钮 ----
        var launchState = "跳过了（没进详情页）";
        if (DetailHost.Content is TrainerDetailView dvOpen &&
            dvOpen.DataContext is TrainerDetailVM vmOpen &&
            dvOpen.FindName("LaunchButton") is Button launchBtn)
        {
            var wasEnabled = vmOpen.LaunchEnabled;

            var disabledFill = ButtonFill();
            vmOpen.LaunchEnabled = true;
            await Task.Delay(150);
            launchBtn.UpdateLayout();
            var enabledFill = ButtonFill();
            SaveShot(Path.Combine(dir, "01b-launch-primary.png"));

            vmOpen.LaunchEnabled = wasEnabled;
            await Task.Delay(100);

            launchState =
                $"不能启动时底色 {disabledFill}｜能启动时底色 {enabledFill}" +
                $"（{(enabledFill == "#FF3B82F6" ? "主题色（对）" : "不是主题色（错）")}，" +
                $"{(disabledFill != enabledFill ? "两种状态有区分（对）" : "两种状态一样（错）")}）";

            string ButtonFill()
            {
                // 模板里那块填充叫 b；摸它的 Background 才知道 DataTrigger 有没有真的把模板换掉
                var fill = launchBtn.Template?.FindName("b", launchBtn) as Border;
                return fill?.Background is SolidColorBrush brush ? brush.Color.ToString() : "(未知)";
            }
        }

        // ---- 遮层：本地已有数据进来不该有遮层；点刷新 → 出现 → 抓完自动收 ----
        var overlayOnLocal = item.HasDownloads &&
                             DetailHost.Content is TrainerDetailView dv0 && dv0.OverlayVisible;
        var overlayRefresh = "跳过了（没进详情页）";
        if (DetailHost.Content is TrainerDetailView dv1)
        {
            dv1.ReloadForCheck();
            var shown = false;
            for (var i = 0; i < 60 && !shown; i++)
            {
                shown = dv1.OverlayVisible;
                if (!shown) await Task.Delay(50);
            }

            var hideWatch = System.Diagnostics.Stopwatch.StartNew();
            var hideMs = -1;
            for (var i = 0; i < 600; i++)
            {
                if (!dv1.OverlayVisible) { hideMs = (int)hideWatch.ElapsedMilliseconds; break; }
                await Task.Delay(50);
            }

            overlayRefresh = $"刷新后遮层{(shown ? "打开（对）" : "没打开（错）")}、" +
                             (hideMs >= 0 ? $"{hideMs}ms 后收起（对）" : "30 秒内没收起（错）");
        }

        // ---- 卡片中心点落在谁身上（应该落在卡片里的元素，而不是遮罩）----
        var center = new Point(DetailHost.ActualWidth / 2, DetailHost.ActualHeight / 2);
        var hit = VisualTreeHelper.HitTest(DetailHost, center);
        var hitName = hit?.VisualHit is null ? "(null)" : hit.VisualHit.GetType().Name;
        var cardOwnsCenter = hit?.VisualHit is not null &&
                             (hit.VisualHit is not System.Windows.Shapes.Rectangle r || r.Name != "DetailBackdrop");

        // ---- 关掉：等 500ms 再看是不是真的收层了 ----
        var maskClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent
        };
        DetailBackdrop.RaiseEvent(maskClick);
        await Task.Delay(500);

        var collapsed = DetailLayer.Visibility != Visibility.Visible;
        var cleared = DetailHost.Content is null;
        var resetOpacity = Math.Abs(DetailHost.Opacity) < 0.01;
        SaveShot(Path.Combine(dir, "02-detail-closed.png"));

        // ---- 再开一次：确认收层后还能正常打开（动画值有没有被清回初始态）----
        OpenDetail(item);
        await Task.Delay(500);
        var reopened = DetailLayer.Visibility == Visibility.Visible && DetailHost.Opacity > 0.99 &&
                       Math.Abs(DetailScale.ScaleX - 1) < 0.002;

        File.WriteAllText(log,
            $"卡片中心命中：{hitName}（{(cardOwnsCenter ? "是卡片内容（对）" : "是遮罩（错）")}）\n" +
            $"启动按钮：{launchState}\n" +
            $"本地数据进详情：{(overlayOnLocal ? "出现了遮层（错）" : "没有遮层（对）")}\n" +
            $"点刷新：{overlayRefresh}\n" +
            $"开场动画落定：{openedAt}ms（>0 即 Completed 已触发并清掉了位图缓存）\n" +
            $"缩放峰值：{peak:0.0000}（起点 {DetailScaleFrom}，应 >1 表示有回弹）\n" +
            $"点遮罩后：{(collapsed ? "已收层（对）" : "仍显示（错）")} / " +
            $"内容{(cleared ? "已释放" : "未释放（错）")} / 透明度{(resetOpacity ? "已归零" : $"={DetailHost.Opacity:0.00}（错）")}\n" +
            $"再次打开：{(reopened ? "正常（动画值已回到初始态）" : "异常")}\n" +
            $"时间线：\n{timeline}");
    }

    /// <summary>
    /// 自检：确认框的遮罩点击。真的发一次鼠标点击（不是 RaiseEvent），
    /// 验证「点遮罩 = 取消」「点卡片 = 不关」，并把每一步写进 mask.txt。
    /// </summary>
    public async Task RunMaskCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "mask.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(1200);

        NavList.SelectedIndex = 3;               // 设置
        await Task.Delay(700);
        PressButton(ClearCacheButton);
        await Task.Delay(700);
        SaveShot(Path.Combine(dir, "01-confirm.png"));

        // 遮罩上的取样点：窗口右下角，离卡片很远，一定是「弹框外面」
        var maskPoint = new Point(ActualWidth - 70, ActualHeight - 110);
        LogHit(maskPoint, "遮罩取样点");

        // 先点卡片内部的空白（正文那行），弹框应该无动于衷
        var bodyPoint = Center(ConfirmBody);
        File.AppendAllText(log, $"卡片内取样点 {bodyPoint} → {HitName(bodyPoint)}\n");
        await ClickAsync(bodyPoint);
        await Task.Delay(400);
        File.AppendAllText(log, $"点卡片内部后：{(ConfirmLayer.Visibility == Visibility.Visible ? "仍打开（对）" : "被关掉了（错）")}\n");
        SaveShot(Path.Combine(dir, "02-after-card-click.png"));

        // 再点遮罩，弹框应当关闭
        var maskScreen = PointToScreen(maskPoint);
        File.AppendAllText(log,
            $"真点会落在：{DescribeHit(InputHitTest(maskPoint))}｜窗口激活={IsActive}｜" +
            $"前台是本窗口={(GetForegroundWindow() == new WindowInteropHelper(this).Handle ? "是" : "否")}｜" +
            $"屏幕点 {maskScreen.X:F0},{maskScreen.Y:F0}｜VM.IsOpen={VM.Confirm.IsOpen}\n");
        await ClickAsync(maskPoint);
        await Task.Delay(600);
        File.AppendAllText(log,
            $"点遮罩后：{(ConfirmLayer.Visibility == Visibility.Visible ? "仍打开（错）" : "已关闭（对）")}" +
            $"｜VM.IsOpen={VM.Confirm.IsOpen}\n");
        SaveShot(Path.Combine(dir, "03-after-mask-click.png"));

        // 重开一次，确认连点遮罩两下也不会把确认框卡住
        PressButton(ClearCacheButton);
        await Task.Delay(600);
        await ClickAsync(new Point(ActualWidth - 70, 90));       // 这次点右上角
        await Task.Delay(500);
        File.AppendAllText(log, $"第二次开框后点右上遮罩：{(ConfirmLayer.Visibility == Visibility.Visible ? "仍打开（错）" : "已关闭（对）")}\n");

        // 对照组：真点「取消」按钮。这一下能关掉，说明鼠标事件本身是通的，
        // 上面遮罩没关就只可能是遮罩的命中/处理器问题，而不是自检点空了
        if (ConfirmLayer.Visibility == Visibility.Visible)
        {
            var cancelPoint = Center(ConfirmCancelButton);
            File.AppendAllText(log, $"取消按钮取样点 {cancelPoint} → {HitName(cancelPoint)}\n");
            await ClickAsync(cancelPoint);
            await Task.Delay(600);
            File.AppendAllText(log,
                $"点「取消」后：{(ConfirmLayer.Visibility == Visibility.Visible ? "仍打开（错）" : "已关闭（对）")}" +
                $"｜VM.IsOpen={VM.Confirm.IsOpen}\n");
        }

        string HitName(Point p)
        {
            var hit = VisualTreeHelper.HitTest(ConfirmLayer, p);
            DependencyObject? v = hit?.VisualHit;
            while (v is not null && !(v is FrameworkElement)) v = VisualTreeHelper.GetParent(v);
            return v is FrameworkElement fe ? $"{fe.GetType().Name}({fe.Name})" : "null";
        }

        // 真正会被点到的是哪一层：把整窗的命中结果也写一笔，方便分清「有东西挡住」和「点了没反应」
        string DescribeHit(IInputElement? element)
        {
            var v = element as DependencyObject;
            while (v is not null && !(v is FrameworkElement)) v = VisualTreeHelper.GetParent(v);
            return v is FrameworkElement fe ? $"{fe.GetType().Name}({fe.Name})" : "null";
        }

        void LogHit(Point p, string label) =>
            File.AppendAllText(log, $"{label} {p} → {HitName(p)}\n");

        Point Center(FrameworkElement element)
        {
            var local = new Point(element.ActualWidth / 2, element.ActualHeight / 2);
            return element.TranslatePoint(local, ConfirmLayer);
        }
    }

    /// <summary>
    /// 自检：「数据更新」的设置卡片和同步弹框。不联网，进度是喂进去的假数据，
    /// 重点验证三件事——排版正常、点遮层关不掉、按 Esc 也关不掉（只能取消）。
    /// </summary>
    public async Task RunSyncUiCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "sync-ui.txt");
        File.WriteAllText(log, "");

        await Task.Delay(1500);

        NavList.SelectedIndex = 3;               // 设置
        await Task.Delay(900);
        SaveShot(Path.Combine(dir, "01-settings.png"));

        File.AppendAllText(log,
            $"设置页可见={SettingsPanel.Visibility == Visibility.Visible} " +
            $"同步全部按钮={(SyncAllButton.IsEnabled ? "可点" : "不可点")}\n");

        // 左侧菜单：点哪一项只显示那一组，其余必须收起
        var groups = new[] { SettingsGroupData, SettingsGroupGeneral, SettingsGroupAbout, SettingsGroupDonate };
        for (var i = 0; i < 4; i++)
        {
            SettingsMenu.SelectedIndex = i;
            await Task.Delay(320);
            var vis = string.Join(",", groups
                .Select(g => g.Visibility == Visibility.Visible ? "1" : "0"));
            File.AppendAllText(log,
                $"菜单{i}：选中={SettingsMenu.SelectedIndex} 四组显隐={vis}" +
                $"（应为第 {i} 位为 1，其余 0） 偏移={SettingsScroll.VerticalOffset:0}\n");
        }
        SaveShot(Path.Combine(dir, "01b-settings-about.png"));

        SettingsMenu.SelectedIndex = 0;
        await Task.Delay(400);

        // 直接开弹框喂假进度：渲染路径和真实同步完全一样
        _syncRunning = true;
        ShowSyncDialog(SyncKind.All);
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "02-sync-open.png"));

        Feed(SyncPhase.Syncing, "Assassin's Creed Mirage", "第 3 / 52 页 · 正在更新", 45, 780, 0.06, 43, 2);
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "03-sync-progress.png"));

        File.AppendAllText(log,
            "—— 第一条（游戏数据）——\n" +
            $"进度行=「{SyncGameText.Text}」 说明=「{SyncPhaseText.Text}」 " +
            $"计数=「{SyncCountText.Text}」 百分比=「{SyncPercentText.Text}」 " +
            $"进度条不确定态={SyncBar.IsIndeterminate}\n" +
            $"—— 第二条（中文名）——\n" +
            $"状态=「{SyncNameStateText.Text}」 明细=「{SyncNameDetailText.Text}」 " +
            $"计数=「{SyncNameCountText.Text}」 百分比=「{SyncNamePercentText.Text}」 " +
            $"进度条不确定态={SyncNameBar.IsIndeterminate}\n");

        // 喂一段「正在同步中文名」的进度：第二条进度条要跟着动，名字行按「英文 >> 中文」显示
        ShowNameStageRunning(767);
        ShowNameStageProgress(128, 767, "The Legend of Heroes: Trails through Daybreak", "英雄传说 黎之轨迹");
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "03b-sync-name.png"));

        File.AppendAllText(log,
            $"中文名阶段：第二条=「{SyncNameStateText.Text}」 {SyncNameCountText.Text} {SyncNamePercentText.Text}｜" +
            $"名字行=「{SyncNamePairText.Text}」（{(SyncNamePairText.Visibility == Visibility.Visible ? "显示（对）" : "藏着（错）")}）\n");

        // 再喂一条「Steam 上没中文名」的：那一截要写「无中文名」，不能只留一个破折号
        ShowNameStageProgress(129, 767, "Sins of a Solar Empire II", "");
        await Task.Delay(400);
        SaveShot(Path.Combine(dir, "03c-sync-no-cn.png"));

        File.AppendAllText(log,
            $"没中文名那款：名字行=「{SyncNamePairText.Text}」" +
            $"（应含「无中文名」，实际{(SyncNamePairText.Text.Contains("无中文名") ? "对" : "错")}）\n");

        // 回到「等待中文名」的初始样子，后面测遮层 / Esc 的截图才干净
        Sync.Names.Waiting("正在读取待补中文名的游戏…");
        await Task.Delay(300);

        // 1) 点遮层（窗口右下角，离卡片很远）：弹框必须纹丝不动
        var backdrop = new Point(ActualWidth - 70, ActualHeight - 110);
        await ClickAsync(backdrop);
        await Task.Delay(400);
        File.AppendAllText(log,
            $"点遮层后：{(SyncDialogOpen ? "仍打开（对）" : "被关掉了（错）")}\n");
        SaveShot(Path.Combine(dir, "04-after-mask-click.png"));

        // 2) 按 Esc：同样不该关
        var esc = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        RaiseEvent(esc);
        await Task.Delay(300);
        File.AppendAllText(log,
            $"按 Esc 后：{(SyncDialogOpen ? "仍打开（对）" : "被关掉了（错）")} Esc 被吃掉={esc.Handled}\n");

        // 3) 没有正在跑的任务时点「取消同步」应当什么都不发生（不能抛异常 / 不能关框）
        PressButton(SyncCancelButton);
        await Task.Delay(300);
        File.AppendAllText(log,
            $"无任务时点取消：{(SyncDialogOpen ? "仍打开（对）" : "被关掉了（错）")}\n");

        // 收尾
        CloseSyncDialog();
        await Task.Delay(400);

        // 灵石结缘弹窗：设置页入口 → 打开截图 → Esc 关闭
        PressButton(SupportButton);
        await Task.Delay(600);
        SaveShot(Path.Combine(dir, "05-support.png"));
        File.AppendAllText(log, $"结缘弹窗可见={SupportLayer.Visibility == Visibility.Visible}\n");

        var esc2 = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        RaiseEvent(esc2);
        await Task.Delay(400);
        File.AppendAllText(log,
            $"按 Esc 后结缘弹窗={(SupportLayer.Visibility == Visibility.Visible ? "仍打开（错）" : "已关闭（对）")}\n");
        _syncRunning = false;
        await Task.Delay(400);
        SaveShot(Path.Combine(dir, "05-closed.png"));
        File.AppendAllText(log, $"收尾后弹框可见={SyncDialogOpen}\n");

        void Feed(SyncPhase phase, string game, string detail, int done, int total,
            double ratio, int updated, int skipped)
        {
            OnSyncProgress(new SyncProgress
            {
                Phase = phase,
                Game = game,
                Detail = detail,
                Done = done,
                Total = total,
                Ratio = ratio,
                Updated = updated,
                Skipped = skipped
            });
        }
    }

    /// <summary>
    /// 自检：本地一条数据都没有时的首页空状态。应当直接显示「同步数据」按钮 + 那句描述，
    /// 点下去就等同「同步全部数据」（弹进度框），不再有任何启动弹框。
    /// 需要先清空本地库再跑，跑完会把数据重新抓一部分回来。
    /// </summary>
    public async Task RunEmptySyncCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "empty.txt");
        File.WriteAllText(log, "");

        await Task.Delay(1500);

        File.AppendAllText(log,
            $"本地卡片={_library.Items.Count} 空状态={EmptyState.Visibility} " +
            $"同步按钮={EmptySyncButton.Visibility} 文案=「{EmptyDesc.Text}」\n");
        SaveShot(Path.Combine(dir, "01-empty.png"));

        PressButton(EmptySyncButton);
        await Task.Delay(1800);

        File.AppendAllText(log,
            $"点同步后：弹框={(SyncDialogOpen ? "开（对）" : "关（错）")} 标题=「{SyncTitle.Text}」 " +
            $"进度=「{SyncPhaseText.Text}」\n");
        SaveShot(Path.Combine(dir, "02-dialog.png"));

        PressButton(SyncCancelButton);
        for (var i = 0; i < 200 && SyncDialogOpen; i++) await Task.Delay(100);
        await Task.Delay(600);

        File.AppendAllText(log,
            $"取消后：弹框={(SyncDialogOpen ? "仍开着（错）" : "已关（对）")} " +
            $"本地条数={AppDatabase.Instance.CacheCount()} " +
            $"首页卡片={_library.Items.Count} 同步按钮={EmptySyncButton.Visibility}\n");
        SaveShot(Path.Combine(dir, "03-after.png"));
    }

    /// <summary>
    /// 自检：走真实按钮路径跑一次「同步全部数据」。点按钮 → 弹框 → 采样进度 →
    /// 点「取消同步」→ 等弹框收起，把过程中看到的每一行文案写盘。
    /// 会真的往本地库里写数据（和用户点按钮的效果完全一致）。
    /// </summary>
    /// <param name="dir">输出目录。</param>
    /// <param name="seconds">同步持续多少秒后自动取消。</param>
    public async Task RunSyncRunCheckAsync(string dir, int seconds)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "sync-run.txt");
        File.WriteAllText(log, "");

        await Task.Delay(1200);

        NavList.SelectedIndex = 3;               // 设置
        await Task.Delay(600);

        var before = AppDatabase.Instance.CacheCount();
        var samples = new List<string>();

        // 顺手盯着有没有「不该出现」的异常：取消只该安静收工，不该抛东西出来
        var thrown = new List<string>();
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is OperationCanceledException) return;
            thrown.Add($"{e.Exception.GetType().Name}: {e.Exception.Message}");
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;

        PressButton(SyncAllButton);
        await Task.Delay(1500);
        File.AppendAllText(log, $"点按钮后弹框可见={SyncDialogOpen}\n");

        var limit = Math.Max(4, seconds) * 4;
        for (var i = 0; i < limit; i++)
        {
            if (i % 4 == 0)
                samples.Add($"{i * 250}ms | {SyncGameText.Text} | {SyncPhaseText.Text} | " +
                            $"{SyncCountText.Text} | {SyncPercentText.Text} | 中文名 {SyncNamePercentText.Text} {SyncNameCountText.Text}");

            if (!SyncDialogOpen) break;
            await Task.Delay(250);
        }

        SaveShot(Path.Combine(dir, "01-during-sync.png"));

        PressButton(SyncCancelButton);

        for (var i = 0; i < 200 && SyncDialogOpen; i++) await Task.Delay(100);

        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;

        File.WriteAllText(log, string.Join("\n", samples) + "\n");
        File.AppendAllText(log,
            $"\n取消后弹框可见={SyncDialogOpen}\n" +
            $"状态栏=「{StatusText.Text}」\n" +
            $"缓存条数：{before} → {AppDatabase.Instance.CacheCount()}\n" +
            $"Steam 节奏：{SteamWeb.PaceSummary}\n" +
            $"非取消类异常：{(thrown.Count == 0 ? "无（对）" : string.Join(" | ", thrown.Distinct().Take(6)))}\n" +
            $"首页条数={_library.Items.Count} 副标题=「{SectionSubtitle.Text}」\n");

        SaveShot(Path.Combine(dir, "02-after-sync.png"));
    }

    /// <summary>
    /// 自检：走真实按钮路径跑一次「更新已有游戏数据」，验证它和「更新全部」走的是同一套逻辑 ——
    /// 详情那条线只碰本地库里已有的游戏，游戏名那条线照旧抓官网名录 + Steam。
    /// 采样几秒后点「取消更新」，把两条线的文案和异常写盘。
    /// </summary>
    public async Task RunSyncExistingCheckAsync(string dir, int seconds)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "sync-existing.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && _firstLoading; i++) await Task.Delay(100);

        NavList.SelectedIndex = 3;               // 设置
        await Task.Delay(700);
        FindScrollViewer(SettingsPanel)?.ScrollToTop();
        await Task.Delay(300);

        var cached = AppDatabase.Instance.CacheCount();
        File.AppendAllText(log,
            $"本地条数={cached}｜名录={AppDatabase.Instance.GameNameCount()} " +
            $"（有中文名 {AppDatabase.Instance.GameNameCnCount()}）\n");

        var thrown = new List<string>();
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is OperationCanceledException) return;
            thrown.Add($"{e.Exception.GetType().Name}: {e.Exception.Message}");
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;

        PressButton(SyncExistingButton);
        await Task.Delay(900);

        File.AppendAllText(log,
            $"点按钮后：弹框={(SyncDialogOpen ? "开（对）" : "关（错）")}｜标题=「{SyncTitle.Text}」｜" +
            $"副标题=「{SyncSubtitle.Text}」｜游戏数据段={(SyncDataSection.Visibility == Visibility.Visible ? "显示（对）" : "收起（错）")}\n" +
            $"按钮状态：全部={(SyncAllButton.IsEnabled ? "可点（错）" : "置灰（对）")} " +
            $"快速={(QuickNamesButton.IsEnabled ? "可点（错）" : "置灰（对）")}\n");

        var limit = Math.Max(4, seconds) * 4;
        for (var i = 0; i < limit; i++)
        {
            if (!SyncDialogOpen) break;
            if (i % 8 == 0)
                File.AppendAllText(log,
                    $"{i * 250}ms | 数据：{SyncGameText.Text} | {SyncPhaseText.Text} | {SyncCountText.Text} | {SyncPercentText.Text}" +
                    $" || 游戏名：{SyncNameStateText.Text} | {SyncNameDetailText.Text} | {SyncNameCountText.Text}\n");
            await Task.Delay(250);
        }

        SaveShot(Path.Combine(dir, "01-existing-during.png"));

        PressButton(SyncCancelButton);
        for (var i = 0; i < 200 && SyncDialogOpen; i++) await Task.Delay(100);

        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;

        File.AppendAllText(log,
            $"\n取消后弹框={SyncDialogOpen}\n" +
            $"状态栏=「{StatusText.Text}」\n" +
            $"按钮复原：全部={SyncAllButton.Content} 快速={QuickNamesButton.Content}\n" +
            $"本地条数：{cached} → {AppDatabase.Instance.CacheCount()}｜名录={AppDatabase.Instance.GameNameCount()}\n" +
            $"非取消类异常：{(thrown.Count == 0 ? "无（对）" : string.Join(" | ", thrown.Distinct().Take(6)))}\n");

        SaveShot(Path.Combine(dir, "02-existing-after.png"));
    }

    /// <summary>
    /// 自检：收藏夹自己的搜索与分页。验证三件事——收藏夹页有分页栏、
    /// 搜索只在收藏夹里过滤（副标题 / 状态栏 / 卡片数都跟着变）、提示只出自收藏夹。
    /// </summary>
    public async Task RunFavoriteCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "favorites.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        await Task.Delay(1200);

        NavList.SelectedIndex = 1;               // 收藏夹
        await Task.Delay(900);

        void Note(string step) => File.AppendAllText(log,
            $"{step}：副标题=「{SectionSubtitle.Text}」 状态=「{StatusText.Text}」 " +
            $"页码=「{PagerPageText.Text}」 总数=「{PagerTotalText.Text}」 卡片={_items.Count} " +
            $"分页栏={(PagerBar.Visibility == Visibility.Visible ? "显示" : "隐藏")} " +
            $"状态栏={(StatusBar.Visibility == Visibility.Visible ? "显示" : "隐藏")}\n");

        Note("01-收藏夹");
        SaveShot(Path.Combine(dir, "01-favorites.png"));

        var total = _favorites.Count;

        // 拿第一款收藏的英文名第一个词当关键词，命中面宽一点
        var keyword = total > 0
            ? (_favorites[0].Title.Replace(" Trainer", "").Trim().Split(' ').FirstOrDefault() ?? "")
            : "";

        if (keyword.Length == 0)
        {
            File.AppendAllText(log, "    收藏夹是空的，跳过搜索部分\n");
            return;
        }

        Library.SearchText = keyword;
        await Library.SubmitSearchAsync();
        await Task.Delay(700);

        Note($"02-收藏夹搜索「{keyword}」");
        File.AppendAllText(log,
            $"    命中 {_items.Count} 款 / 收藏共 {total} 款｜关键词=「{Library.FavoritesKeyword}」\n");
        SaveShot(Path.Combine(dir, "02-favorites-search.png"));

        // 提示列表：只应出自收藏夹
        Library.SearchText = keyword;
        await Task.Delay(600);
        File.AppendAllText(log,
            $"    提示条数={Library.Suggestions.Count}｜前三条=" +
            $"{string.Join(" / ", Library.Suggestions.Take(3).Select(s => s.CleanEn))}\n");

        // 清空搜索：回到完整收藏夹
        Library.ExitSearch(clearText: true);
        await Task.Delay(600);
        Note("03-退出搜索");
        SaveShot(Path.Combine(dir, "03-favorites-back.png"));
    }

    /// <summary>在窗口坐标系里真的发一次左键单击（光标也移过去，和用户点没区别）。</summary>
    private async Task ClickAsync(Point point)
    {
        var screen = PointToScreen(point);
        SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        await Task.Delay(90);

        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(70);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(90);
    }

    /// <summary>
    /// 自检：上下分页。核对第 1 页的总数 / 页码 / 卡片数，点「下一页」后页码与内容是否跟着走，
    /// 再点「上一页」回到第 1 页（应走缓存、不重新请求），顺带验证详情页封面是打开即有。
    /// </summary>
    public async Task RunPageCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "page.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        await Task.Delay(1800);   // 封面陆续落盘，等一下再截图

        void Note(string step) => File.AppendAllText(log,
            $"{step}：副标题=「{SectionSubtitle.Text}」 状态=「{StatusText.Text}」 " +
            $"页码=「{PagerPageText.Text}」 总数=「{PagerTotalText.Text}」 卡片={_items.Count} " +
            $"分页栏={(PagerBar.Visibility == Visibility.Visible ? "显示" : "隐藏")} " +
            $"上一页可点={PagerPrev.IsEnabled} 下一页可点={PagerNext.IsEnabled}\n");

        Note("01-第1页");
        SaveShot(Path.Combine(dir, "01-page1.png"));

        var page1Ids = _items.Select(i => i.Id).ToList();

        PressButton(PagerNext);
        for (var i = 0; i < 200 && _libraryPager.Loading; i++) await Task.Delay(100);
        await Task.Delay(900);
        Note("02-下一页");
        var page2Ids = _items.Select(i => i.Id).ToList();
        SaveShot(Path.Combine(dir, "02-page2.png"));

        PressButton(PagerPrev);
        await Task.Delay(700);
        Note("03-上一页（应走缓存）");
        var backIds = _items.Select(i => i.Id).ToList();
        SaveShot(Path.Combine(dir, "03-page1-again.png"));

        File.AppendAllText(log,
            $"第 1 页 {page1Ids.Count} 条 / 第 2 页 {page2Ids.Count} 条 / 回到第 1 页 {backIds.Count} 条\n" +
            $"两页内容重叠：{page1Ids.Intersect(page2Ids).Count()} 条（应为 0）\n" +
            $"回到第 1 页是否与首次一致：{(backIds.SequenceEqual(page1Ids) ? "是" : "否")}\n" +
            $"请求过的页：{string.Join(",", _libraryPager.Cache.Keys.OrderBy(k => k))}\n");

        // ---- 详情页封面：点开瞬间就该有图，不再等开场动画 ----
        var item = _items.FirstOrDefault();
        if (item is not null)
        {
            OpenDetail(item);
            var immediate = DetailHost.Content is TrainerDetailView v && v.BannerReady;
            await Task.Delay(60);
            SaveShot(Path.Combine(dir, "04-detail-open.png"));
            File.AppendAllText(log, $"详情页打开瞬间封面已就绪：{(immediate ? "是" : "否")}\n");

            // 等开场动画彻底落定再拍一张，核对详情页本身的版式与亮度
            await Task.Delay(900);
            SaveShot(Path.Combine(dir, "05-detail-settled.png"));

            CloseDetail();
            await Task.Delay(400);
        }

        // 其余几页顺带拍一张，核对提亮之后的观感
        NavList.SelectedIndex = 1;
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "06-favorites.png"));

        NavList.SelectedIndex = 3;
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "07-settings.png"));

        NavList.SelectedIndex = 2;
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "08-queue.png"));

        NavList.SelectedIndex = 0;
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "09-back-to-library.png"));

        // ---- 搜索结果同样走上下分页 ----
        SearchBox.Text = "assassin";
        await SubmitSearchAsync();
        await Task.Delay(500);
        CloseSuggest();
        File.AppendAllText(log,
            $"搜索：副标题=「{SectionSubtitle.Text}」 状态=「{StatusText.Text}」 " +
            $"页码=「{PagerPageText.Text}」 总数=「{PagerTotalText.Text}」 卡片={_items.Count}\n");
        SaveShot(Path.Combine(dir, "10-search.png"));

        if (_searchPager.TotalPages > 1)
        {
            PressButton(PagerNext);
            for (var i = 0; i < 200 && _searchPager.Loading; i++) await Task.Delay(100);
            await Task.Delay(600);
            File.AppendAllText(log,
                $"搜索第 2 页：页码=「{PagerPageText.Text}」 卡片={_items.Count} " +
                $"状态=「{StatusText.Text}」\n");
            SaveShot(Path.Combine(dir, "11-search-page2.png"));
        }

        ExitSearch(clearText: true);
        await Task.Delay(500);
        SaveShot(Path.Combine(dir, "12-after-search-exit.png"));
    }

#if DEBUG
    /// <summary>
    /// 自检：一页超过 15 条时卡片不再被压小、列表能滚动。
    /// 只往内存里把当前页再叠一份（30 条），不发请求也不写库，测完 <see cref="Render"/> 还原。
    /// </summary>
    public async Task RunScrollCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "scroll.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        await Task.Delay(1500);

        var scroll = _listScroll ??= FindScrollViewer(CardList);

        void Note(string tag) => File.AppendAllText(log,
            $"{tag}：卡片 {_cardW}x{_cardH} · 行数 {Library.Rows.Count} · 条数 {_items.Count} · " +
            $"可视高 {scroll?.ViewportHeight:F0} 内容高 {scroll?.ExtentHeight:F0} " +
            $"可滚高度 {scroll?.ScrollableHeight:F0}\n");

        Note("15 条");
        SaveShot(Path.Combine(dir, "01-fifteen.png"));

        var snapshot = _items.ToList();
        var doubled = new List<TrainerItem>(snapshot);
        doubled.AddRange(snapshot);
        SetItems(doubled);
        await Task.Delay(800);

        Note("30 条");
        SaveShot(Path.Combine(dir, "02-thirty.png"));

        scroll?.ScrollToVerticalOffset(260);
        await Task.Delay(350);
        File.AppendAllText(log, $"滚到 260 之后实际位置：{scroll?.VerticalOffset:F0}\n");
        SaveShot(Path.Combine(dir, "03-scrolled.png"));

        Render();
        await Task.Delay(600);
        Note("还原");
        SaveShot(Path.Combine(dir, "04-restored.png"));
    }

    /// <summary>
    /// 自检：游戏名库。核对「表为空时启动自动抓全量」有没有落库、本地提示能不能查到、
    /// Steam 换中文名通不通，最后把同步进度弹窗摆出来拍一张。
    /// </summary>
    public async Task RunNameCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "names.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);

        // 启动时的种子任务是后台跑的，这里等它落库（最多 90 秒）
        var waited = 0;
        while (AppDatabase.Instance.GameNameCount() == 0 && waited < 900)
        {
            await Task.Delay(100);
            waited++;
        }

        await Task.Delay(800);

        var count = AppDatabase.Instance.GameNameCount();
        File.AppendAllText(log,
            $"游戏名库条数：{count}（等待种子 {waited * 100} ms）\n" +
            $"库里前 3 条：{string.Join(" / ", AppDatabase.Instance.SearchGameNames("a", 3).Select(r => r.CleanEn))}\n" +
            $"待补中文名：{AppDatabase.Instance.LoadGameNamesMissingCn().Count} 条（后台补齐只碰这些）\n");

        // ---- 本地提示 ----
        var hits = AppDatabase.Instance.SearchGameNames("assas", 8);
        File.AppendAllText(log,
            $"搜索 “assas” 命中 {hits.Count} 条：{string.Join(" / ", hits.Select(h => h.CleanEn))}\n");

        SearchBox.Text = "assas";
        SearchBox.Focus();
        await Task.Delay(1000);
        File.AppendAllText(log, $"提示弹窗：{(SuggestPopup.IsOpen ? "已打开" : "没打开")}，条目 {_suggests.Count}\n");

        // 弹窗是独立窗口，RenderTargetBitmap 拍不到，这里直接截屏
        CaptureWindowScreen(Path.Combine(dir, "01-suggest.png"));
        CloseSuggest();

        // 中文名也能搜（同步过才有）
        var cnHits = AppDatabase.Instance.SearchGameNames("赛博", 8);
        File.AppendAllText(log,
            $"搜索 “赛博” 命中 {cnHits.Count} 条：{string.Join(" / ", cnHits.Select(h => $"{h.CleanEn}（{h.NameCn}）"))}\n");

        // ---- 中文名提示（需要先有中文名；没有就现场换几条写进去） ----
        foreach (var probe in new[] { "Assassin's Creed Mirage", "Cyberpunk 2077", "Elden Ring" })
        {
            var cn = await SteamWeb.FetchChineseNameAsync(probe);
            File.AppendAllText(log, $"Steam：{probe} → {(cn is null ? "请求失败" : cn.Length == 0 ? "（没有中文名）" : cn)}\n");
        }

        // ---- 方向键选中 + 回车回填（键盘路径直接调内部方法验证，真实按键靠人工） ----
        // 先把输入清空再打，否则文本没变不会触发 TextChanged，提示不会重新查
        SearchBox.Text = "";
        await Task.Delay(60);
        SearchBox.Text = "assas";
        SearchBox.Focus();
        await Task.Delay(1000);

        MoveSuggest(+1);
        MoveSuggest(+1);
        var picked = SuggestList.SelectedItem as GameNameRow;
        File.AppendAllText(log, $"方向键选中：index={SuggestList.SelectedIndex}，选中项=「{picked?.CleanEn}」\n");

        // 再从「按键」这条路走一遍：模拟 ↓↓，看是不是真的挪到了第 2 条
        SuggestList.SelectedIndex = -1;
        SimulateSearchKey(Key.Down);
        SimulateSearchKey(Key.Down);
        File.AppendAllText(log,
            $"模拟按键 ↓↓ 之后：index={SuggestList.SelectedIndex}，" +
            $"选中项=「{(SuggestList.SelectedItem as GameNameRow)?.CleanEn}」\n");

        // 等一帧把高亮画出来，否则截到的还是上一帧（WPF 渲染在下一个合成周期）
        await Task.Delay(300);
        CaptureWindowScreen(Path.Combine(dir, "04-suggest-selected.png"));

        if (SuggestList.SelectedItem is GameNameRow row)
        {
            // 模拟回车：应该回填英文名并直接搜
            SimulateSearchKey(Key.Enter);
            await Task.Delay(1200);
            File.AppendAllText(log, $"模拟回车之后：搜索框=「{SearchBox.Text}」\n");
            SaveShot(Path.Combine(dir, "05-suggest-applied.png"));
        }
    }

    /// <summary>自检用：把一个按键按「预览」路径发给搜索框，等价于真实按下。</summary>
    private void SimulateSearchKey(Key key)
    {
        var source = PresentationSource.FromVisual(SearchBox);
        if (source is null) return;

        SearchBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
        });
    }

    /// <summary>
    /// 自检：用真实鼠标点一下智能提示的第二行，验证「点得到、点完直接去搜官网」。
    /// 之前的毛病是 Popup 用了 StaysOpen=False，鼠标一按它先把自己关掉，点击就丢了。
    /// </summary>
    public async Task RunSuggestClickCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "click.txt");
        File.WriteAllText(log, "");
        _selfCheckLog = log;

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        for (var i = 0; i < 600 && AppDatabase.Instance.GameNameCount() == 0; i++) await Task.Delay(100);

        NavList.SelectedIndex = 0;
        await Task.Delay(400);
        Activate();
        SearchBox.Focus();
        SearchBox.Text = "assas";
        await Task.Delay(1300);

        File.AppendAllText(log,
            $"提示已开：{SuggestPopup.IsOpen}，条目 {_suggests.Count}：" +
            $"{string.Join(" | ", _suggests.Take(3).Select(s => s.CleanEn))}\n");

        if (!SuggestPopup.IsOpen || _suggests.Count < 2)
        {
            File.AppendAllText(log, "没有可点的提示项，跳过\n");
            return;
        }

        var target = _suggests[1];
        SuggestList.SelectedIndex = 0;   // 高亮先放第一行，点完看它有没有真的换过去
        SuggestList.UpdateLayout();
        await Task.Delay(150);

        if (SuggestList.ItemContainerGenerator.ContainerFromIndex(1) is not FrameworkElement container)
        {
            File.AppendAllText(log, "拿不到第 2 行的容器，跳过\n");
            return;
        }

        // Popup 是独立窗口，Visual.PointToScreen 在这套 DPI 下不可靠 ——
        // 直接拿 Popup 自己的窗口矩形，再加上行在 Popup 里的相对位置（按 DPI 换算成物理像素）
        var popupRoot = SuggestPopup.Child as FrameworkElement;
        var popupHwnd = popupRoot is null
            ? IntPtr.Zero
            : (PresentationSource.FromVisual(popupRoot) as HwndSource)?.Handle ?? IntPtr.Zero;

        var x = 0;
        var y = 0;
        if (popupHwnd != IntPtr.Zero && GetWindowRect(popupHwnd, out var popupRect))
        {
            var scale = Math.Max(1.0, GetDpiForWindow(popupHwnd) / 96.0);
            var rel = container.TransformToAncestor(popupRoot!)
                .Transform(new Point(container.ActualWidth / 2, container.ActualHeight / 2));

            x = popupRect.Left + (int)Math.Round(rel.X * scale);
            y = popupRect.Top + (int)Math.Round(rel.Y * scale);

            File.AppendAllText(log,
                $"Popup 窗口 ({popupRect.Left},{popupRect.Top})-({popupRect.Right},{popupRect.Bottom})，" +
                $"缩放 {scale:F2}，行内相对 ({rel.X:F0},{rel.Y:F0})\n");
        }
        else
        {
            var fallback = container.PointToScreen(
                new Point(container.ActualWidth / 2, container.ActualHeight / 2));
            x = (int)fallback.X;
            y = (int)fallback.Y;
            File.AppendAllText(log, $"拿不到 Popup 句柄，退回 PointToScreen ({x},{y})\n");
        }

        File.AppendAllText(log, $"要点第 2 行「{target.CleanEn}」，屏幕坐标 ({x}, {y})\n");

        // 自检窗口必须在最前面，否则这一下点到的是别的窗口
        var mainHwnd = new WindowInteropHelper(this).Handle;
        SetForegroundWindow(mainHwnd);
        await Task.Delay(400);
        File.AppendAllText(log,
            $"点击前前台是本窗口：{(GetForegroundWindow() == mainHwnd ? "是" : "否")}\n");

        SetCursorPos(x, y);
        await Task.Delay(120);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(40);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);

        await Task.Delay(1800);

        var ok = SearchBox.Text == target.CleanEn && _searchPager.TotalCount > 0;
        File.AppendAllText(log,
            $"点完之后：搜索框=「{SearchBox.Text}」，提示开着={SuggestPopup.IsOpen}\n" +
            $"    结果 {_searchPager.TotalCount} 条：{string.Join(" / ", _items.Take(2).Select(i => i.Title))}\n" +
            $"    状态栏：{StatusText.Text}\n" +
            $"    与预期一致：{(ok ? "是" : "否")}\n");

        CloseSuggest();
        SaveShot(Path.Combine(dir, "01-after-click.png"));
    }

    /// <summary>
    /// 自检：搜索的本地优先策略 + 关键词编码 / 中文名翻译。
    /// 本地库能搜到的词应当直接给本地结果（不联网），搜不到才翻译成英文名去官网；
    /// 这里把几类关键词真打一遍，记下反查结果、生效关键词、结果来源和条数。
    /// </summary>
    public async Task RunSearchCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "search.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        for (var i = 0; i < 600 && AppDatabase.Instance.GameNameCount() == 0; i++) await Task.Delay(100);

        File.AppendAllText(log,
            $"游戏名库 {AppDatabase.Instance.GameNameCount()} 条，其中有中文名 {AppDatabase.Instance.GameNameCnCount()} 条，" +
            $"本地卡片 {AppDatabase.Instance.CacheCount()} 条\n");

        foreach (var (label, keyword) in new[]
                 {
                     ("中文名 · 完整", "赛博朋克 2077"),
                     ("中文名 · 部分", "艾尔登法环"),
                     ("英文 · 空格与撇号", "Assassin's Creed"),
                     ("英文 · 单词", "elden"),
                     ("中文名 · 库里没有", "风灵月影"),
                 })
        {
            var translated = AppDatabase.Instance.FindEnglishByChinese(keyword);
            SearchBox.Text = keyword;
            await SubmitSearchAsync();
            await Task.Delay(800);

            File.AppendAllText(log,
                $"[{label}] 输入「{keyword}」\n" +
                $"    本地反查英文名：{translated ?? "（没有）"}\n" +
                $"    生效关键词：{_keyword}（编码后 {Uri.EscapeDataString(_keyword)}）\n" +
                $"    结果来源：{(_searchFromLocal ? "本地库（未联网）" : "官网")}\n" +
                $"    结果 {_searchPager.TotalCount} 条：{string.Join(" / ", _items.Take(3).Select(i => i.Title))}\n" +
                $"    状态栏：{StatusText.Text}\n");
        }

        // 最后停在中文搜索的结果页上，顺带把顶栏（刷新按钮 + 悬停菜单）一起拍进去
        SearchBox.Text = "赛博朋克 2077";
        await SubmitSearchAsync();
        await Task.Delay(900);
        CloseSuggest();
        await Task.Delay(200);
        CaptureWindowScreen(Path.Combine(dir, "01-search-cn.png"));

        File.AppendAllText(log,
            $"截图停在：搜索「{_keyword}」，结果 {_searchPager.TotalCount} 条，第一页 {_items.Count} 张卡片\n");
    }

    /// <summary>
    /// 自检：关于 / 声明弹窗的排版。正文全部来自 Assets\disclaimer.txt，
    /// 查一遍文件有没有读到、段落数、字号与颜色，再拍首屏和滚到底两张。
    /// </summary>
    public async Task RunAboutCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "about.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);

        ShowAbout();
        await Task.Delay(900);
        SaveShot(Path.Combine(dir, "01-about.png"));

        var body = AboutBody.Text;
        File.AppendAllText(log,
            $"正文文件：{DisclaimerText.FilePath}（存在={File.Exists(DisclaimerText.FilePath)}）\n" +
            $"正文：{body.Length} 字 / {body.Split('\n').Length} 行，" +
            $"字号 {AboutBody.FontSize:0.#}，行高 {TextBlock.GetLineHeight(AboutBody):0.#}，颜色 {AboutBody.Foreground}\n" +
            $"正文控件：{AboutBody.GetType().Name}（只读 {AboutBody.IsReadOnly}）\n" +
            $"首个非空行：「{body.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)}」\n");

        var copied = await VerifyAboutCopyAsync(body);
        File.AppendAllText(log,
            $"复制实测：{(copied ? "全选 → 复制 → 剪贴板与正文一致" : "没复制成功或内容不一致")}\n");

        var scroll = FindScrollViewer(AboutCard);
        if (scroll is not null)
        {
            scroll.ScrollToEnd();
            await Task.Delay(400);
            SaveShot(Path.Combine(dir, "02-about-end.png"));

            File.AppendAllText(log,
                $"关于卡片：{AboutCard.ActualWidth:F0}x{AboutCard.ActualHeight:F0}，" +
                $"正文可视高 {scroll.ViewportHeight:F0} / 内容高 {scroll.ExtentHeight:F0}，" +
                $"可滚 {scroll.ScrollableHeight:F0}\n");
        }

        CloseAbout();
    }

    /// <summary>
    /// 自检：设置页「关于」分组 + 更新弹窗。发行版信息是喂进去的假数据，断网也能验排版；
    /// 最后再真查一次 Gitee，把结果（或失败原因）写进日志，证明联网那条路是通的。
    /// </summary>
    public async Task RunUpdateCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "update.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);

        NavList.SelectedIndex = 3;               // 设置
        await Task.Delay(800);

        // 设置页左侧菜单切到「关于」：默认停在「数据管理」，不切的话这一组整块折叠，
        // 截图和 IsVisible 都测不到里面的网盘那一栏
        SettingsMenu.SelectedIndex = 2;
        await Task.Delay(400);

        // 「关于」这一组在设置页最下面，滚到底才看得见
        FindScrollViewer(SettingsPanel)?.ScrollToEnd();
        await Task.Delay(450);
        SaveShot(Path.Combine(dir, "01-settings-about.png"));

        File.AppendAllText(log,
            $"当前版本：v{UpdateService.CurrentVersionText}\n" +
            $"配置来源：{AppConfig.Describe()}，文件 {AppConfig.FilePath}，存在={File.Exists(AppConfig.FilePath)}\n" +
            $"启动自动检查更新：{(AppConfig.AutoUpdateCheck ? "开（会查）" : "关（跳过）")}\n" +
            $"接口地址：{UpdateService.ApiUrl}\n" +
            $"软件更新行：{UpdateStatusText.Text}\n" +
            $"更新内容行：{UpdateNotesText.Text}\n" +
            $"按钮：{CheckUpdateButton.Content} / {UpdateNotesButton.Content} / {CloudDriveButton.Content}\n" +
            $"网盘下载栏：行高 {CloudDriveRow.ActualHeight:F0}，按钮可见 {CloudDriveButton.IsVisible}，" +
            $"按钮宽 {CloudDriveButton.ActualWidth:F0}\n");

        var fake = new UpdateRelease
        {
            Version = "1.0.3",
            Tag = "v1.0.3",
            Title = "风灵月影宗藏宝阁 1.0.3",
            Notes = UpdateService.PlainNotes(
                "## 新增\n" +
                "- 在线更新：启动自动检查，也可以在设置里手动点\n" +
                "- 「关于」分组：软件更新 + 更新内容\n\n" +
                "## 修复\n" +
                "- 修掉详情页在 125% 缩放下横幅偏移的问题\n" +
                "- 清理缓存偶尔漏删 f 目录的问题\n\n" +
                "> 更新包约 70MB，下载完成后自动安装。"),
            PageUrl = "https://gitee.com/misslius/FlingTrainerX/releases/tag/v1.0.3",
            DownloadUrl = "https://gitee.com/misslius/FlingTrainerX/releases/download/v1.0.3/pkg.zip",
            AssetName = "风灵月影宗藏宝阁-更新包-1.0.3.zip",
            Size = 73_400_000,
            PublishedAt = DateTimeOffset.Now
        };

        ShowUpdateDialog(fake, prompt: true);
        await Task.Delay(900);
        SaveShot(Path.Combine(dir, "02-update-prompt.png"));

        File.AppendAllText(log,
            $"发现新版本弹窗：{UpdateTitleText.Text}｜{UpdateVersionText.Text}｜" +
            $"卡片 {UpdateCard.ActualWidth:F0}x{UpdateCard.ActualHeight:F0}｜" +
            $"说明 {UpdateNotesBox.Text.Length} 字 / 可视高 {UpdateNotesBox.ActualHeight:F0}｜" +
            $"按钮 {UpdateNowButton.Content} + {(UpdateLaterButton.Visibility == Visibility.Visible ? UpdateLaterButton.Content : "（无取消）")}\n");

        // 点遮罩应当能关掉。先把窗口抢到前台：自检用真鼠标点，窗口被别的窗口压住就点空了
        ShowMainWindow();
        await Task.Delay(350);
        var maskPoint = new Point(ActualWidth - 70, ActualHeight - 110);
        await ClickAsync(maskPoint);
        await Task.Delay(600);
        File.AppendAllText(log,
            $"点遮罩后：{(UpdateLayer.Visibility == Visibility.Visible ? "仍打开（错）" : "已关闭（对）")}\n");

        // 「取消更新」：要顺手把 autoUpdateCheck=false 写进 config.json，以后启动不再自动查
        var autoBefore = AppConfig.AutoUpdateCheck;
        var configBefore = File.Exists(AppConfig.FilePath) ? File.ReadAllText(AppConfig.FilePath) : "";
        var cancelText = UpdateLaterButton.Content?.ToString() ?? "";

        ShowUpdateDialog(fake, prompt: true);
        await Task.Delay(800);
        PressButton(UpdateLaterButton);           // 点「取消更新」
        await Task.Delay(700);

        var autoAfter = AppConfig.AutoUpdateCheck;
        var configAfter = File.Exists(AppConfig.FilePath) ? File.ReadAllText(AppConfig.FilePath) : "";
        var wroteFlag = configAfter.Contains($"\"{AppConfig.AutoUpdateKey}\": false");
        File.AppendAllText(log,
            $"取消更新：按钮「{cancelText}」｜开关 {autoBefore} → {autoAfter}（应为 False）｜" +
            $"config.json 里 {AppConfig.AutoUpdateKey} 写成 false {(wroteFlag ? "（对）" : "（错）")}｜" +
            $"弹窗{(UpdateLayer.Visibility == Visibility.Visible ? "仍打开（错）" : "已关闭（对）")}\n");

        // 测完还原，别把本机的配置改坏
        AppConfig.SetAutoUpdateCheck(autoBefore);
        if (configBefore.Length > 0) File.WriteAllText(AppConfig.FilePath, configBefore);

        // 启动后的自动检查：只有「发现新版本」才弹，拿不到发行版 / 断网一律安静
        var silentOk = true;
        foreach (var quiet in new[]
                 {
                     UpdateCheckResult.Fail("检查更新超时，请稍后再试"),
                     UpdateCheckResult.Unavailable(System.Net.HttpStatusCode.NotFound)
                 })
        {
            await VM.Settings.PresentAsync(quiet, auto: true);
            await Task.Delay(300);
            if (ConfirmLayer.Visibility == Visibility.Visible || UpdateLayer.Visibility == Visibility.Visible)
                silentOk = false;
        }

        File.AppendAllText(log,
            $"自动检查失败（断网 / 404）：{(silentOk ? "全部静默，没弹框（对）" : "弹了框（错）")}\n");

        // 只读模式：不该出现「取消更新」，主按钮变成「知道了」
        ShowUpdateDialog(fake, prompt: false);
        await Task.Delay(900);
        SaveShot(Path.Combine(dir, "03-update-notes.png"));
        File.AppendAllText(log,
            $"更新内容弹窗：{UpdateTitleText.Text}｜主按钮 {UpdateNowButton.Content}｜" +
            $"稍后按钮 {(UpdateLaterButton.Visibility == Visibility.Visible ? "可见（错）" : "隐藏（对）")}\n");

        CloseUpdateDialog();
        await Task.Delay(500);

        // 设置页「查看更新」走的那条路：正文来自本地 Assets\changelog.txt，不联网
        Overlays.ShowUpdateNotes(
            $"更新内容 · v{UpdateService.CurrentVersionText}",
            $"当前版本 v{UpdateService.CurrentVersionText}",
            ChangelogText.Load());
        await Task.Delay(900);
        SaveShot(Path.Combine(dir, "03b-update-local.png"));

        var localBody = UpdateNotesBox.Text;
        var localFirst = localBody.Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
        File.AppendAllText(log,
            $"本地更新内容：{UpdateTitleText.Text}｜{UpdateVersionText.Text}｜" +
            $"正文 {localBody.Length} 字 / {localBody.Split('\n').Length} 行｜首个非空行「{localFirst}」｜" +
            $"稍后按钮 {(UpdateLaterButton.Visibility == Visibility.Visible ? "可见（错）" : "隐藏（对）")}\n");

        CloseUpdateDialog();
        await Task.Delay(500);

        // 超长发行说明：卡片不能撑出窗口，正文自己出滚动条
        var longNotes = string.Join("\n", Enumerable.Range(1, 40)
            .Select(i => $"· 第 {i} 条：修掉某处细节问题，顺便把相关文案也统一了一遍"));
        var big = new UpdateRelease
        {
            Version = fake.Version,
            Tag = fake.Tag,
            Notes = longNotes,
            PageUrl = fake.PageUrl,
            DownloadUrl = fake.DownloadUrl,
            AssetName = fake.AssetName,
            Size = fake.Size,
            PublishedAt = fake.PublishedAt
        };

        ShowUpdateDialog(big, prompt: true);
        await Task.Delay(900);
        SaveShot(Path.Combine(dir, "04-update-long.png"));
        File.AppendAllText(log,
            $"超长说明：卡片 {UpdateCard.ActualWidth:F0}x{UpdateCard.ActualHeight:F0}（窗口 {ActualWidth:F0}x{ActualHeight:F0}）｜" +
            $"正文可视 {UpdateNotesBox.ViewportHeight:F0} / 内容 {UpdateNotesBox.ExtentHeight:F0}｜" +
            $"{(UpdateCard.ActualHeight <= ActualHeight - 40 ? "卡片没超出窗口（对）" : "卡片超出窗口（错）")}\n");

        CloseUpdateDialog();
        await Task.Delay(500);

        // 「已是最新」提示框：和确认框共用一层，但只有一颗「知道了」按钮
        _ = VM.Dialogs.NotifyAsync("已是最新版本", $"当前版本 v{UpdateService.CurrentVersionText}，无需更新。");
        await Task.Delay(700);
        SaveShot(Path.Combine(dir, "05-latest-notify.png"));
        File.AppendAllText(log,
            $"最新版提示：标题=「{ConfirmTitle.Text}」｜按钮=「{ConfirmOkButton.Content}」｜" +
            $"取消按钮={(ConfirmCancelButton.Visibility == Visibility.Visible ? "显示（错）" : "隐藏（对）")}\n");

        PressButton(ConfirmOkButton);
        await Task.Delay(500);
        File.AppendAllText(log,
            $"提示框关闭后：{(ConfirmLayer.Visibility == Visibility.Visible ? "仍打开（错）" : "已关闭（对）")}\n");

        // 再验一次「仓库访问不到」那种失败：文案只提网盘，不许把 404 / 403 摆给用户
        _ = VM.Settings.PresentAsync(
            UpdateCheckResult.Unavailable(System.Net.HttpStatusCode.Forbidden), auto: false);
        await Task.Delay(700);
        SaveShot(Path.Combine(dir, "05b-unavailable-notify.png"));
        File.AppendAllText(log,
            $"无法在线更新提示：标题=「{ConfirmTitle.Text}」｜正文=「{ConfirmBody.Text}」｜" +
            $"{(ConfirmBody.Text.Contains("网盘") ? "有网盘引导（对）" : "没有网盘引导（错）")}" +
            $"{(ConfirmBody.Text.Contains("403") || ConfirmBody.Text.Contains("HTTP") ? "｜漏了状态码（错）" : "｜没漏状态码（对）")}\n");
        PressButton(ConfirmOkButton);
        await Task.Delay(400);

        // 真查一次：没网 / 仓库没公开也只会写一句失败原因，不会抛异常
        var result = await UpdateService.CheckAsync();
        File.AppendAllText(log,
            $"真查一次：{result.State}｜{result.Message}" +
            (result.RepoUnreachable ? $"｜仓库访问不到（{result.Detail}），界面按「去网盘」处理" : "") + "\n");
        if (result.Release is { } release)
        {
            File.AppendAllText(log,
                $"发行版：v{release.Version}｜附件 {release.AssetName}（{release.SizeText}）｜" +
                $"{release.PageUrl}\n");
        }
    }

    /// <summary>
    /// 自检用：「全选 → 复制」走一遍真剪贴板，确认正文真的能复制出去（不是摆着好看的）。
    /// 会临时占用剪贴板，测完把原来的内容放回去。
    /// </summary>
    private async Task<bool> VerifyAboutCopyAsync(string expected)
    {
        var backup = "";
        try
        {
            if (Clipboard.ContainsText()) backup = Clipboard.GetText();
        }
        catch (Exception)
        {
            // 剪贴板被别的程序占着就读不到，跳过备份
        }

        var ok = false;
        try
        {
            AboutBody.Focus();
            AboutBody.SelectAll();
            AboutBody.Copy();
            await Task.Delay(150);

            // TextBox 往剪贴板写的是 CRLF，正文里是 LF，比对前先统一
            ok = Flat(Clipboard.GetText()) == Flat(expected);
        }
        catch (Exception)
        {
            ok = false;
        }

        try
        {
            if (backup.Length > 0) Clipboard.SetDataObject(backup, true);
        }
        catch (Exception)
        {
            // 恢复不了也不影响结论
        }

        // 清掉全选留下的高亮，否则后面那张「滚到底」的截图整片发蓝
        try
        {
            AboutBody.Select(0, 0);
        }
        catch (Exception)
        {
            // 忽略
        }

        return ok;

        static string Flat(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
    }

    /// <summary>
    /// 自检：顶栏刷新按钮的悬停菜单。展开后应当看到「同步已有游戏 / 同步全部游戏」两项；
    /// Popup 是独立窗口，RenderTargetBitmap 拍不到，这里拍真实屏幕，再验一次鼠标进出能自己开关。
    /// </summary>
    public async Task RunSyncMenuCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "sync-menu.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(1200);

        OpenSyncMenu();
        await Task.Delay(500);

        File.AppendAllText(log,
            $"菜单打开={SyncMenuPopup.IsOpen} 第一项=「{MenuLabel(SyncMenuExisting)}」 " +
            $"第二项=「{MenuLabel(SyncMenuAll)}」 可用={SyncMenuExisting.IsEnabled}/{SyncMenuAll.IsEnabled}\n");

        var winAt = PointToScreen(new Point(0, 0));
        var hostAt = RefreshHost.PointToScreen(new Point(0, 0));
        if (SyncMenuPopup.Child is FrameworkElement menuChild)
        {
            var menuAt = menuChild.PointToScreen(new Point(0, 0));
            File.AppendAllText(log,
                $"窗口左上=({winAt.X:0},{winAt.Y:0}) 按钮左上=({hostAt.X:0},{hostAt.Y:0}) " +
                $"菜单左上=({menuAt.X:0},{menuAt.Y:0}) 菜单尺寸={menuChild.ActualWidth:0}x{menuChild.ActualHeight:0}\n");
        }

        CaptureWindowScreen(Path.Combine(dir, "01-menu.png"));

        // 菜单是独立的 HWND，窗口截图拍不到；屏幕截图又可能被别的窗口挡住，
        // 干脆把菜单自己渲染成一张图，宽度够不够一眼就能看出来
        if (SyncMenuPopup.Child is FrameworkElement menuVisual)
        {
            try
            {
                var mw = Math.Max(1, (int)Math.Round(menuVisual.ActualWidth));
                var mh = Math.Max(1, (int)Math.Round(menuVisual.ActualHeight));
                var menuBitmap = new RenderTargetBitmap(mw, mh, 96, 96, PixelFormats.Pbgra32);
                menuBitmap.Render(menuVisual);

                var menuEncoder = new PngBitmapEncoder();
                menuEncoder.Frames.Add(BitmapFrame.Create(menuBitmap));
                using var menuStream = File.Create(Path.Combine(dir, "menu-only.png"));
                menuEncoder.Save(menuStream);
            }
            catch
            {
                // 拍不到不影响自检结论
            }
        }

        CloseSyncMenu();
        await Task.Delay(400);
        File.AppendAllText(log, $"收起后菜单打开={SyncMenuPopup.IsOpen}\n");

        // 真实鼠标路径：挪到按钮上应当自己弹出来，挪走应当自己收起来
        var saved = WinForms.Cursor.Position;
        var center = RefreshHost.PointToScreen(new Point(RefreshHost.ActualWidth / 2, RefreshHost.ActualHeight / 2));
        SetCursorPos((int)center.X, (int)center.Y);
        await Task.Delay(700);
        File.AppendAllText(log, $"鼠标移上去后菜单打开={SyncMenuPopup.IsOpen}\n");
        CaptureWindowScreen(Path.Combine(dir, "02-hover.png"));

        SetCursorPos(saved.X, saved.Y);
        await Task.Delay(900);
        File.AppendAllText(log, $"鼠标移开后菜单打开={SyncMenuPopup.IsOpen}\n");
        SaveShot(Path.Combine(dir, "03-after.png"));

        static string MenuLabel(Button button) => button.Content is Grid grid
            ? grid.Children.OfType<TextBlock>().Last().Text
            : "";
    }

    /// <summary>
    /// 自检：把窗口依次摆到每块显示器上，同一块屏分别用 Display / Ideal 两种文本整形模式各拍一张。
    /// 用来回答「为什么 100% 的屏字发虚、150% 的屏反而清楚」——到底是 DPI 的锅，还是整形模式选错了。
    /// </summary>
    public async Task RunTextModeCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "text-mode.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(1500);

        var startedLeft = Left;
        var startedTop = Top;

        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var center = new Point32
            {
                X = screen.Bounds.Left + screen.Bounds.Width / 2,
                Y = screen.Bounds.Top + screen.Bounds.Height / 2
            };

            var monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
            GetDpiForMonitor(monitor, 0, out var monitorDpi, out _);
            var scale = monitorDpi / 96d;
            if (scale <= 0) scale = 1;

            // 窗口坐标是 DIP，屏幕矩形是物理像素，摆位置时要先除缩放
            Left = screen.Bounds.Left / scale + 40;
            Top = screen.Bounds.Top / scale + 40;
            await Task.Delay(900);

            var tag = $"{screen.DeviceName.Replace(@"\\.\", "")}-{monitorDpi}dpi";

            var variants = new (TextFormattingMode Format, TextRenderingMode Render)[]
            {
                (TextFormattingMode.Display, TextRenderingMode.ClearType),
                (TextFormattingMode.Display, TextRenderingMode.Grayscale),
                (TextFormattingMode.Ideal, TextRenderingMode.Grayscale)
            };

            foreach (var variant in variants)
            {
                TextOptions.SetTextFormattingMode(this, variant.Format);
                TextOptions.SetTextRenderingMode(this, variant.Render);
                await Task.Delay(350);
                ShootWindow(Path.Combine(dir, $"{tag}-{variant.Format}-{variant.Render}.png"));

                File.AppendAllText(log,
                    $"{tag} 缩放 {scale * 100:0}% 模式 {variant.Format}/{variant.Render}：" +
                    $"窗口 DPI={GetDpiForWindow(new WindowInteropHelper(this).Handle)}，" +
                    $"尺寸 {ActualWidth:0}x{ActualHeight:0} DIP\n");
            }
        }

        // 还原：位置、抗锯齿方式、以及窗口原本的整形模式
        Left = startedLeft;
        Top = startedTop;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Auto);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        await Task.Delay(600);

        void ShootWindow(string path)
        {
            try
            {
                GetWindowRect(new WindowInteropHelper(this).Handle, out var rect);
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0) return;

                using var bitmap = new System.Drawing.Bitmap(width, height);
                using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height));
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch
            {
                // 拍不到就算了，日志里的数据仍然有效
            }
        }
    }

    /// <summary>
    /// 字重对比自检（仅 Debug）：同一句话用 Regular / SemiBold / Bold 画出来，顺手查雅黑有哪些真实字重。
    /// 背景：微软雅黑只有 Light / Regular / Bold 三个字形，没有 SemiBold——WPF 遇到 SemiBold
    /// 会拿 Regular 的轮廓做合成加粗，笔画边缘是「撑」出来的，低像素密度下看着发虚发灰。
    /// </summary>
    public async Task RunWeightCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "weight.txt");
        File.WriteAllText(log, string.Empty);

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(1200);

        var yahei = new FontFamily("Microsoft YaHei UI");
        var realWeights = yahei.GetTypefaces()
            .Where(t => t.Style == FontStyles.Normal && t.Stretch == FontStretches.Normal)
            .Select(t => t.Weight.ToOpenTypeWeight())
            .Distinct()
            .OrderBy(w => w)
            .ToList();

        File.AppendAllText(log,
            $"Microsoft YaHei UI 真实字重（OpenType 数值）：{string.Join(", ", realWeights)}\n" +
            $"本次请求：Regular={FontWeights.Normal.ToOpenTypeWeight()}、" +
            $"SemiBold={FontWeights.SemiBold.ToOpenTypeWeight()}、" +
            $"Bold={FontWeights.Bold.ToOpenTypeWeight()}\n" +
            "（SemiBold 的数值不在第一行里，说明是 WPF 合成加粗）\n\n");

        var panel = new Border
        {
            Padding = new Thickness(28, 20, 28, 24),
            Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x14, 0x20)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var stack = new StackPanel();
        panel.Child = stack;

        var family = (FontFamily)FindResource("F.Ui");
        var rows = new (string Tag, double Size)[] { ("12", 12d), ("14", 14d), ("17", 17d) };
        var weights = new (string Name, FontWeight Weight)[]
        {
            ("Regular ", FontWeights.Normal),
            ("SemiBold", FontWeights.SemiBold),
            ("Bold    ", FontWeights.Bold),
        };

        foreach (var (tag, size) in rows)
        {
            stack.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, stack.Children.Count == 0 ? 0 : 14, 0, 2),
                FontFamily = family,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5F, 0x7C, 0x99)),
                Text = $"—— {tag} px ——",
            });

            foreach (var (name, weight) in weights)
            {
                var tb = new TextBlock
                {
                    FontFamily = family,
                    FontSize = size,
                    FontWeight = weight,
                    Foreground = Brushes.White,
                    Text = $"{name}  风灵月影宗藏宝阁 0123",
                };
                TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);
                TextOptions.SetTextRenderingMode(tb, TextRenderingMode.ClearType);
                stack.Children.Add(tb);
            }
        }

        Grid.SetColumnSpan(panel, 2);
        RootGrid.Children.Add(panel);
        await Task.Delay(600);

        try
        {
            GetWindowRect(new WindowInteropHelper(this).Handle, out var rect);
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width > 0 && height > 0)
            {
                using var bitmap = new System.Drawing.Bitmap(width, height);
                using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height));
                bitmap.Save(Path.Combine(dir, "weights.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        catch
        {
            // 拍不到就只留日志
        }

        RootGrid.Children.Remove(panel);
        File.AppendAllText(log, $"窗口 DPI={GetDpiForWindow(new WindowInteropHelper(this).Handle)}" +
                                $"（96 = 100% 缩放，144 = 150%）\n");
    }

    /// <summary>
    /// 自检：拖窗口时悬停菜单要跟着走。
    /// Popup 是独立 HWND，用 GetWindowRect 量「菜单左上 − 窗口左上」这个相对偏移，
    /// 移动窗口前后它必须保持不变（差 2px 内算过），否则就是那个「菜单留在原地」的 BUG 又回来了。
    /// </summary>
    public async Task RunSyncMenuTrackCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "sync-menu-track.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(1200);

        OpenSyncMenu();
        await Task.Delay(600);

        var started = Left;
        var startTop = Top;

        // 基准：窗口还没动时，菜单相对窗口左上角的偏移
        GetWindowRect(new WindowInteropHelper(this).Handle, out var baseWin);
        GetWindowRect(PopupHandle(SyncMenuPopup), out var baseMenu);
        var gapX = baseMenu.Left - baseWin.Left;
        var gapY = baseMenu.Top - baseWin.Top;

        // 第一轮：先按「修之前」的样子跑一次 —— 把跟随处理摘掉，再按系统拖动的方式挪窗口
        LocationChanged -= OnWindowLocationChanged;
        var without = await MoveByAsync(-50, -34);
        LogRound("不装跟随处理", without);

        // 第二轮：装回跟随处理，反方向挪回来
        LocationChanged += OnWindowLocationChanged;
        var with = await MoveByAsync(50, 34);
        LogRound("装回跟随处理", with);

        // 还原窗口位置，别把后面还要跑的自检带偏
        Left = started;
        Top = startTop;
        await Task.Delay(300);
        CloseSyncMenu();

        void LogRound(string name, (int Dx, int Dy, int MovedX, int MovedY, bool Open) r)
        {
            if (!r.Open)
            {
                File.AppendAllText(log, $"{name}：移动过程中菜单被收起了，测不出跟随（本轮作废）\n");
                return;
            }

            var followed = Math.Abs(r.Dx - gapX) <= 2 && Math.Abs(r.Dy - gapY) <= 2;

            File.AppendAllText(log,
                $"{name}：窗口移动 ({r.MovedX},{r.MovedY})，菜单相对偏移 ({r.Dx},{r.Dy})，" +
                $"基准 ({gapX},{gapY}) → {(followed ? "菜单跟着走了" : "菜单留在原地")}\n");
        }

        // 按系统拖动的方式挪窗口：SetWindowPos 会产生和真人拖窗一样的 WM_MOVE / WM_WINDOWPOSCHANGED，
        // 也就是 LocationChanged。分几步挪，模拟拖动过程中的连续位移。
        async Task<(int Dx, int Dy, int MovedX, int MovedY, bool Open)> MoveByAsync(int dx, int dy)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            GetWindowRect(hwnd, out var win0);

            const int steps = 6;
            for (var i = 1; i <= steps; i++)
            {
                SetWindowPos(hwnd, IntPtr.Zero,
                    win0.Left + dx * i / steps, win0.Top + dy * i / steps, 0, 0,
                    SwpNoSize | SwpNoZOrder | SwpNoActivate);
                await Task.Delay(60);
            }

            await Task.Delay(150);

            GetWindowRect(hwnd, out var win1);
            var open = SyncMenuPopup.IsOpen;
            var popupHandle = PopupHandle(SyncMenuPopup);

            var menuX = 0;
            var menuY = 0;
            if (open && popupHandle != IntPtr.Zero && GetWindowRect(popupHandle, out var menu1))
            {
                menuX = menu1.Left;
                menuY = menu1.Top;
            }

            return (menuX - win1.Left, menuY - win1.Top, win1.Left - win0.Left, win1.Top - win0.Top, open);
        }
    }

    /// <summary>
    /// 自检：把和「字体糊不糊」有关的环境信息一次性打出来 ——
    /// 每块屏的分辨率/缩放、窗口实际 DPI、当前生效的文本整形模式、系统 ClearType 设置。
    /// 哪块屏上字发虚，就把窗口拖过去跑一遍，日志里能直接看到那块屏的缩放是不是 100%。
    /// </summary>
    public async Task RunDpiCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "dpi.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(1500);

        var dpi = VisualTreeHelper.GetDpi(this);
        var hwnd = new WindowInteropHelper(this).Handle;

        File.AppendAllText(log,
            $"窗口 DPI = {GetDpiForWindow(hwnd)}（VisualTree 报 {dpi.PixelsPerInchX:0.#}，" +
            $"缩放 {dpi.DpiScaleX:0.###}）\n" +
            $"窗口矩形 = ({Left:0},{Top:0}) {ActualWidth:0}x{ActualHeight:0} DIP\n" +
            $"当前生效的文本整形模式 = {TextOptions.GetTextFormattingMode(this)}\n" +
            $"渲染层 = Tier {RenderCapability.Tier >> 16}，进程渲染模式 = {RenderOptions.ProcessRenderMode}\n" +
            $"系统 ClearType = FontSmoothing {Reg("FontSmoothing")} / Type {Reg("FontSmoothingType")} " +
            $"(1=灰度, 2=ClearType) / Gamma {Reg("FontSmoothingGamma")}\n\n");

        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var center = new Point32
            {
                X = screen.Bounds.Left + screen.Bounds.Width / 2,
                Y = screen.Bounds.Top + screen.Bounds.Height / 2
            };

            var monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
            GetDpiForMonitor(monitor, 0, out var monitorDpi, out _);
            var scale = monitorDpi / 96d;

            File.AppendAllText(log,
                $"显示器 {screen.DeviceName}{(screen.Primary ? "（主屏）" : "")}：" +
                $"{screen.Bounds.Width}x{screen.Bounds.Height} @ ({screen.Bounds.Left},{screen.Bounds.Top}) " +
                $"→ DPI {monitorDpi}，缩放 {scale * 100:0}%（同样 12 DIP 的字，这里是用 {12 * scale:0.#} 个像素画的）\n");
        }

        SaveShot(Path.Combine(dir, "01-window.png"));
        CaptureFullScreen(Path.Combine(dir, "02-screen.png"));

        static string Reg(string name) =>
            Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", name, "?")?.ToString() ?? "?";
    }

    /// <summary>
    /// 自检：搜索的本地优先策略。拿本地库里真实存在的一款游戏当关键词，
    /// 应当直接给本地结果（不联网），状态栏标「本地 xx 款」；退出搜索后回到原游戏库。
    /// </summary>
    public async Task RunLocalSearchCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "local-search.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && _library.Items.Count == 0; i++) await Task.Delay(100);
        await Task.Delay(600);

        var sample = _library.Items.FirstOrDefault();
        if (sample is null)
        {
            File.AppendAllText(log, "本地库是空的，先同步一次再来跑这个自检\n");
            return;
        }

        // 取标题里第一个够长的词当关键词，整串太长容易搜不到
        var key = sample.CleanTitle
            .Split(new[] { ' ', ':', '-', '·', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(w => w.Length >= 4) ?? sample.CleanTitle;

        SearchBox.Text = key;
        await SubmitSearchAsync();
        await Task.Delay(700);

        File.AppendAllText(log,
            $"本地卡片 {AppDatabase.Instance.CacheCount()} 条，关键词「{key}」\n" +
            $"结果来源={(_searchFromLocal ? "本地库（对）" : "官网（不对）")} " +
            $"结果 {_searchPager.TotalCount} 条，副标题=「{SectionSubtitle.Text}」，状态栏=「{StatusText.Text}」\n");

        SaveShot(Path.Combine(dir, "01-local-search.png"));

        // 再拿一个本地明显没有的词，确认会走官网（只记录来源，不要求联网成功）
        SearchBox.Text = "zzz-not-in-local-zzz";
        await SubmitSearchAsync();
        await Task.Delay(1200);
        File.AppendAllText(log,
            $"未命中的词：来源={(_searchFromLocal ? "本地（不对）" : "官网（对）")}，状态栏=「{StatusText.Text}」\n");

        ExitSearch(clearText: true);
        await Task.Delay(600);
        File.AppendAllText(log, $"退出搜索后：{_mode}，卡片 {_items.Count} 张\n");
        SaveShot(Path.Combine(dir, "02-back-library.png"));
    }

    /// <summary>
    /// 自检：弹框里两条进度是「并行」而不是排队。让中文名那一段真跑起来（会向 Steam 发几条请求），
    /// 同时把游戏数据那一段喂到 40%，同一张截图里能看到两条都在动；再用「取消同步」把两段一起停掉，
    /// 并且要求取消过程不抛任何非取消类异常。
    /// </summary>
    public async Task RunSyncComboCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "sync-combo.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && AppDatabase.Instance.GameNameCount() == 0; i++) await Task.Delay(100);

        var hits = new List<string>();
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is OperationCanceledException) return;
            if (e.Exception is System.Threading.Tasks.TaskCanceledException) return;
            hits.Add($"{e.Exception.GetType().Name}: {e.Exception.Message}");
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;

        try
        {
            _syncRunning = true;
            _syncCts = new CancellationTokenSource();
            ShowSyncDialog(SyncKind.All);

            // 游戏名那一段（名录 + Steam 中文名）立刻开跑，不等游戏数据
            var running = RunNameLaneAsync(withSteam: true, _syncCts.Token);

            // 游戏数据这一段同时走到 40%
            OnSyncProgress(new SyncProgress
            {
                Phase = SyncPhase.Syncing,
                Game = "Assassin's Creed Mirage",
                Detail = "第 3 / 52 页 · 正在更新",
                Done = 241,
                Total = 603,
                Ratio = 0.4,
                Updated = 6,
                Skipped = 235
            });
            await Task.Delay(700);
            SaveShot(Path.Combine(dir, "01-parallel.png"));

            File.AppendAllText(log,
                $"刚开跑：数据条={SyncPercentText.Text}（{SyncCountText.Text}），" +
                $"中文名条={SyncNamePercentText.Text}（{SyncNameCountText.Text}）\n");

            await Task.Delay(3200);
            SaveShot(Path.Combine(dir, "02-name-phase.png"));
            File.AppendAllText(log,
                $"并行中：数据条={SyncPercentText.Text}，中文名条={SyncNamePercentText.Text}" +
                $"（{SyncNameCountText.Text}，不确定={SyncNameBar.IsIndeterminate}），" +
                $"中文名状态=「{SyncNameStateText.Text}」\n" +
                $"Steam 节奏：{SteamWeb.PaceSummary}\n");

            // 「取消同步」应当把中文名这一段也停掉，已换到的名字保留
            PressButton(SyncCancelButton);
            await Task.WhenAny(running, Task.Delay(15000));
            await Task.Delay(700);

            File.AppendAllText(log,
                $"取消后：状态=「{SyncNameStateText.Text}」 百分比=「{SyncNamePercentText.Text}」 " +
                $"异常={string.Join(" | ", hits.Distinct().Take(6))}\n");

            CloseSyncDialog();
            _syncRunning = false;
            _syncCts?.Dispose();
            _syncCts = null;
            await Task.Delay(400);
            SaveShot(Path.Combine(dir, "03-after.png"));
            File.AppendAllText(log, $"收尾：弹框={SyncDialogOpen}\n");
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }
    }
    /// <summary>
    /// 自检：设置页「快速搜索游戏名」那一行，并<b>真的点一次</b>。
    /// 会联网打官网（正常只有 All Trainers 一个请求 + 补漏），验证它同样走模态进度框、
    /// 「游戏数据」那一段收起、点遮层关不掉；把名单条数、中文名保留数、搜索提示抽样写盘。
    /// </summary>
    public async Task RunQuickNamesCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "quick-names.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 300 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);

        NavList.SelectedIndex = 3;               // 设置
        await Task.Delay(800);
        FindScrollViewer(SettingsPanel)?.ScrollToTop();
        await Task.Delay(400);
        SaveShot(Path.Combine(dir, "01-settings-quick-names.png"));

        var before = await Task.Run(() => AppDatabase.Instance.GameNameCount());
        var beforeCn = await Task.Run(() => AppDatabase.Instance.GameNameCnCount());
        File.AppendAllText(log,
            $"按钮：{QuickNamesButton.Content}｜可点={(QuickNamesButton.IsEnabled ? "是" : "否")}｜" +
            $"全量按钮={(SyncAllButton.IsEnabled ? "可点" : "不可点")}\n" +
            $"更新前：{before} 条，其中有中文名 {beforeCn} 条\n");

        var started = System.Diagnostics.Stopwatch.StartNew();
        PressButton(QuickNamesButton);

        // 点「开始更新」不应当直接开跑：直接把进度框立起来，勾选项和开始按钮都在这一个框里
        await Task.Delay(250);
        File.AppendAllText(log,
            $"点后：准备框={(SyncDialogOpen ? "开（对）" : "关（错）")}｜标题=「{SyncTitle.Text}」\n" +
            $"勾选项={(SyncSteamOptionPanel.Visibility == Visibility.Visible ? "显示（对）" : "藏着（错）")}｜" +
            $"已勾={(SyncSteamCheck.IsChecked == true ? "是（错）" : "否（对）")}｜" +
            $"开始按钮={(SyncStartButton.Visibility == Visibility.Visible ? "显示（对）" : "藏着（错）")}｜" +
            $"游戏数据段={(SyncDataSection.Visibility == Visibility.Visible ? "显示（错）" : "收起（对）")}｜" +
            $"中文名段={(SyncCnSection.Visibility == Visibility.Visible ? "显示（对）" : "藏着（错）")}\n");
        SaveShot(Path.Combine(dir, "01-quick-names-setup.png"));

        // 点框里的「开始更新」才真的开跑：仍然是这一个框，勾选项变灰留着、进度条原地跑
        PressButton(SyncStartButton);

        await Task.Delay(200);
        File.AppendAllText(log,
            $"开始后：{QuickNamesButton.Content}｜可点={(QuickNamesButton.IsEnabled ? "是（错）" : "否（对）")}｜" +
            $"弹框={(SyncDialogOpen ? "开（对）" : "关（错）")}｜标题=「{SyncTitle.Text}」\n" +
            $"勾选项={(SyncSteamOptionPanel.Visibility == Visibility.Visible ? "原位留着（对）" : "消失了（错）")}｜" +
            $"可改={(SyncSteamOptionPanel.IsEnabled ? "还开着（错）" : "已锁（对）")}｜" +
            $"开始按钮={(SyncStartButton.Visibility == Visibility.Visible ? "原位留着（对）" : "消失了（错）")}" +
            $"/{SyncStartButton.Content}/{(SyncStartButton.IsEnabled ? "可点（错）" : "已锁（对）")}｜" +
            $"游戏数据段={(SyncDataSection.Visibility == Visibility.Visible ? "显示（错）" : "收起（对）")}｜" +
            $"中文名段={(SyncCnSection.Visibility == Visibility.Visible ? "原位留着（对）" : "消失了（错）")}" +
            $"（{(SyncCnSection.Opacity < 1 ? "灰显=本次跳过" : "正常")}）｜" +
            $"副标题=「{SyncSubtitle.Text}」\n");

        // 弹框开着的时候点遮层：模态框必须纹丝不动（流程只要 3 秒出头，所以要早点点）
        await Task.Delay(700);
        var backdrop = new Point(ActualWidth - 70, ActualHeight - 110);
        var wasOpen = SyncDialogOpen;
        await ClickAsync(backdrop);
        await Task.Delay(250);
        File.AppendAllText(log,
            $"点遮层（点击时弹框={(wasOpen ? "开着" : "已关")}）：" +
            $"{(SyncDialogOpen ? "仍打开（对）" : "被关掉了（错）")}\n");

        SaveShot(Path.Combine(dir, "02-quick-names-progress.png"));

        // 采样几条进度：应当看得到游戏名在滚
        for (var i = 0; i < 12 && SyncDialogOpen; i++)
        {
            await Task.Delay(250);
            if (i % 3 != 0) continue;

            // 名字行只在「正在换名」时才有内容，没开始就别把空的三段拼出来
            var pair = SyncCnPairText.Visibility == Visibility.Visible ? SyncCnPairText.Text : "（还没有开始换名）";
            File.AppendAllText(log,
                $"采样{i}: 「{SyncNameStateText.Text}」 明细=「{SyncNameDetailText.Text}」 " +
                $"计数=「{SyncNameCountText.Text}」 不确定={SyncNameBar.IsIndeterminate}｜" +
                $"中文名=「{SyncCnStateText.Text}」 {SyncCnCountText.Text} 名字行=「{pair}」\n");
        }

        SaveShot(Path.Combine(dir, "03-quick-names-writing.png"));

        for (var i = 0; i < 900 && SyncDialogOpen; i++) await Task.Delay(100);
        started.Stop();

        var after = await Task.Run(() => AppDatabase.Instance.GameNameCount());
        var afterCn = await Task.Run(() => AppDatabase.Instance.GameNameCnCount());
        var sample = string.Join(" / ",
            AppDatabase.Instance.SearchGameNames("elden", 3).Select(r => $"{r.TrainerId}={r.CleanEn}"));
        var byCn = string.Join(" / ",
            AppDatabase.Instance.SearchGameNames("赛博", 2).Select(r => $"{r.NameCn}→{r.CleanEn}"));

        File.AppendAllText(log,
            $"耗时：{started.Elapsed.TotalSeconds:F1}s｜按钮复原：{QuickNamesButton.Content} " +
            $"（可点={(QuickNamesButton.IsEnabled ? "是（对）" : "否（错）")}）｜收尾弹框={(SyncDialogOpen ? "仍开（错）" : "已关（对）")}\n" +
            $"更新后：{after} 条，其中有中文名 {afterCn} 条\n" +
            $"抽样（elden）：{sample}\n" +
            $"抽样（赛博）：{byCn}\n" +
            $"状态栏：{StatusText.Text}\n");

        await Task.Delay(300);
        SaveShot(Path.Combine(dir, "04-quick-names-done.png"));

        // 再来一次：这回在同一个框里勾上「同时更新 Steam 中文名」，点开始后应当多出中文名那条进度
        PressButton(QuickNamesButton);
        await Task.Delay(250);
        Sync.SteamChecked = true;
        await Task.Delay(120);
        SaveShot(Path.Combine(dir, "05-quick-names-setup-checked.png"));

        PressButton(SyncStartButton);
        await Task.Delay(400);
        File.AppendAllText(log,
            $"勾选后：副标题=「{SyncSubtitle.Text}」｜段标签=「{SyncNameLabel.Text}」｜" +
            $"游戏数据段={(SyncDataSection.Visibility == Visibility.Visible ? "显示（错）" : "收起（对）")}｜" +
            $"中文名段={(SyncCnSection.Visibility == Visibility.Visible ? "显示（对）" : "藏着（错）")}" +
            $"（{(SyncCnSection.Opacity < 1 ? "灰显（错）" : "正常（对）")}）｜" +
            $"勾选项={(SyncSteamOptionPanel.Visibility == Visibility.Visible ? "原位留着（对）" : "消失了（错）")}" +
            $"/{(SyncSteamOptionPanel.IsEnabled ? "还可改（错）" : "已锁（对）")}\n");
        SaveShot(Path.Combine(dir, "06-quick-names-steam.png"));

        // 真的跑一遍中文名要好几分钟，这里立刻取消，只验「路走对了」
        PressButton(SyncCancelButton);
        for (var i = 0; i < 300 && SyncDialogOpen; i++) await Task.Delay(100);
        File.AppendAllText(log,
            $"勾选那轮取消后：弹框={(SyncDialogOpen ? "仍开（错）" : "已关（对）")}｜状态栏=「{StatusText.Text}」\n");
    }
#endif

    /// <summary>
    /// 自检里「点一下按钮」：按钮现在挂的是 Command，直接 RaiseEvent 不会触发命令，
    /// 所以先照按钮自己的规矩走一遍命令；没挂命令的按钮才退回老办法。
    /// </summary>
    private static void PressButton(System.Windows.Controls.Button button)
    {
        if (button.Command is { } command)
        {
            if (command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter);
            return;
        }

        PressButton(button);
    }

    /// <summary>
    /// 自检：核对「已经收录」（SQLite 缓存条数）和「已加载」（内存里当前列表条数）两个数字，
    /// 顺便看看正常一轮加载要多久。
    /// </summary>
    public async Task RunCountCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "count.txt");
        File.WriteAllText(log, "");

        var started = Environment.TickCount64;
        for (var i = 0; i < 600 && (_firstLoading || _library.Items.Count == 0); i++) await Task.Delay(100);
        var elapsed = Environment.TickCount64 - started;

        File.AppendAllText(log,
            $"首次加载耗时：{elapsed} ms\n" +
            $"副标题：{SectionSubtitle.Text}\n" +
            $"状态栏：{StatusText.Text}\n" +
            $"内存条数：{_library.Items.Count} / SQLite：{AppDatabase.Instance.CacheCount()}\n" +
            $"收藏：{_favorites.Count} / 库可用：{AppDatabase.Instance.IsAvailable}\n");

        SaveShot(Path.Combine(dir, "count.png"));
    }

    /// <summary>
    /// 自检：圆角按钮的边缘。同一个窗口分别按 1.0 / 1.25 / 1.5 / 2.0 倍渲染出来，
    /// 再抓一张当前屏幕的真实像素，用来对比「100% 缩放」和「150% 缩放」下圆角有没有发黑。
    /// </summary>
    public async Task RunCornerCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "corner.txt");
        File.WriteAllText(log, $"实际 DPI 缩放 = {VisualTreeHelper.GetDpi(this).DpiScaleX:0.###}\n");

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(1200);

        NavList.SelectedIndex = 3;               // 设置
        await Task.Delay(700);
        Activate();
        await Task.Delay(400);

        CaptureWindowScreen(Path.Combine(dir, "blue-screen.png"));
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            SaveShotAtScale(Path.Combine(dir, $"blue-{scale * 100:0}.png"), scale);

        // 红按钮在确认框里，底色比卡片更暗，是最容易看出黑边的那颗
        PressButton(ClearCacheButton);
        await Task.Delay(700);

        CaptureWindowScreen(Path.Combine(dir, "red-screen.png"));
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            SaveShotAtScale(Path.Combine(dir, $"red-{scale * 100:0}.png"), scale);

        File.AppendAllText(log, "已输出 blue-*/red-* 各 1x 1.25x 1.5x 2x 以及屏幕实拍\n");
    }

    /// <summary>
    /// 自检：几种圆角画法并排渲染（纯填充 / 不同强度的内侧亮边 / 不同圆角半径），
    /// 在 1x 和 1.5x 下各出一张，用来看哪种在 100% 缩放下四角不会显得发黑。
    /// </summary>
    public Task RunRimCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);

        var variants = new (string name, double radius, Color? rim, double rimAlpha, double halo)[]
        {
            ("A-纯填充-r10", 10, null, 0, 0),
            ("B-亮边6%-r10", 10, Colors.White, 0.06, 0),
            ("C-亮边12%-r10", 10, Colors.White, 0.12, 0),
            ("D-纯填充-r12", 12, null, 0, 0),
            ("E-亮边6%-r12", 12, Colors.White, 0.06, 0),
            ("F-同色描边-r10", 10, Color.FromRgb(0x7A, 0xB2, 0xFF), 1.0, 0),
            ("G-同色衬底22%-r10", 10, null, 0, 0.22),
            ("H-同色衬底35%-r10", 10, null, 0, 0.35),
            ("I-同色衬底45%-r12", 12, null, 0, 0.45)
        };

        var host = new Grid
        {
            Width = 300,
            Height = 34 * variants.Length + 12 * (variants.Length + 1),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x28, 0x36))
        };

        for (var i = 0; i < variants.Length; i++)
        {
            var (_, radius, rim, alpha, halo) = variants[i];

            var slot = new Grid
            {
                Width = 116,
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 12 + i * (34 + 12), 0, 0)
            };

            // 衬底：比按钮大 1 DIP 的同色半透明圆角块。主填充的抗锯齿就只和这层同色混，
            // 不再直接和深色卡片底混，角上那圈"发黑"的过渡就被换成一圈很淡的同色光。
            if (halo > 0)
            {
                slot.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(radius + 1),
                    Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(halo * 255), 0x3B, 0x82, 0xF6))
                });
            }

            var button = new Border
            {
                Margin = new Thickness(halo > 0 ? 1 : 0),
                CornerRadius = new CornerRadius(radius),
                Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6))
            };

            if (rim is { } color)
            {
                button.BorderThickness = new Thickness(1);
                button.BorderBrush =
                    new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B));
            }

            slot.Children.Add(button);
            host.Children.Add(slot);
        }

        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();

        foreach (var scale in new[] { 1.0, 1.5 })
        {
            var bitmap = new RenderTargetBitmap(
                (int)Math.Round(host.Width * scale), (int)Math.Round(host.Height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(host);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(dir, $"rims-{scale * 100:0}.png"));
            encoder.Save(stream);
        }

        File.WriteAllText(Path.Combine(dir, "rims.txt"),
            string.Join("\n", variants.Select((v, i) => $"{i}: {v.name} radius={v.radius} rim={v.rim} alpha={v.rimAlpha}")));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 自检：首页右上角「最近更新 / 热门游戏」页签（选中态）的圆角描边在 1x 下发黑的对照实验。
    /// 背景 #0E1825、填充 #244A72、描边 #3974AE、圆角 8、高 32——与 LibraryTabButton 完全一致。
    /// </summary>
    public Task RunTabCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);

        var fill = Color.FromRgb(0x24, 0x4A, 0x72);
        var stroke = Color.FromRgb(0x39, 0x74, 0xAE);
        var pageBg = Color.FromRgb(0x0E, 0x18, 0x25);

        Border Pill(double radius, double thickness, Brush? brush)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(radius),
                Background = new SolidColorBrush(fill)
            };
            if (brush is not null)
            {
                b.BorderThickness = new Thickness(thickness);
                b.BorderBrush = brush;
            }
            return b;
        }

        // 每个槽位 140x44，槽间距 14，页签 116x32 居中放
        (string name, Func<UIElement> build)[] variants =
        [
            ("A-现状-描边1", () => Pill(8, 1, new SolidColorBrush(stroke))),
            ("B-衬底55%同色", () =>
            {
                var g = new Grid();
                g.Children.Add(new Border
                {
                    Margin = new Thickness(-1),
                    CornerRadius = new CornerRadius(9),
                    Background = new SolidColorBrush(Color.FromArgb(0x8C, stroke.R, stroke.G, stroke.B))
                });
                g.Children.Add(Pill(8, 1, new SolidColorBrush(stroke)));
                return g;
            }),
            ("C-衬底纯填充", () =>
            {
                var g = new Grid();
                g.Children.Add(new Border
                {
                    Margin = new Thickness(-1),
                    CornerRadius = new CornerRadius(9),
                    Background = new SolidColorBrush(fill)
                });
                g.Children.Add(Pill(8, 1, new SolidColorBrush(stroke)));
                return g;
            }),
            ("D-内嵌描边", () =>
            {
                var g = new Grid();
                g.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(fill) });
                g.Children.Add(new Border
                {
                    Margin = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(stroke)
                });
                return g;
            }),
            ("E-描边1.5", () => Pill(8, 1.5, new SolidColorBrush(stroke))),
        ];

        var host = new Grid
        {
            Width = 180,
            Height = variants.Length * 44 + (variants.Length + 1) * 14,
            Background = new SolidColorBrush(pageBg)
        };

        for (var i = 0; i < variants.Length; i++)
        {
            var slot = new Grid
            {
                Width = 140,
                Height = 44,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 14 + i * (44 + 14), 0, 0)
            };
            slot.Children.Add(variants[i].build());
            host.Children.Add(slot);
        }

        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();

        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var bitmap = new RenderTargetBitmap(
                (int)Math.Round(host.Width * scale), (int)Math.Round(host.Height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(host);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(dir, $"tabs-{scale * 100:0}.png"));
            encoder.Save(stream);
        }

        File.WriteAllText(Path.Combine(dir, "tabs.txt"),
            string.Join("\n", variants.Select((v, i) => $"{i}: {v.name}")));
        return Task.CompletedTask;
    }

    /// <summary>按给定倍数把整个窗口渲染成一张图（相当于那台机器按这个 DPI 缩放渲染）。</summary>
    private void SaveShotAtScale(string path, double scale)
    {
        try
        {
            var width = (int)Math.Round(ActualWidth * scale);
            var height = (int)Math.Round(ActualHeight * scale);
            if (width <= 0 || height <= 0) return;

            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(this);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch
        {
            // 截图失败不影响自检
        }
    }

    /// <summary>抓当前屏幕上这块窗口的真实像素（硬件渲染的结果，不是 RenderTargetBitmap）。</summary>
    private void CaptureWindowScreen(string path)
    {
        try
        {
            var origin = PointToScreen(new Point(0, 0));
            var width = (int)Math.Round(ActualWidth);
            var height = (int)Math.Round(ActualHeight);
            if (width <= 0 || height <= 0) return;

            using var bitmap = new System.Drawing.Bitmap(width, height);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen((int)Math.Round(origin.X), (int)Math.Round(origin.Y), 0, 0,
                new System.Drawing.Size(width, height));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch
        {
            // 抓不到屏幕也不影响自检
        }
    }

    /// <summary>抓整块屏幕（工具提示、Popup 都是独立 HWND，窗口截图里拍不到）。</summary>
    private void CaptureFullScreen(string path)
    {
        try
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen is null) return;

            var bounds = screen.Bounds;
            using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch
        {
            // 抓不到屏幕也不影响自检
        }
    }

    /// <summary>按类型找视觉树里的所有子元素。</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;

            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>
    /// 自检：把窗口从 1080 一路摆到 3840 宽，记录每档的版心宽度、卡片尺寸、行数，
    /// 用来核对「固定版心 + 一行 5 张」在 1080 / 2K / 4K 上的表现。
    /// </summary>
    public async Task RunLayoutCheckAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "layout.txt");
        File.WriteAllText(log, "");

        for (var i = 0; i < 200 && _firstLoading; i++) await Task.Delay(100);
        await Task.Delay(2000);

        // 这台机器的屏幕可能不大：放开尺寸限制，只验证布局算法
        MinWidth = 320;
        MinHeight = 320;
        MaxWidth = 6000;
        MaxHeight = 3000;

        foreach (var width in new[] { 1080, 1280, 1440, 1920, 2560, 3840 })
        {
            Width = width;
            Height = 860;
            await Task.Delay(650);

            var perRow = Library.Rows.Count > 0 ? Library.Rows[0].Items.Count : 0;
            var leftGap = Math.Max(0, (MainArea.ActualWidth - CardArea.ActualWidth) / 2);
            File.AppendAllText(log,
                $"窗口 {width,5} / 实际 {ActualWidth,5:F0} / 右栏 {MainArea.ActualWidth,5:F0} → " +
                $"版心 {CardArea.ActualWidth,5:F0}（两侧各留 {leftGap,4:F0}） " +
                $"卡片 {_cardW}x{_cardH} 首行 {perRow} 条 / 共 {Library.Rows.Count} 行\n");

            if (width is 1080 or 1440 or 1920 or 3840) SaveShot(Path.Combine(dir, $"layout-{width}.png"));
        }

        // 滚到中间再改宽度，验证行高变化后仍停在原来那一行
        Width = 1440;
        await Task.Delay(500);
        _listScroll ??= FindScrollViewer(CardList);
        _listScroll?.ScrollToVerticalOffset(400);
        await Task.Delay(400);
        var before = _listScroll?.VerticalOffset ?? 0;

        Width = 2560;
        await Task.Delay(900);
        var after = _listScroll?.VerticalOffset ?? 0;

        File.AppendAllText(log,
            $"改宽度前后滚动位置：{before:F0} → {after:F0}（行高 {CardRowGap + _cardH:F0}，" +
            $"等效行号 {after / (CardRowGap + _cardH):F2}）\n");
    }

    /// <summary>把任意窗口渲染成 PNG（用于托盘小菜单这种独立窗口）。</summary>
    private static void SaveShotOf(Window window, string path)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            var width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);
            if (width <= 0 || height <= 0) return;

            var bitmap = new RenderTargetBitmap(width, height,
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch
        {
            // 截图失败不影响自检
        }
    }

    /// <summary>整屏截图：用来核对右下角托盘区里有没有那只图标；顺带截一条任务栏，看还有没有程序按钮。</summary>
    private static void CaptureScreen(string path)
    {
        try
        {
            var bounds = WinForms.Screen.PrimaryScreen?.Bounds;
            if (bounds is null) return;

            using var bitmap = new Drawing.Bitmap(bounds.Value.Width, bounds.Value.Height);
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Value.Location, Drawing.Point.Empty, bounds.Value.Size);
            bitmap.Save(path, Drawing.Imaging.ImageFormat.Png);

            // 任务栏那条单独裁一份：整屏缩略图里那几个像素看不清
            var stripHeight = Math.Min(80, bounds.Value.Height / 4);
            using var strip = new Drawing.Bitmap(bounds.Value.Width, stripHeight);
            using (var graphics = Drawing.Graphics.FromImage(strip))
                graphics.DrawImage(bitmap,
                    new Drawing.Rectangle(0, 0, strip.Width, strip.Height),
                    new Drawing.Rectangle(0, bitmap.Height - stripHeight, bitmap.Width, stripHeight),
                    Drawing.GraphicsUnit.Pixel);

            strip.Save(Path.Combine(Path.GetDirectoryName(path) ?? "", "tray-taskbar.png"),
                Drawing.Imaging.ImageFormat.Png);
        }
        catch
        {
            // 截图失败不影响自检
        }
    }

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
        DetailBackdrop.RaiseEvent(maskClick);
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
        CloseSuggest();
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
