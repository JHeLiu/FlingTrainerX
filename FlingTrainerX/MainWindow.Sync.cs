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
/// 主窗口的一部分：数据更新：同步菜单、名录与中文名、进度面板。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 首页只读本地，所以右上角这个按钮不再是「联网刷新」，而是「重新读一遍本地库」：
    /// 主要给同步完成之后手动刷一次用，读的是 SQLite，秒回，不碰网络。
    /// 要同步官网数据，鼠标停在按钮上会展开「同步已有游戏 / 同步全部游戏」菜单。
    /// </summary>
    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;

        var rotate = new RotateTransform();
        RefreshGlyph.RenderTransform = rotate;
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });

        RefreshButton.IsEnabled = false;

        try
        {
            ReloadFromLocal();

            StatusText.Text = _library.Items.Count > 0
                ? $"已重新读取本地数据 · {_library.Items.Count} 款"
                : "本地还没有数据，去「设置 → 数据更新」更新一次";
        }
        finally
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            RefreshButton.IsEnabled = true;
            _busy = false;
        }
    }

    // ==================== 顶栏「刷新」按钮上的同步菜单 ====================

    /// <summary>鼠标移到刷新按钮上：展开同步菜单。</summary>
    private void RefreshHost_MouseEnter(object sender, MouseEventArgs e) => OpenSyncMenu();

    /// <summary>展开同步菜单：正在同步时两项置灰，免得点了没反应。</summary>
    private void OpenSyncMenu()
    {
        _syncMenuTimer.Stop();

        var busy = _syncRunning;
        SyncMenuExisting.IsEnabled = !busy;
        SyncMenuAll.IsEnabled = !busy;

        SyncMenuPopup.IsOpen = true;
    }

    /// <summary>鼠标离开按钮：先起计时，进了菜单就不收。</summary>
    private void RefreshHost_MouseLeave(object sender, MouseEventArgs e) => _syncMenuTimer.Start();

    /// <summary>鼠标进到菜单里：取消收起。</summary>
    private void SyncMenu_MouseEnter(object sender, MouseEventArgs e) => _syncMenuTimer.Stop();

    /// <summary>鼠标离开菜单：同样起计时，回到按钮上会自己取消。</summary>
    private void SyncMenu_MouseLeave(object sender, MouseEventArgs e) => _syncMenuTimer.Start();

    /// <summary>计时到点：鼠标真的不在按钮 / 菜单上了才收起。</summary>
    private void SyncMenuTimer_Tick(object? sender, EventArgs e)
    {
        if (RefreshHost.IsMouseOver || SyncMenuBorder.IsMouseOver)
        {
            _syncMenuTimer.Stop();
            return;
        }

        // 按住左键多半正在拖窗口：这会儿先不收起，松手之后再判一次。
        // 否则菜单会在拖动途中突然消失，看起来比「跟着走」更像 BUG
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            _syncMenuTimer.Start();
            return;
        }

        _syncMenuTimer.Stop();
        SyncMenuPopup.IsOpen = false;
    }

    /// <summary>菜单里的「同步已有游戏」：按本地库逐款重抓详情，再补中文名。</summary>
    private void SyncMenuExisting_Click(object sender, RoutedEventArgs e)
    {
        CloseSyncMenu();
        _ = RunSyncAsync(SyncKind.Existing);
    }

    /// <summary>菜单里的「同步全部游戏」：官网全量列表 + 封面，再补中文名。</summary>
    private void SyncMenuAll_Click(object sender, RoutedEventArgs e)
    {
        CloseSyncMenu();
        _ = RunSyncAsync(SyncKind.All);
    }

    /// <summary>收起同步菜单（点条目、点窗口别处、切走窗口时都调它）。</summary>
    private void CloseSyncMenu()
    {
        if (!SyncMenuPopup.IsOpen) return;

        _syncMenuTimer.Stop();
        SyncMenuPopup.IsOpen = false;
    }

    /// <summary>
    /// 窗口被拖动时，把已经打开的弹出层重新摆一次位置。
    ///
    /// Popup 是挂在**独立窗口**上的，它的位置只在「目标元素自己发生布局变化」时才重算；
    /// 拖整个窗口并不会让按钮重新布局，于是菜单就留在屏幕原地，窗口从它下面溜走 —— 这就是之前那个 BUG。
    /// 解决办法是把偏移量抖 0.01 像素再抖回来，逼 Popup 按目标的新屏幕坐标重新定位（肉眼看不出来）。
    /// </summary>
    private void OnWindowLocationChanged(object? sender, EventArgs e) => TrackOpenPopups();

    /// <summary>让所有开着的弹出层跟着窗口走（拖动、改尺寸、换显示器时调）。</summary>
    private void TrackOpenPopups()
    {
        Nudge(SyncMenuPopup);
        Nudge(SuggestPopup);

        static void Nudge(System.Windows.Controls.Primitives.Popup? popup)
        {
            if (popup is null || !popup.IsOpen) return;

            var offset = popup.HorizontalOffset;
            popup.HorizontalOffset = offset + 0.01;
            popup.HorizontalOffset = offset;
        }
    }

    // ==================== 游戏名库：Steam 中文名 ====================

    /// <summary>游戏名那一段的产出，更新结束时拼到状态栏上。</summary>
    /// <param name="Names">名录里一共多少款。</param>
    /// <param name="Kept">重建名录时保留下来的中文名条数。</param>
    /// <param name="Missing">需要去 Steam 查的有多少款（只更新名录时为 0）。</param>
    /// <param name="Got">本轮真正换到的中文名条数。</param>
    /// <param name="Blocked">Steam 一直连不上，提前收工。</param>
    /// <param name="Canceled">被用户取消。</param>
    /// <param name="Error">名录没拿到 / 写库失败时的原因。</param>
    private sealed record NameLaneResult(
        int Names, int Kept, int Missing, int Got, bool Blocked, bool Canceled, string? Error);

    /// <summary>
    /// 更新弹框的第二段：先把官网 All Trainers 名录重抓一遍写进本地库，再（可选）去 Steam 换中文名。
    /// <para>
    /// 和游戏数据那段<b>同时开跑</b>：名录走官网名录页、中文名走 Steam、详情走官网详情页，
    /// 三条路互不相干，写的也是不同的表（<c>game_names</c> / <c>trainer_cache</c>），所以谁也不等谁。
    /// </para>
    /// <para>
    /// 整段「安静退出」：取消、官网抽风、Steam 连不上都只体现在返回值里，不往外抛异常。
    /// </para>
    /// </summary>
    /// <param name="withSteam">true = 名录换完继续补中文名；false = 只更新名录（设置里的「快速搜索游戏名」）。</param>
    private async Task<NameLaneResult> RunNameLaneAsync(bool withSteam, CancellationToken ct)
    {
        List<GameNameRow> rows;
        int kept;

        try
        {
            ShowNameStageFetching();

            var fetchProgress = new Progress<NameFetchProgress>(
                p => ShowNameStageFetch(p.Count, p.Current, p.Detail));

            // 一个请求拿全名录（官网页面是快照，内部会自动补上最近上架的游戏）
            rows = await TrainerWeb.FetchAllNamesAsync(ct, fetchProgress);

            if (rows.Count == 0)
            {
                ShowNameStageBroken("官网没有返回游戏名");
                return new NameLaneResult(0, 0, 0, 0, false, ct.IsCancellationRequested, "官网没有返回游戏名");
            }

            // 防线：官网改版 / 被拦时常常只解析出零头，这时候宁可不动，也不要把本地名录清空重写
            var existing = await Task.Run(() => AppDatabase.Instance.GameNameCount(), ct);
            if (existing > 200 && rows.Count < existing * 0.6)
            {
                var why = $"官网只返回了 {rows.Count} 款（本地已有 {existing} 款），先保留本地名录不动";
                ShowNameStageBroken(why);
                return new NameLaneResult(0, 0, 0, 0, false, false, why);
            }

            ShowNameStageWriting(rows.Count);

            // 整表重建（保留已换到的中文名）放后台：几千行写库不占 UI 线程
            kept = await Task.Run(() => AppDatabase.Instance.RebuildGameNames(rows), ct);
        }
        catch (OperationCanceledException)
        {
            ShowNameStageCancelled();
            return new NameLaneResult(0, 0, 0, 0, false, true, null);
        }
        catch (Exception ex)
        {
            ShowNameStageBroken(ex.Message);
            return new NameLaneResult(0, 0, 0, 0, false, false, ex.Message);
        }

        // 单独显示两条进度时，游戏名这条到这儿就算完成，先打满；
        // 中文名那条接在后面单独走（不勾同步时这一条整块藏着）
        if (Sync.SplitLayout) ShowNameStageListDone(rows.Count, kept);

        // 只更新名录：到此为止，不碰 Steam
        if (!withSteam)
        {
            if (!Sync.SplitLayout) ShowNameStageListDone(rows.Count, kept);
            return new NameLaneResult(rows.Count, kept, 0, 0, false, false, null);
        }

        List<GameNameRow> missing;
        try
        {
            missing = await Task.Run(() => AppDatabase.Instance.LoadGameNamesMissingCn(), ct);
        }
        catch (Exception ex)
        {
            ShowNameStageBroken(ex.Message);
            return new NameLaneResult(rows.Count, kept, 0, 0, false, false, ex.Message);
        }

        if (missing.Count == 0)
        {
            ShowNameStageDone();
            return new NameLaneResult(rows.Count, kept, 0, 0, false, false, null);
        }

        ShowNameStageRunning(missing.Count);

        var progress = new Progress<(int Done, int Total, string English, string Chinese)>(
            p => ShowNameStageProgress(p.Done, p.Total, p.English, p.Chinese));

        var (got, blocked) = await FillChineseNamesAsync(missing, ct, progress);

        var cancelled = ct.IsCancellationRequested;

        if (cancelled) ShowNameStageCancelled();
        else if (blocked) ShowNameStageBlocked();
        else ShowNameStageDone();

        return new NameLaneResult(rows.Count, kept, missing.Count, got, blocked, cancelled, null);
    }

    /// <summary>
    /// 去 Steam 换中文名：3 路并发，但发请求的节奏由 <see cref="SteamWeb"/> 里的全局闸统一控制
    /// （约 1 秒一条，撞 429 自动整体冷却、再放慢），并发只是让等待时间被别的请求利用起来。
    /// 拿到一个就写一个（取消也不丢）；连着失败太多次（基本等于 Steam 不可达）就提前收工。
    /// </summary>
    private async Task<(int Got, bool Blocked)> FillChineseNamesAsync(
        List<GameNameRow> rows,
        CancellationToken ct,
        IProgress<(int Done, int Total, string English, string Chinese)> progress)
    {
        var done = 0;
        var got = 0;
        var fails = 0;
        var blocked = 0;          // 0/1：Steam 一直连不上，排队中的那些直接收工
        var lastPush = 0L;

        await Parallel.ForEachAsync(
            rows,
            new ParallelOptions { MaxDegreeOfParallelism = 3 },
            async (row, _) =>
            {
                // 用户取消，或已经判定 Steam 连不上：直接结束，不抛异常
                if (ct.IsCancellationRequested || Volatile.Read(ref blocked) != 0) return;

                var name = await SteamWeb.FetchChineseNameAsync(SteamQueryName(row.NameEn), ct)
                    .ConfigureAwait(false);

                // 取消瞬间拿到的是 null，别把它记成「Steam 连不上」
                if (ct.IsCancellationRequested) return;

                var n = Interlocked.Increment(ref done);

                if (name is null)
                {
                    // 请求本身失败：连着 12 次且一条中文名都没拿到，就别再试了
                    if (Interlocked.Increment(ref fails) >= 12 && Volatile.Read(ref got) == 0)
                        Volatile.Write(ref blocked, 1);
                }
                else if (name.Length > 0)
                {
                    Interlocked.Increment(ref got);
                    Interlocked.Exchange(ref fails, 0);

                    try { AppDatabase.Instance.UpdateGameNameCn(row.TrainerId, name); }
                    catch { /* 单条写库失败不该打断整轮同步 */ }
                }
                else
                {
                    Interlocked.Exchange(ref fails, 0);
                }

                // UI 不用每条都刷：200ms 推一次足够顺，也不会把界面线程喂爆
                var now = Environment.TickCount64;
                if (n == rows.Count || now - Interlocked.Read(ref lastPush) >= 200)
                {
                    Interlocked.Exchange(ref lastPush, now);
                    // 英文名和中文名一起报：面板上按「英文名 >> 中文名」显示，
                    // 没换到中文的就只留英文（中文那一侧写空，由面板补占位符）
                    progress.Report((n, rows.Count, row.CleanEn, name ?? ""));
                }
            });

        return (got, blocked != 0);
    }

    /// <summary>拿去 Steam 搜的名字：去掉官网标题里的 " Trainer" / " Enhanced" 后缀。</summary>
    private static string SteamQueryName(string officialTitle) =>
        officialTitle.Replace(" Trainer", "").Replace(" Enhanced", "").Trim();

    // ==================== 进度面板：游戏名 + 中文名 ====================

    // 同一套进度只在模态弹框里跑：三个更新入口都往同一份 VM（Sync）里写。
    // 这里只说「该显示什么」，具体哪个控件、怎么绑，全在 XAML 上。

    /// <summary>名录那一条进度线（不分开显示时，中文名也写在这一条上）。</summary>
    private ProgressLane NameLane => Sync.Names;

    /// <summary>中文名那一条进度线：两条分开显示时各走各的条，否则和名录共用一条。</summary>
    private ProgressLane CnLane => Sync.SplitLayout ? Sync.Chinese : Sync.Names;

    /// <summary>开始读官网名录：总数还不知道，进度条先滚动。</summary>
    private void ShowNameStageFetching()
    {
        _nameStageCn = false;
        NameLane.Rolling("正在读取官网游戏名录…", "正在连接 flingtrainer.com");
    }

    /// <summary>名录边读边报：面板上滚动显示刚读到的游戏名，右边写已读到多少款。</summary>
    private void ShowNameStageFetch(int count, string current, string detail)
    {
        var lane = NameLane;
        lane.Indeterminate = true;
        lane.Percent = "";
        lane.State = current.Length > 0 ? $"正在读取官网名录 · {current}" : "正在读取官网游戏名录…";
        lane.Detail = detail;
        lane.Count = count > 0 ? $"已读取 {count} 款" : "";
    }

    /// <summary>名录读完了，正在整表写进本地库。</summary>
    private void ShowNameStageWriting(int total)
    {
        _nameStageCn = false;
        NameLane.Rolling("正在写入本地数据库…", $"共 {total} 款，重建游戏名库（已换到的中文名会保留）");
    }

    /// <summary>只更新名录时的收尾：进度条打满，写明多少款、保留了多少中文名。</summary>
    private void ShowNameStageListDone(int names, int kept)
    {
        _nameStageCn = false;
        NameLane.Finish("游戏名更新完成", kept > 0 ? $"共 {names} 款，保留 {kept} 个中文名" : $"共 {names} 款");
    }

    /// <summary>开始换名：有总数，进度条能算百分比。</summary>
    private void ShowNameStageRunning(int total)
    {
        _nameStageCn = true;

        // 两条分开显示时，这一段属于「Steam 中文名」，游戏名那条已经打满了
        CnLane.Start(total, "正在从 Steam 换取中文名");
    }

    /// <summary>换一条刷一下：面板上看到「英文名 >> 中文名」和整体百分比。</summary>
    private void ShowNameStageProgress(int done, int total, string english, string chinese)
    {
        var lane = CnLane;
        lane.Report(done, total, "正在从 Steam 换取中文名");
        lane.ShowNamePair(english, chinese);
    }

    private void ShowNameStageDone()
    {
        var lane = CnLane;
        lane.Finish(
            Sync.SplitLayout ? "中文名更新完成" : "游戏名与中文名更新完成",
            $"共 {AppDatabase.Instance.GameNameCount()} 款，其中 {AppDatabase.Instance.GameNameCnCount()} 款有中文名");
    }

    private void ShowNameStageCancelled()
    {
        // 停在哪一段就写在哪一段：名录还没读完不能把「已取消」写到中文名那条上
        var cn = Sync.SplitLayout && _nameStageCn;
        var lane = cn ? Sync.Chinese : Sync.Names;

        // 取消只换说明，进度条和百分比停在原地（已经跑到哪儿一目了然）
        lane.Stop("已取消，已经更新好的部分都保留", clearPercent: false);

        // 名录还没写完就停了：中文名那条压根没机会开始，别让它一直挂着「等待…」
        if (Sync.SplitLayout && !_nameStageCn)
            Sync.Chinese.Stop("游戏名没更新完，这一步没有开始");
    }

    private void ShowNameStageBlocked()
    {
        CnLane.Stop("Steam 连不上，中文名先停在这儿", "网络恢复后再更新一次，已换到的会自动跳过");
    }

    private void ShowNameStageBroken(string message)
    {
        Sync.Names.Stop("游戏名更新失败", message);

        if (!Sync.SplitLayout) return;

        Sync.Chinese.Stop("游戏名没读到，这一步没有开始");
    }

    /// <summary>
    /// 启动时准备游戏名库：表里有货就跳过；空的才去抓一份（只抓英文名，很便宜）。
    /// 只做静默准备——启动不弹任何框；中文名在「同步全部 / 已有游戏数据」时顺带补齐。
    /// </summary>
    private async Task SeedGameNamesAsync()
    {
        // 查库和写库都挪到后台线程：这张表有几千行，整批写进去不能占着 UI 线程
        var empty = await Task.Run(() => AppDatabase.Instance.IsAvailable && AppDatabase.Instance.GameNameCount() == 0);
        if (!empty) return;

        try
        {
            var rows = await TrainerWeb.FetchAllNamesAsync();
            if (rows.Count == 0) return;

            await Task.Run(() =>
            {
                if (AppDatabase.Instance.GameNameCount() == 0) AppDatabase.Instance.SaveGameNames(rows);
            });
        }
        catch
        {
            // 抓不到就下次启动再试，不影响主流程
        }
    }

    // ==================== 设置 · 数据更新 ====================

    /// <summary>首页空状态里的「更新数据」：和设置里的「更新全部游戏数据」是同一条路。</summary>
    private void EmptySync_Click(object sender, RoutedEventArgs e) => _ = RunSyncAsync(SyncKind.All);

    private void SyncAll_Click(object sender, RoutedEventArgs e) => _ = RunSyncAsync(SyncKind.All);

    private void SyncExisting_Click(object sender, RoutedEventArgs e) => _ = RunSyncAsync(SyncKind.Existing);

    /// <summary>
    /// 设置里的「快速搜索游戏名」：先把官网 All Trainers 名录写进 game_names，
    /// 勾了可选项才接着去 Steam 换中文名（默认不勾）。
    /// <para>
    /// 点「开始更新」不立刻开跑，而是把进度框直接立起来：两条进度条、
    /// 「同时更新 Steam 中文名」勾选项和「开始更新」按钮都在同一个框里，
    /// 用户勾好再点按钮才真正开工 —— 不再先弹一个小确认框、再弹一个进度框。
    /// </para>
    /// </summary>
    private async void QuickNames_Click(object sender, RoutedEventArgs e)
    {
        if (_syncRunning || _syncStart is not null)
        {
            StatusText.Text = "正在更新，等这一轮跑完再开始下一轮";
            StatusDot.Fill = (Brush)FindResource("B.Accent");
            return;
        }

        _syncStart = new TaskCompletionSource<(bool Ok, bool WithSteam)>();
        SetUpdateButtonsBusy(SyncKind.Names);
        ShowQuickNamesSetup();

        var (ok, withSteam) = await _syncStart.Task;
        _syncStart = null;

        if (!ok)
        {
            // 只是关掉准备框，没开跑，按钮直接复原
            CloseSyncDialog();
            SetUpdateButtonsBusy(null);
            return;
        }

        await RunSyncAsync(SyncKind.Names, withSteam, fromSetup: true);
    }

    /// <summary>进度框是否开着（模态：点遮层、按 Esc 都关不掉）。</summary>
    private bool SyncDialogOpen => Sync.IsOpen;

    /// <summary>
    /// 跑一次更新，三个入口共用：更新全部 / 更新已有 / 快速更新游戏名。
    /// <para>
    /// 「游戏数据」和「游戏名 + 中文名」是两条完全独立的线：一条走官网列表 / 详情页写
    /// <c>trainer_cache</c>，另一条走官网名录页 + Steam 写 <c>game_names</c>，
    /// <b>同时开跑、互不等待、互不干扰</b>，弹框上下两条进度各跟各的线走。
    /// 只更新游戏名时不跑第一条线，弹框里也只留一条进度。
    /// </para>
    /// <para>
    /// 中途取消会把两条线一起停掉，已经写进库的都保留。抓取全在后台，进度通过
    /// <see cref="Progress{T}"/> 回到 UI 线程；不管成功、失败还是被取消，结束时都收起弹框并重读本地库。
    /// </para>
    /// </summary>
    /// <param name="kind">更新全部 / 更新已有 / 只更新游戏名。</param>
    /// <param name="withSteam">
    /// 只更新游戏名时是否连带更新 Steam 中文名（准备框里那个勾选项，默认 false）。
    /// 更新全部 / 更新已有总是 true，传进来也不影响。
    /// </param>
    /// <param name="fromSetup">
    /// true = 准备框已经立起来（用户在里面勾好、点了「开始更新」），
    /// 这里只把框切成「跑起来」的样子；false = 现在就开框。
    /// </param>
    private async Task RunSyncAsync(SyncKind kind, bool withSteam = true, bool fromSetup = false)
    {
        if (_syncRunning || _syncStart is not null)
        {
            StatusText.Text = "正在更新，等这一轮跑完再开始下一轮";
            StatusDot.Fill = (Brush)FindResource("B.Accent");
            return;
        }

        // 本地一条都没有时，「更新已有游戏数据」无事可做，先说明白再返回
        if (kind == SyncKind.Existing && AppDatabase.Instance.CacheCount() == 0)
        {
            StatusText.Text = "本地还没有数据，先点「更新全部游戏数据」";
            StatusDot.Fill = (Brush)FindResource("B.Accent");
            return;
        }

        _syncRunning = true;
        _syncCts?.Dispose();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;

        SetUpdateButtonsBusy(kind);
        if (fromSetup) PrepareSyncDialogRunning(kind, withSteam);
        else ShowSyncDialog(kind, withSteam);

        var result = new SyncResult();
        NameLaneResult? nameResult = null;
        Task<NameLaneResult>? nameTask = null;
        Task<SyncResult>? dataTask = null;

        try
        {
            var progress = new Progress<SyncProgress>(OnSyncProgress);

            // 第一条线：游戏数据（官网列表 / 本地详情页）。只更新游戏名时不开这条线
            if (kind != SyncKind.Names)
            {
                dataTask = kind == SyncKind.All
                    ? TrainerSyncService.SyncAllAsync(progress, ct)
                    : TrainerSyncService.SyncExistingAsync(progress, ct);
            }

            // 第二条线：官网名录 + （可选）Steam 中文名，和第一条同时开跑。
            // 只更新游戏名时，中文名要不要一起换由确认框里那个勾决定
            nameTask = RunNameLaneAsync(kind != SyncKind.Names || withSteam, ct);

            if (dataTask is null)
            {
                // 只有名录这一段：跑完直接收尾，不用再等别的
                nameResult = await nameTask;
            }
            else
            {
                await Task.WhenAll(dataTask, nameTask);
                result = dataTask.Result;
                nameResult = nameTask.Result;

                // 让「100%」在屏幕上停一下，别一闪就收框
                if (!result.Canceled && !ct.IsCancellationRequested) await Task.Delay(900);
            }
        }
        catch (OperationCanceledException)
        {
            result = new SyncResult { Canceled = true };
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 取消时的连接中断（IOException 等）不是失败
            result = new SyncResult { Canceled = true };
        }
        catch (Exception ex)
        {
            // 一条线崩了：把另一条也一起停掉，并等它收尾，别让它关掉弹框之后还在后台写库
            _syncCts?.Cancel();
            if (nameTask is not null)
            {
                try { await nameTask; } catch { }
            }

            result = new SyncResult { Error = ex.Message };
        }
        finally
        {
            CloseSyncDialog();
            SetUpdateButtonsBusy(null);
            _syncRunning = false;
            _syncCts?.Dispose();
            _syncCts = null;
        }

        // 更新写的是本地库，首页跟着重读一遍才看得到新数据；
        // 只更新游戏名时没碰卡片表，不用动首页（搜索提示每次都是现查库）
        if (kind != SyncKind.Names) ReloadFromLocal();

        if (kind == SyncKind.Names)
        {
            StatusText.Text = nameResult switch
            {
                null => "更新游戏名结束",
                { Error: not null } => $"更新游戏名失败：{nameResult.Error}",
                { Canceled: true } => "更新游戏名已取消",
                _ => DescribeQuickNames(nameResult, withSteam)
            };
            StatusDot.Fill = (Brush)FindResource(
                nameResult is { Error: null, Canceled: false } ? "B.Green" : "B.Accent");
            return;
        }

        StatusText.Text = result.Summary + DescribeNameLane(nameResult);
        StatusDot.Fill = (Brush)FindResource(result.Error is null && !result.Canceled ? "B.Green" : "B.Accent");
    }

    /// <summary>
    /// 「快速更新游戏名」的状态栏文案：名录条数 + 保留的中文名，勾了中文名再加本轮补到的条数。
    /// </summary>
    /// <param name="r">游戏名那一段的结果。</param>
    /// <param name="withSteam">这一轮有没有连带更新中文名。</param>
    private static string DescribeQuickNames(NameLaneResult r, bool withSteam)
    {
        var text = $"游戏名已更新：共 {r.Names} 款";
        if (r.Kept > 0) text += $"，保留 {r.Kept} 个中文名";
        if (!withSteam) return text;

        return text + (r.Blocked
            ? " · Steam 连不上"
            : r.Got > 0
                ? $" · 补到 {r.Got} 个中文名"
                : " · 中文名没有新增");
    }

    /// <summary>把「游戏名 + 中文名」那一段的结果拼成状态栏后缀。</summary>
    private static string DescribeNameLane(NameLaneResult? r) => r switch
    {
        null => "",
        { Error: not null } => " · 游戏名更新失败",
        { Canceled: true } => " · 游戏名已取消",
        { Blocked: true } => $" · 名录 {r.Names} 款 · Steam 连不上",
        { Got: > 0 } => $" · 名录 {r.Names} 款 · 补到 {r.Got} 个中文名",
        _ => $" · 名录 {r.Names} 款 · 中文名无新增"
    };

    /// <summary>三颗入口按钮统一置忙 / 复原（显隐与文案见 <see cref="SettingsViewModel.ShowRunning"/>）。</summary>
    private void SetUpdateButtonsBusy(SyncKind? running) => Settings.ShowRunning(running);

    /// <summary>
    /// 只更新游戏名时的「准备框」：把两条进度和「是否同步中文名」一次性摆出来，
    /// 用户勾好点「开始更新」才开工（按钮走 <see cref="SyncViewModel.StartCommand"/>）。
    /// </summary>
    private void ShowQuickNamesSetup()
    {
        Sync.SplitLayout = true;

        Sync.Title = "快速更新游戏名";
        Sync.Subtitle = "先选好要不要顺手更新 Steam 中文名，再开始";
        Sync.Tip = "从官网 All Trainers 名录读取全部游戏名写进本地，搜索框的联想靠它；已经换到的中文名会原样保留。";

        // 只更新游戏名：不跑「游戏数据」那一条线，整段收起；中文名单独占一条
        Sync.UseSplitLayout();

        Sync.NamesLabel = "更新游戏名";
        Sync.Names.Waiting("等待开始", "点右下角「开始更新」开始读取官网名录");

        Sync.Chinese.Opacity = 1;
        Sync.Chinese.Waiting("等待开始", "勾选下面的选项才会去 Steam 查中文名");

        Sync.SteamOptionVisible = true;
        Sync.SteamOptionEnabled = true;
        Sync.SteamChecked = false;

        Sync.StartVisible = true;
        Sync.StartEnabled = true;
        Sync.StartText = "开始更新";
        Sync.CancelText = "取消";
        Sync.CancelEnabled = true;

        Sync.IsOpen = true;
    }

    /// <summary>
    /// 用户点了框里的「开始更新」：准备框这边只是在等这个信号，
    /// 真正开工由 <see cref="QuickNames_Click"/> 里那句等待收口。
    /// </summary>
    private void OnSyncStartRequested()
    {
        _syncStart?.TrySetResult((true, Sync.SteamChecked));
    }

    /// <summary>把模态进度框立起来：进度条回到起点，文案按更新类型切。</summary>
    /// <param name="kind">更新全部 / 更新已有 / 只更新游戏名。</param>
    /// <param name="withSteam">只更新游戏名时，这一轮是否连带更新 Steam 中文名。</param>
    private void ShowSyncDialog(SyncKind kind, bool withSteam = true)
    {
        PrepareSyncDialogRunning(kind, withSteam);
        Sync.IsOpen = true;
    }

    /// <summary>进度框切到「正在跑」：文案、进度条、按钮全部归位。</summary>
    private void PrepareSyncDialogRunning(SyncKind kind, bool withSteam)
    {
        var namesOnly = kind == SyncKind.Names;
        Sync.SplitLayout = namesOnly;
        _nameStageCn = false;

        Sync.Title = kind switch
        {
            SyncKind.Names => "快速更新游戏名",
            SyncKind.Existing => "更新已有游戏数据",
            _ => "更新全部游戏数据"
        };

        Sync.Subtitle = kind switch
        {
            SyncKind.Names => withSteam
                ? "官网名录与 Steam 中文名同时更新，互不等待"
                : "只更新官网游戏名录，不查询 Steam 中文名",
            SyncKind.Existing => "本地详情与官网名录同时更新，互不等待",
            _ => "官网列表与游戏名同时更新，互不等待"
        };

        Sync.Tip = !namesOnly
            ? "游戏数据、游戏名录和中文名同时跑，快慢取决于你的网络，不要着急哦。中文名走 Steam 商店接口，为避免限流大约每秒查一款，慢是正常的。"
            : withSteam
                ? "从官网名录读取全部游戏名写进本地，接着逐个去 Steam 换中文名，大约每秒查一款，慢是正常的。"
                : "从官网 All Trainers 名录读取全部游戏名写进本地，搜索框的联想靠它。已经换到的中文名会原样保留。";

        // 「快速更新游戏名」是从准备态原地切过来的：勾选项、开始按钮都留在原位只变灰，
        // 弹框的长相和高度都不动，进度条直接在原处跑起来 —— 看起来就是同一个框在更新，
        // 而不是关掉一个又开一个。另外两个入口没有准备态，这两块一直收起。
        Sync.SteamOptionVisible = namesOnly;
        Sync.SteamOptionEnabled = false;
        Sync.StartVisible = namesOnly;
        Sync.StartEnabled = false;
        Sync.StartText = namesOnly ? "正在更新…" : "开始更新";
        Sync.CancelText = "取消更新";
        Sync.CancelEnabled = true;

        // 只更新游戏名时把「游戏数据」整段收起来，框里只留游戏名 / 中文名两条
        Sync.Data.Visible = !namesOnly;
        Sync.Chinese.Visible = namesOnly;
        Sync.NamesLabel = namesOnly ? "更新游戏名" : "更新游戏名与中文名";

        // 中文名那一条：勾了就跟着跑；没勾也把位置留着，写明「本次跳过」，免得框突然缩一半
        Sync.Chinese.Opacity = namesOnly && !withSteam ? 0.45 : 1;

        // ① 游戏数据
        Sync.Data.Rolling("正在连接官网…");

        // ② 游戏名
        Sync.Names.Rolling("正在读取官网游戏名录…");

        // ③ Steam 中文名（只更新游戏名时才有这一条）
        Sync.Chinese.Waiting(
            namesOnly && !withSteam ? "本次未勾选，跳过 Steam 中文名" : "等待游戏名更新完成");
    }

    private void CloseSyncDialog()
    {
        Sync.IsOpen = false;
        Sync.SplitLayout = false;
        _syncStart = null;

        // 这次跑完了，下一轮还要从准备态开始：勾选项、开始按钮、中文名那一条都复原
        Sync.SteamOptionEnabled = true;
        Sync.StartEnabled = true;
        Sync.StartText = "开始更新";
        Sync.Chinese.Opacity = 1;
    }

    /// <summary>渲染一条进度：游戏名、阶段说明、进度条和计数。</summary>
    private void OnSyncProgress(SyncProgress p)
    {
        if (!Sync.IsOpen) return;

        var lane = Sync.Data;

        if (p.Game.Length > 0)
            lane.State = p.Phase == SyncPhase.Syncing ? $"正在更新 {p.Game}" : p.Game;

        lane.Detail = p.Detail;

        if (p.Ratio >= 0)
        {
            lane.Indeterminate = false;
            lane.Value = Math.Clamp(p.Ratio, 0, 1);
            lane.Percent = $"{Math.Clamp(p.Ratio, 0, 1) * 100:0}%";
        }
        else
        {
            lane.Indeterminate = true;
            lane.Percent = "";
        }

        if (p.Total > 0)
        {
            var text = $"已处理 {p.Done} / {p.Total} · 更新 {p.Updated} · 跳过 {p.Skipped}";
            if (p.Failed > 0) text += $" · 失败 {p.Failed}";
            lane.Count = text;
        }
        else
        {
            lane.Count = "";
        }
    }

    /// <summary>取消：只打取消标记，抓取那头自己收尾，已经写进库的数据都保留。</summary>
    private void OnSyncCancelRequested()
    {
        // 准备框阶段还没开跑：这里只是「关掉这个框」，不动任何任务
        if (_syncStart is not null)
        {
            _syncStart.TrySetResult((false, false));
            return;
        }

        if (_syncCts is null) return;

        Sync.CancelEnabled = false;
        Sync.CancelText = "正在取消…";

        // 两条线共用这一颗取消标记，一按全部停；提示写在当前正在动的那一段上
        const string stopping = "正在停止，已经更新好的部分会保留";

        if (Sync.Data.Visible) Sync.Data.SetDetail(stopping);
        else if (Sync.Chinese.Visible) Sync.Chinese.SetDetail(stopping);
        else Sync.Names.SetDetail(stopping);

        try { _syncCts.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
    }
}
