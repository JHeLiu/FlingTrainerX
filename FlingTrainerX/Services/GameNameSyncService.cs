using FlingTrainerX.Models;

namespace FlingTrainerX.Services;

/// <summary>游戏名那一段的产出，更新结束时拼到状态栏上。</summary>
/// <param name="Names">名录里一共多少款。</param>
/// <param name="Kept">重建名录时保留下来的中文名条数。</param>
/// <param name="Missing">需要去 Steam 查的有多少款（只更新名录时为 0）。</param>
/// <param name="Got">本轮真正换到的中文名条数。</param>
/// <param name="Blocked">Steam 一直连不上，提前收工。</param>
/// <param name="Canceled">被用户取消。</param>
/// <param name="Error">名录没拿到 / 写库失败时的原因。</param>
public sealed record NameLaneResult(
    int Names, int Kept, int Missing, int Got, bool Blocked, bool Canceled, string? Error);

/// <summary>
/// 游戏名那两条进度线的「播报口」：服务每走到一步就调一个回调，
/// 具体写成哪句话、哪条进度条滚到哪儿，由 <c>MainWindowSyncVM</c> 决定。
/// 全部可选 —— 自检或后台预热时可以一个都不传。
/// </summary>
public sealed class NameLanePresenter
{
    /// <summary>开始读官网名录（阶段说明, 明细）。</summary>
    public Action<string, string>? Fetching { get; init; }

    /// <summary>名录边读边报（已读条数, 当前这一款, 明细）。</summary>
    public Action<int, string, string>? FetchProgress { get; init; }

    /// <summary>名录读完了，正在整表写库（总数）。</summary>
    public Action<int>? Writing { get; init; }

    /// <summary>只更新名录时的收尾（总数, 保留的中文名数）。</summary>
    public Action<int, int>? ListDone { get; init; }

    /// <summary>开始去 Steam 换中文名（待查条数）。</summary>
    public Action<int>? Running { get; init; }

    /// <summary>换掉一条（已完成, 总数, 英文名, 中文名）。</summary>
    public Action<int, int, string, string>? Progress { get; init; }

    /// <summary>整段完成。</summary>
    public Action? Done { get; init; }

    /// <summary>被取消。</summary>
    public Action? Cancelled { get; init; }

    /// <summary>Steam 连不上，提前收工。</summary>
    public Action? Blocked { get; init; }

    /// <summary>名录没拿到 / 写库失败（原因）。</summary>
    public Action<string>? Broken { get; init; }
}

/// <summary>
/// 游戏名库同步：重抓官网 All Trainers 名录 → 整表重建 <c>game_names</c> →（可选）去 Steam 换中文名。
/// <para>
/// 和游戏数据那条线互不相干：各自请求、各自写表，可以同时跑。整段「安静退出」——
/// 取消、官网抽风、Steam 连不上都只体现在返回值里，不往外抛异常。
/// </para>
/// </summary>
public sealed class GameNameSyncService
{
    /// <summary>
    /// 跑一遍游戏名同步。
    /// </summary>
    /// <param name="withSteam">true = 名录换完继续补中文名；false = 只更新名录。</param>
    /// <param name="splitLayout">两条进度是否分开显示（只更新游戏名时为 true，决定名录那条何时打满）。</param>
    /// <param name="presenter">进度播报口，可为 null。</param>
    /// <param name="ct">取消标记。</param>
    public async Task<NameLaneResult> RunAsync(
        bool withSteam, bool splitLayout, NameLanePresenter? presenter, CancellationToken ct)
    {
        List<GameNameRow> rows;
        int kept;

        try
        {
            presenter?.Fetching?.Invoke("正在读取官网游戏名录…", "正在连接 flingtrainer.com");

            var fetchProgress = presenter?.FetchProgress is { } report
                ? new Progress<NameFetchProgress>(p => report(p.Count, p.Current, p.Detail))
                : null;

            // 一个请求拿全名录（官网页面是快照，内部会自动补上最近上架的游戏）
            rows = await TrainerWeb.FetchAllNamesAsync(ct, fetchProgress);

            if (rows.Count == 0)
            {
                presenter?.Broken?.Invoke("官网没有返回游戏名");
                return new NameLaneResult(0, 0, 0, 0, false, ct.IsCancellationRequested, "官网没有返回游戏名");
            }

            // 防线：官网改版 / 被拦时常常只解析出零头，这时候宁可不动，也不要把本地名录清空重写
            var existing = await Task.Run(() => AppDatabase.Instance.GameNameCount(), ct);
            if (existing > 200 && rows.Count < existing * 0.6)
            {
                var why = $"官网只返回了 {rows.Count} 款（本地已有 {existing} 款），先保留本地名录不动";
                presenter?.Broken?.Invoke(why);
                return new NameLaneResult(0, 0, 0, 0, false, false, why);
            }

            presenter?.Writing?.Invoke(rows.Count);

            // 整表重建（保留已换到的中文名）放后台：几千行写库不占 UI 线程
            kept = await Task.Run(() => AppDatabase.Instance.RebuildGameNames(rows), ct);
        }
        catch (OperationCanceledException)
        {
            presenter?.Cancelled?.Invoke();
            return new NameLaneResult(0, 0, 0, 0, false, true, null);
        }
        catch (Exception ex)
        {
            presenter?.Broken?.Invoke(ex.Message);
            return new NameLaneResult(0, 0, 0, 0, false, false, ex.Message);
        }

        // 两条分开显示时，游戏名这条到这儿就算完成，先打满；中文名那条接在后面单独走
        if (splitLayout) presenter?.ListDone?.Invoke(rows.Count, kept);

        // 只更新名录：到此为止，不碰 Steam
        if (!withSteam)
        {
            if (!splitLayout) presenter?.ListDone?.Invoke(rows.Count, kept);
            return new NameLaneResult(rows.Count, kept, 0, 0, false, false, null);
        }

        List<GameNameRow> missing;
        try
        {
            missing = await Task.Run(() => AppDatabase.Instance.LoadGameNamesMissingCn(), ct);
        }
        catch (Exception ex)
        {
            presenter?.Broken?.Invoke(ex.Message);
            return new NameLaneResult(rows.Count, kept, 0, 0, false, false, ex.Message);
        }

        if (missing.Count == 0)
        {
            presenter?.Done?.Invoke();
            return new NameLaneResult(rows.Count, kept, 0, 0, false, false, null);
        }

        presenter?.Running?.Invoke(missing.Count);

        var progress = presenter?.Progress is { } step
            ? new Progress<(int Done, int Total, string English, string Chinese)>(
                p => step(p.Done, p.Total, p.English, p.Chinese))
            : null;

        var (got, blocked) = await FillChineseNamesAsync(missing, ct, progress);

        var cancelled = ct.IsCancellationRequested;

        if (cancelled) presenter?.Cancelled?.Invoke();
        else if (blocked) presenter?.Blocked?.Invoke();
        else presenter?.Done?.Invoke();

        return new NameLaneResult(rows.Count, kept, missing.Count, got, blocked, cancelled, null);
    }

    /// <summary>
    /// 启动时准备游戏名库：表里有货就跳过；空的才去抓一份（只抓英文名，很便宜）。
    /// 只做静默准备——启动不弹任何框；中文名在同步时顺带补齐。
    /// </summary>
    public static async Task SeedIfEmptyAsync()
    {
        // 查库和写库都挪到后台线程：这张表有几千行，整批写进去不能占着 UI 线程
        var empty = await Task.Run(() =>
            AppDatabase.Instance.IsAvailable && AppDatabase.Instance.GameNameCount() == 0);
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

    /// <summary>
    /// 去 Steam 换中文名：3 路并发，但发请求的节奏由 <see cref="SteamWeb"/> 里的全局闸统一控制
    /// （约 1 秒一条，撞 429 自动整体冷却、再放慢），并发只是让等待时间被别的请求利用起来。
    /// 拿到一个就写一个（取消也不丢）；连着失败太多次（基本等于 Steam 不可达）就提前收工。
    /// </summary>
    private static async Task<(int Got, bool Blocked)> FillChineseNamesAsync(
        List<GameNameRow> rows,
        CancellationToken ct,
        IProgress<(int Done, int Total, string English, string Chinese)>? progress)
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
                    progress?.Report((n, rows.Count, row.CleanEn, name ?? ""));
                }
            });

        return (got, blocked != 0);
    }

    /// <summary>拿去 Steam 搜的名字：去掉官网标题里的 " Trainer" / " Enhanced" 后缀。</summary>
    private static string SteamQueryName(string officialTitle) =>
        officialTitle.Replace(" Trainer", "").Replace(" Enhanced", "").Trim();
}
