using FlingTrainerX.Services;

namespace FlingTrainerX.ViewModels;

/// <summary>
/// 更新进度弹框的视图模型：三条进度线（游戏数据 / 游戏名 / Steam 中文名）
/// 加上标题、说明、可选项和两颗按钮的状态。
/// <para>
/// 它同时负责「跑一次更新」：两条线（游戏数据 / 游戏名 + 中文名）同时开跑、
/// 进度写进对应的进度线、结束后收起弹框并让首页重读本地库。
/// 抓取本身在 <see cref="TrainerSyncService"/> / <see cref="GameNameSyncService"/> 里，
/// 这里只做编排与播报，不碰任何控件。
/// </para>
/// </summary>
public sealed partial class MainWindowSyncVM : ObservableObject
{
    private readonly IStatusSink _status;

    /// <summary>「快速更新游戏名」的准备阶段：等用户在里面勾好、点「开始更新」。</summary>
    private TaskCompletionSource<(bool Ok, bool WithSteam)>? _syncStart;

    /// <summary>名录已经写完了、正在换中文名：取消时才知道该把「已取消」写在哪一条上。</summary>
    private bool _nameStageCn;

    public MainWindowSyncVM(IStatusSink status) => _status = status;

    /// <summary>① 游戏数据：官网列表 / 本地详情页。</summary>
    public MainWindowSyncLaneVM Data { get; } = new();

    /// <summary>② 官网游戏名录（+ 不分开显示时的 Steam 中文名）。</summary>
    public MainWindowSyncLaneVM Names { get; } = new();

    /// <summary>③ Steam 中文名：只有「快速更新游戏名」这一路会单独拆出来。</summary>
    public MainWindowSyncLaneVM Chinese { get; } = new() { Visible = false };

    /// <summary>正在跑一次更新（含准备阶段）。</summary>
    public bool IsRunning { get; set; }

    /// <summary>取消源；窗口退出时顺手掐断。</summary>
    public CancellationTokenSource? Cts { get; set; }

    /// <summary>弹框开着没有（遮罩 + 卡片一起显隐）。</summary>
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _title = "更新全部游戏数据";

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private string _tip = "";

    /// <summary>第二条进度线的标签：「更新游戏名」还是「更新游戏名与中文名」。</summary>
    [ObservableProperty]
    private string _namesLabel = "更新游戏名与中文名";

    /// <summary>
    /// 两条进度分开显示（只更新游戏名时）：名录一条、Steam 中文名一条。
    /// false 时中文名和名录共用一条。
    /// </summary>
    [ObservableProperty]
    private bool _splitLayout;

    // ---- 「同时更新 Steam 中文名」那一块：只更新游戏名时才有 ----

    [ObservableProperty]
    private bool _steamOptionVisible;

    /// <summary>开跑之后还留在原位，只是锁住不让改（框不跳动）。</summary>
    [ObservableProperty]
    private bool _steamOptionEnabled = true;

    [ObservableProperty]
    private bool _steamChecked;

    // ---- 右下角两颗按钮 ----

    [ObservableProperty]
    private bool _startVisible;

    [ObservableProperty]
    private bool _startEnabled = true;

    [ObservableProperty]
    private string _startText = "开始更新";

    [ObservableProperty]
    private bool _cancelEnabled = true;

    [ObservableProperty]
    private string _cancelText = "取消更新";

    /// <summary>三颗入口按钮统一置忙 / 复原（设置页的三行文案）。</summary>
    public Action<SyncKind?>? BusyChanged { get; set; }

    /// <summary>更新完要让首页重读本地库。</summary>
    public Action? LibraryReloaded { get; set; }

    partial void OnStartEnabledChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

    partial void OnCancelEnabledChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();

    /// <summary>只更新游戏名：游戏数据那条不跑，第三条单独显示。</summary>
    public void UseSplitLayout()
    {
        SplitLayout = true;
        Data.Visible = false;
        Chinese.Visible = true;
    }

    /// <summary>三条线全部归零，回到「刚开框」的干净状态。</summary>
    public void ResetLanes()
    {
        Data.Reset();
        Names.Reset();
        Chinese.Reset();
    }

    // ==================== 入口 ====================

    /// <summary>
    /// 跑一次更新，三个入口共用：更新全部 / 更新已有 / 快速更新游戏名。
    /// </summary>
    /// <param name="kind">更新全部 / 更新已有 / 只更新游戏名。</param>
    /// <param name="withSteam">只更新游戏名时是否连带更新 Steam 中文名。</param>
    /// <param name="fromSetup">true = 准备框已经立起来，这里只把框切成「跑起来」的样子。</param>
    public async Task RunAsync(SyncKind kind, bool withSteam = true, bool fromSetup = false)
    {
        if (IsRunning || _syncStart is not null)
        {
            _status.Report("正在更新，等这一轮跑完再开始下一轮", StatusLevel.Attention);
            return;
        }

        // 本地一条都没有时，「更新已有游戏数据」无事可做，先说明白再返回
        if (kind == SyncKind.Existing && AppDatabase.Instance.CacheCount() == 0)
        {
            _status.Report("本地还没有数据，先点「更新全部游戏数据」", StatusLevel.Attention);
            return;
        }

        IsRunning = true;
        Cts?.Dispose();
        Cts = new CancellationTokenSource();
        var ct = Cts.Token;

        BusyChanged?.Invoke(kind);
        if (fromSetup) PrepareRunning(kind, withSteam);
        else ShowDialog(kind, withSteam);

        var result = new SyncResult();
        NameLaneResult? nameResult = null;
        Task<NameLaneResult>? nameTask = null;
        Task<SyncResult>? dataTask = null;

        try
        {
            var progress = new Progress<SyncProgress>(ApplyProgress);

            // 第一条线：游戏数据（官网列表 / 本地详情页）。只更新游戏名时不开这条线
            if (kind != SyncKind.Names)
            {
                dataTask = kind == SyncKind.All
                    ? TrainerSyncService.SyncAllAsync(progress, ct)
                    : TrainerSyncService.SyncExistingAsync(progress, ct);
            }

            // 第二条线：官网名录 + （可选）Steam 中文名，和第一条同时开跑
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
            Cts?.Cancel();
            if (nameTask is not null)
            {
                try { await nameTask; } catch { }
            }

            result = new SyncResult { Error = ex.Message };
        }
        finally
        {
            CloseDialog();
            BusyChanged?.Invoke(null);
            IsRunning = false;
            Cts?.Dispose();
            Cts = null;
        }

        // 更新写的是本地库，首页跟着重读一遍才看得到新数据；
        // 只更新游戏名时没碰卡片表，不用动首页（搜索提示每次都是现查库）
        if (kind != SyncKind.Names) LibraryReloaded?.Invoke();

        if (kind == SyncKind.Names)
        {
            _status.Report(
                nameResult switch
                {
                    null => "更新游戏名结束",
                    { Error: not null } => $"更新游戏名失败：{nameResult.Error}",
                    { Canceled: true } => "更新游戏名已取消",
                    _ => DescribeQuickNames(nameResult, withSteam)
                },
                nameResult is { Error: null, Canceled: false } ? StatusLevel.Success : StatusLevel.Attention);
            return;
        }

        _status.Report(
            result.Summary + DescribeNameLane(nameResult),
            result.Error is null && !result.Canceled ? StatusLevel.Success : StatusLevel.Attention);
    }

    /// <summary>
    /// 设置里的「快速搜索游戏名」：先把名录写进 game_names，
    /// 勾了可选项才接着去 Steam 换中文名（默认不勾）。
    /// <para>
    /// 点「开始更新」不立刻开跑，而是把进度框直接立起来：两条进度条、
    /// 「同时更新 Steam 中文名」勾选项和「开始更新」按钮都在同一个框里。
    /// </para>
    /// </summary>
    public async Task QuickNamesAsync()
    {
        if (IsRunning || _syncStart is not null)
        {
            _status.Report("正在更新，等这一轮跑完再开始下一轮", StatusLevel.Attention);
            return;
        }

        _syncStart = new TaskCompletionSource<(bool Ok, bool WithSteam)>();
        BusyChanged?.Invoke(SyncKind.Names);
        ShowQuickNamesSetup();

        var (ok, withSteam) = await _syncStart.Task;
        _syncStart = null;

        if (!ok)
        {
            // 只是关掉准备框，没开跑，按钮直接复原
            CloseDialog();
            BusyChanged?.Invoke(null);
            return;
        }

        await RunAsync(SyncKind.Names, withSteam, fromSetup: true);
    }

    /// <summary>点框里的「开始更新」：准备阶段等到这个信号才真正开工。</summary>
    [RelayCommand]
    private void Start() => _syncStart?.TrySetResult((true, SteamChecked));

    /// <summary>点框里的「取消」：准备阶段只是关框，跑起来之后才真取消。</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_syncStart is not null)
        {
            _syncStart.TrySetResult((false, false));
            return;
        }

        if (Cts is null) return;

        CancelEnabled = false;
        CancelText = "正在取消…";

        // 两条线共用这一颗取消标记，一按全部停；提示写在当前正在动的那一段上
        const string stopping = "正在停止，已经更新好的部分会保留";

        if (Data.Visible) Data.SetDetail(stopping);
        else if (Chinese.Visible) Chinese.SetDetail(stopping);
        else Names.SetDetail(stopping);

        try { Cts.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
    }

    // ==================== 弹框状态 ====================

    public void ShowDialog(SyncKind kind, bool withSteam = true)
    {
        PrepareRunning(kind, withSteam);
        IsOpen = true;
    }

    /// <summary>进度框切到「正在跑」：文案、进度条、按钮全部归位。</summary>
    private void PrepareRunning(SyncKind kind, bool withSteam)
    {
        var namesOnly = kind == SyncKind.Names;
        SplitLayout = namesOnly;
        _nameStageCn = false;

        Title = kind switch
        {
            SyncKind.Names => "快速更新游戏名",
            SyncKind.Existing => "更新已有游戏数据",
            _ => "更新全部游戏数据"
        };

        Subtitle = kind switch
        {
            SyncKind.Names => withSteam
                ? "官网名录与 Steam 中文名同时更新，互不等待"
                : "只更新官网游戏名录，不查询 Steam 中文名",
            SyncKind.Existing => "本地详情与官网名录同时更新，互不等待",
            _ => "官网列表与游戏名同时更新，互不等待"
        };

        Tip = !namesOnly
            ? "游戏数据、游戏名录和中文名同时跑，快慢取决于你的网络，不要着急哦。中文名走 Steam 商店接口，为避免限流大约每秒查一款，慢是正常的。"
            : withSteam
                ? "从官网名录读取全部游戏名写进本地，接着逐个去 Steam 换中文名，大约每秒查一款，慢是正常的。"
                : "从官网 All Trainers 名录读取全部游戏名写进本地，搜索框的联想靠它。已经换到的中文名会原样保留。";

        // 「快速更新游戏名」是从准备态原地切过来的：勾选项、开始按钮都留在原位只变灰，
        // 弹框的长相和高度都不动，进度条直接在原处跑起来 —— 看起来就是同一个框在更新。
        SteamOptionVisible = namesOnly;
        SteamOptionEnabled = false;
        StartVisible = namesOnly;
        StartEnabled = false;
        StartText = namesOnly ? "正在更新…" : "开始更新";
        CancelText = "取消更新";
        CancelEnabled = true;

        // 只更新游戏名时把「游戏数据」整段收起来，框里只留游戏名 / 中文名两条
        Data.Visible = !namesOnly;
        Chinese.Visible = namesOnly;
        NamesLabel = namesOnly ? "更新游戏名" : "更新游戏名与中文名";

        // 中文名那一条：勾了就跟着跑；没勾也把位置留着，写明「本次跳过」，免得框突然缩一半
        Chinese.Opacity = namesOnly && !withSteam ? 0.45 : 1;

        // ① 游戏数据
        Data.Rolling("正在连接官网…");

        // ② 游戏名
        Names.Rolling("正在读取官网游戏名录…");

        // ③ Steam 中文名（只更新游戏名时才有这一条）
        Chinese.Waiting(namesOnly && !withSteam ? "本次未勾选，跳过 Steam 中文名" : "等待游戏名更新完成");
    }

    /// <summary>
    /// 只更新游戏名时的「准备框」：把两条进度和「是否同步中文名」一次性摆出来，
    /// 用户勾好点「开始更新」才开工。
    /// </summary>
    private void ShowQuickNamesSetup()
    {
        SplitLayout = true;

        Title = "快速更新游戏名";
        Subtitle = "先选好要不要顺手更新 Steam 中文名，再开始";
        Tip = "从官网 All Trainers 名录读取全部游戏名写进本地，搜索框的联想靠它；已经换到的中文名会原样保留。";

        // 只更新游戏名：不跑「游戏数据」那一条线，整段收起；中文名单独占一条
        UseSplitLayout();

        NamesLabel = "更新游戏名";
        Names.Waiting("等待开始", "点右下角「开始更新」开始读取官网名录");

        Chinese.Opacity = 1;
        Chinese.Waiting("等待开始", "勾选下面的选项才会去 Steam 查中文名");

        SteamOptionVisible = true;
        SteamOptionEnabled = true;
        SteamChecked = false;

        StartVisible = true;
        StartEnabled = true;
        StartText = "开始更新";
        CancelText = "取消";
        CancelEnabled = true;

        IsOpen = true;
    }

    public void CloseDialog()
    {
        IsOpen = false;
        SplitLayout = false;
        _syncStart = null;

        // 这次跑完了，下一轮还要从准备态开始：勾选项、开始按钮、中文名那一条都复原
        SteamOptionEnabled = true;
        StartEnabled = true;
        StartText = "开始更新";
        Chinese.Opacity = 1;
    }

    // ==================== 进度播报 ====================

    /// <summary>渲染一条进度：游戏名、阶段说明、进度条和计数。</summary>
    public void ApplyProgress(SyncProgress p)
    {
        if (!IsOpen) return;

        var lane = Data;

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

    /// <summary>开始去 Steam 换中文名（自检直接调）。</summary>
    public void ShowNameStageRunning(int total)
    {
        _nameStageCn = true;
        CnLane.Start(total, "正在从 Steam 换取中文名");
    }

    /// <summary>换掉一条中文名（自检直接调）。</summary>
    public void ShowNameStageProgress(int done, int total, string english, string chinese)
    {
        var lane = CnLane;
        lane.Report(done, total, "正在从 Steam 换取中文名");
        lane.ShowNamePair(english, chinese);
    }

    /// <summary>中文名那一条进度线：两条分开显示时各走各的条，否则和名录共用一条。</summary>
    private MainWindowSyncLaneVM CnLane => SplitLayout ? Chinese : Names;

    /// <summary>
    /// 更新弹框的第二段：先把官网 All Trainers 名录重抓一遍写进本地库，再（可选）去 Steam 换中文名。
    /// 进度怎么写由这里的回调决定，抓取逻辑全在 <see cref="GameNameSyncService"/>。
    /// </summary>
    public Task<NameLaneResult> RunNameLaneAsync(bool withSteam, CancellationToken ct)
    {
        var presenter = new NameLanePresenter
        {
            // 开始读官网名录：总数还不知道，进度条先滚动
            Fetching = (state, detail) =>
            {
                _nameStageCn = false;
                Names.Rolling(state, detail);
            },

            // 名录边读边报：面板上滚动显示刚读到的游戏名，右边写已读到多少款
            FetchProgress = (count, current, detail) =>
            {
                var lane = Names;
                lane.Indeterminate = true;
                lane.Percent = "";
                lane.State = current.Length > 0 ? $"正在读取官网名录 · {current}" : "正在读取官网游戏名录…";
                lane.Detail = detail;
                lane.Count = count > 0 ? $"已读取 {count} 款" : "";
            },

            // 名录读完了，正在整表写进本地库
            Writing = total =>
            {
                _nameStageCn = false;
                Names.Rolling("正在写入本地数据库…", $"共 {total} 款，重建游戏名库（已换到的中文名会保留）");
            },

            // 只更新名录时的收尾
            ListDone = (names, kept) =>
            {
                _nameStageCn = false;
                Names.Finish("游戏名更新完成", kept > 0 ? $"共 {names} 款，保留 {kept} 个中文名" : $"共 {names} 款");
            },

            // 开始换名：有总数，进度条能算百分比
            Running = total =>
            {
                _nameStageCn = true;
                CnLane.Start(total, "正在从 Steam 换取中文名");
            },

            // 换一条刷一下
            Progress = (done, total, english, chinese) =>
            {
                var lane = CnLane;
                lane.Report(done, total, "正在从 Steam 换取中文名");
                lane.ShowNamePair(english, chinese);
            },

            Done = () => CnLane.Finish(
                SplitLayout ? "中文名更新完成" : "游戏名与中文名更新完成",
                $"共 {AppDatabase.Instance.GameNameCount()} 款，其中 {AppDatabase.Instance.GameNameCnCount()} 款有中文名"),

            Cancelled = () =>
            {
                // 停在哪一段就写在哪一段：名录还没读完不能把「已取消」写到中文名那条上
                var cn = SplitLayout && _nameStageCn;
                var lane = cn ? Chinese : Names;

                // 取消只换说明，进度条和百分比停在原地（已经跑到哪儿一目了然）
                lane.Stop("已取消，已经更新好的部分都保留", clearPercent: false);

                // 名录还没写完就停了：中文名那条压根没机会开始，别让它一直挂着「等待…」
                if (SplitLayout && !_nameStageCn) Chinese.Stop("游戏名没更新完，这一步没有开始");
            },

            Blocked = () => CnLane.Stop(
                "Steam 连不上，中文名先停在这儿", "网络恢复后再更新一次，已换到的会自动跳过"),

            Broken = message =>
            {
                Names.Stop("游戏名更新失败", message);
                if (SplitLayout) Chinese.Stop("游戏名没读到，这一步没有开始");
            }
        };

        return new GameNameSyncService().RunAsync(withSteam, SplitLayout, presenter, ct);
    }

    /// <summary>
    /// 「快速更新游戏名」的状态栏文案：名录条数 + 保留的中文名，勾了中文名再加本轮补到的条数。
    /// </summary>
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
}
