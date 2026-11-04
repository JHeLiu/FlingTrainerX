using System.Diagnostics;
using System.IO;
using FlingTrainerX.Models;

namespace FlingTrainerX.Services;

/// <summary>同步模式。</summary>
public enum SyncKind
{
    /// <summary>分页遍历官网全部文章（首页卡片 + 封面）。</summary>
    All,

    /// <summary>逐款重抓本地已有游戏的详情页（下载表 + 介绍图）。</summary>
    Existing
}

/// <summary>同步走到哪一步，界面按它切换文案。</summary>
public enum SyncPhase
{
    /// <summary>正在向官网要第一页 / 读本地库。</summary>
    Preparing,

    /// <summary>正在逐条同步。</summary>
    Syncing,

    /// <summary>收尾（刷新首页数据）。</summary>
    Finishing
}

/// <summary>一次同步的进度快照，交给界面渲染。</summary>
public sealed class SyncProgress
{
    /// <summary>当前阶段。</summary>
    public SyncPhase Phase { get; init; } = SyncPhase.Syncing;

    /// <summary>正在同步哪一款，例如 "Assassin's Creed Mirage"。</summary>
    public string Game { get; init; } = "";

    /// <summary>补充说明，例如 "第 3 / 52 页"。</summary>
    public string Detail { get; init; } = "";

    /// <summary>已处理条数。</summary>
    public int Done { get; init; }

    /// <summary>预计总条数（官网总数或本地库条数）。</summary>
    public int Total { get; init; }

    /// <summary>真正写入的条数。</summary>
    public int Updated { get; init; }

    /// <summary>因为没改动而跳过的条数。</summary>
    public int Skipped { get; init; }

    /// <summary>失败条数。</summary>
    public int Failed { get; init; }

    /// <summary>0~1 的完成度；负数表示还算不出来，界面显示滚动条。</summary>
    public double Ratio { get; init; } = -1;
}

/// <summary>一次同步的最终结果。</summary>
public sealed class SyncResult
{
    /// <summary>是否被用户取消。</summary>
    public bool Canceled { get; set; }

    /// <summary>出错信息，正常为 null。</summary>
    public string? Error { get; set; }

    /// <summary>一共检查了多少款。</summary>
    public int Total { get; set; }

    /// <summary>有改动并落库的款数。</summary>
    public int Updated { get; set; }

    /// <summary>没有改动被跳过的款数。</summary>
    public int Skipped { get; set; }

    /// <summary>失败款数。</summary>
    public int Failed { get; set; }

    /// <summary>总耗时。</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>给状态栏用的一句话总结。</summary>
    public string Summary
    {
        get
        {
            if (Error is not null) return $"同步失败：{Error}";

            var head = Canceled ? "已取消同步" : "同步完成";
            var body = $"检查 {Total} 款 · 更新 {Updated} · 跳过 {Skipped}";
            if (Failed > 0) body += $" · 失败 {Failed}";

            return $"{head} · {body} · 用时 {Human(Elapsed)}";
        }
    }

    /// <summary>把时长写成 "2 分 13 秒" / "18 秒" 这种。</summary>
    private static string Human(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return $"{Math.Max(1, (int)span.TotalSeconds)} 秒";

        var minutes = (int)span.TotalMinutes;
        var seconds = span.Seconds;
        return seconds > 0 ? $"{minutes} 分 {seconds} 秒" : $"{minutes} 分";
    }
}

/// <summary>
/// 「数据更新」背后的实际抓取逻辑。首页只读本地 SQLite，
/// 官网数据全部靠这里主动同步：
/// <para><see cref="SyncAllAsync"/>：一页一页翻到最后一页，<c>modified</c> 没变的直接跳过。</para>
/// <para><see cref="SyncExistingAsync"/>：拿本地每一款的地址单独重抓详情页。</para>
/// 两个方法都刻意放慢节奏（一页处理完、封面全部落盘之后才请求下一页），
/// 一次请求本来就带回十几款的数据，抢带宽只会让两边都更慢。
/// </summary>
public static class TrainerSyncService
{
    /// <summary>两页之间歇多久（毫秒）。</summary>
    private const int PageGapMs = 700;

    /// <summary>「同步已有数据」两款之间歇多久（毫秒）。</summary>
    private const int GameGapMs = 350;

    /// <summary>一页里最多几张封面同时下载。</summary>
    private const int CoverConcurrency = 3;

    /// <summary>
    /// 同步全部数据：从第 1 页翻到最后一页，逐条比对官网的修改时间。
    /// 没改动的跳过（封面缺失的顺手补下载），有改动的合并本地路径后写回缓存表。
    /// </summary>
    /// <param name="progress">进度回调，可以传 null。</param>
    /// <param name="ct">取消标记。</param>
    public static async Task<SyncResult> SyncAllAsync(IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var result = new SyncResult();

        // 本地快照：既用来判断「有没有改动」，也用来把已下载的封面 / 程序路径搬过去
        var cached = new Dictionary<int, TrainerItem>();
        foreach (var item in AppDatabase.Instance.LoadCache()) cached[item.Id] = item;

        progress?.Report(new SyncProgress
        {
            Phase = SyncPhase.Preparing,
            Game = "正在连接官网…",
            Detail = "读取第 1 页"
        });

        try
        {
            var (firstPage, totalPages, totalCount) = await TrainerWeb.FetchPageAsync(1, ct).ConfigureAwait(false);
            if (firstPage.Count == 0) throw new InvalidOperationException("官网没有返回数据");

            result.Total = totalCount > 0 ? totalCount : totalPages * TrainerWeb.PageSize;

            var done = 0;
            for (var page = 1; page <= totalPages; page++)
            {
                ct.ThrowIfCancellationRequested();

                var items = page == 1
                    ? firstPage
                    : (await TrainerWeb.FetchPageAsync(page, ct).ConfigureAwait(false)).Items;

                var covers = new List<TrainerItem>();

                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();
                    done++;

                    cached.TryGetValue(item.Id, out var prev);

                    // modified 一样就当没更新。官网每次改文章都会刷新这个时间戳，
                    // 精确到秒，比「最后更新」那个只到日的字段靠谱。
                    var unchanged = prev is not null &&
                                    !string.IsNullOrEmpty(item.ModifiedIso) &&
                                    string.Equals(item.ModifiedIso, prev.ModifiedIso, StringComparison.Ordinal);

                    if (unchanged)
                    {
                        result.Skipped++;

                        // 数据没变但封面还没落盘的，顺手补一张。
                        // 必须先把下载表 / 本地路径搬过来：封面下载完会整条回写，
                        // 搬过来之前回写会把库里的下载表冲成空表。
                        if (!prev!.HasLocalCover && !string.IsNullOrWhiteSpace(item.Cover))
                        {
                            TrainerDataService.MergeLocal(item, prev);
                            covers.Add(item);
                        }

                        Report(progress, SyncPhase.Syncing, item, $"第 {page} / {totalPages} 页",
                            done, result, unchanged: true);
                        continue;
                    }

                    // 新对象里没有本地路径，从旧对象搬过来，否则已下好的封面和程序会「消失」
                    TrainerDataService.MergeLocal(item, prev);

                    AppDatabase.Instance.SaveCache(item);
                    cached[item.Id] = item;
                    result.Updated++;

                    if (!item.HasLocalCover && !string.IsNullOrWhiteSpace(item.Cover)) covers.Add(item);

                    Report(progress, SyncPhase.Syncing, item, $"第 {page} / {totalPages} 页",
                        done, result, unchanged: false);
                }

                // 这一页的封面全部下完再进下一页：一次请求已经带回十几款的数据，
                // 图没落盘就接着翻页，等于把带宽全花在争抢上
                if (covers.Count > 0)
                {
                    await CacheCoversAsync(covers, progress, page, totalPages, done, result, ct)
                        .ConfigureAwait(false);
                }

                await Task.Delay(PageGapMs, ct).ConfigureAwait(false);
            }

            progress?.Report(new SyncProgress
            {
                Phase = SyncPhase.Finishing,
                Game = "正在刷新首页…",
                Done = done,
                Total = result.Total,
                Updated = result.Updated,
                Skipped = result.Skipped,
                Failed = result.Failed,
                Ratio = 1
            });
        }
        catch (OperationCanceledException)
        {
            result.Canceled = true;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        result.Elapsed = watch.Elapsed;
        return result;
    }

    /// <summary>
    /// 同步已有数据：按本地库一款一款重抓详情页，只更新下载表和介绍图，
    /// 本地已下载的文件路径按 URL 对齐后保留。下载表没变化的算「跳过」。
    /// </summary>
    /// <param name="progress">进度回调，可以传 null。</param>
    /// <param name="ct">取消标记。</param>
    public static async Task<SyncResult> SyncExistingAsync(IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var result = new SyncResult();

        var items = AppDatabase.Instance.LoadCache();
        result.Total = items.Count;

        if (items.Count == 0)
        {
            result.Error = "本地还没有数据，先点一次「同步全部数据」";
            result.Elapsed = watch.Elapsed;
            return result;
        }

        progress?.Report(new SyncProgress
        {
            Phase = SyncPhase.Preparing,
            Game = "正在读取本地库…",
            Detail = $"共 {items.Count} 款",
            Total = items.Count
        });

        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var item = items[i];
                var before = Signature(item);

                try
                {
                    var (downloads, images) = await TrainerWeb.FetchDetailAsync(item.Url, ct).ConfigureAwait(false);

                    if (downloads.Count == 0 && images.Count == 0)
                    {
                        result.Failed++;
                    }
                    else
                    {
                        // 先按 URL 把本地路径搬进新抓到的下载表，再替换
                        TrainerDataService.MergeLocalDownloads(item, downloads);
                        if (downloads.Count > 0) item.Downloads = downloads;
                        if (images.Count > 0 && item.Images.Count == 0) item.Images = images;

                        if (!item.HasLocalCover && !string.IsNullOrWhiteSpace(item.Cover))
                            await TrainerFiles.CacheCoverAsync(item, ct).ConfigureAwait(false);

                        AppDatabase.Instance.SaveCache(item);

                        if (Signature(item) == before) result.Skipped++;
                        else result.Updated++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    result.Failed++;
                }

                progress?.Report(new SyncProgress
                {
                    Phase = SyncPhase.Syncing,
                    Game = item.DisplayTitle,
                    Detail = $"第 {i + 1} / {items.Count} 款",
                    Done = i + 1,
                    Total = items.Count,
                    Updated = result.Updated,
                    Skipped = result.Skipped,
                    Failed = result.Failed,
                    Ratio = (i + 1) / (double)items.Count
                });

                // 别催官网：一款一款来，太快容易被限流
                await Task.Delay(GameGapMs, ct).ConfigureAwait(false);
            }

            progress?.Report(new SyncProgress
            {
                Phase = SyncPhase.Finishing,
                Game = "正在刷新首页…",
                Done = items.Count,
                Total = items.Count,
                Updated = result.Updated,
                Skipped = result.Skipped,
                Failed = result.Failed,
                Ratio = 1
            });
        }
        catch (OperationCanceledException)
        {
            result.Canceled = true;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        result.Elapsed = watch.Elapsed;
        return result;
    }

    /// <summary>把一条进度发出去，统一在这里拼文案。</summary>
    private static void Report(
        IProgress<SyncProgress>? progress, SyncPhase phase, TrainerItem item, string detail,
        int done, SyncResult result, bool unchanged)
    {
        progress?.Report(new SyncProgress
        {
            Phase = phase,
            Game = item.DisplayTitle,
            Detail = unchanged ? $"{detail} · 已是最新，跳过" : $"{detail} · 正在更新",
            Done = done,
            Total = result.Total,
            Updated = result.Updated,
            Skipped = result.Skipped,
            Failed = result.Failed,
            Ratio = result.Total > 0 ? Math.Clamp(done / (double)result.Total, 0, 1) : -1
        });
    }

    /// <summary>一页的封面按 3 个并发下完；失败的留给下次同步。</summary>
    private static async Task CacheCoversAsync(
        List<TrainerItem> items, IProgress<SyncProgress>? progress, int page, int totalPages,
        int done, SyncResult result, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(CoverConcurrency);

        var jobs = items.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();

                if (await TrainerFiles.CacheCoverAsync(item, ct).ConfigureAwait(false))
                    AppDatabase.Instance.SaveCache(item);

                progress?.Report(new SyncProgress
                {
                    Phase = SyncPhase.Syncing,
                    Game = item.DisplayTitle,
                    Detail = $"第 {page} / {totalPages} 页 · 正在下载封面",
                    Done = done,
                    Total = result.Total,
                    Updated = result.Updated,
                    Skipped = result.Skipped,
                    Failed = result.Failed,
                    Ratio = result.Total > 0 ? Math.Clamp(done / (double)result.Total, 0, 1) : -1
                });
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(jobs).ConfigureAwait(false);
    }

    /// <summary>下载表 + 图片地址的指纹，用来判断这次抓完到底有没有变化。</summary>
    private static string Signature(TrainerItem item)
    {
        var downloads = string.Join("|", item.Downloads.Select(d => $"{d.Url}>{d.Date}>{d.Size}"));
        return $"{downloads}::{string.Join("|", item.Images)}";
    }
}
