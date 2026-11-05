using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace FlingTrainerX.Services;

/// <summary>
/// 拿官网的英文游戏名去 Steam 商店换简体中文名。
/// <para>
/// 用的是商店搜索接口（<c>api/storesearch</c>）：按关键词搜、指定 <c>l=schinese</c>，
/// 返回的第一个 app 的名字就是国区中文名。不需要 key，也不需要登录。
/// 只有一条规则：返回的名字里得真有汉字，否则说明这款游戏在 Steam 上没出中文，
/// 或者搜到的是别的英文结果——这两种都当「没换到」，留空。
/// </para>
/// <para>
/// <b>限速</b>：商店接口没有公开配额，但社区实测单 IP 大约 1 次/秒是安全线，
/// 短时间几百次会直接回 429（封十几秒到几分钟）。所以这里所有请求共用一个节流闸：
/// 不管界面上开几路并发，全局限速 <see cref="BaseIntervalMs"/> 一条；
/// 真撞上 429 就按 Retry-After / 指数退避整体冷却，并把节奏再放慢一档。
/// </para>
/// </summary>
public static class SteamWeb
{
    private const string StoreHost = "https://store.steampowered.com";
    private const string SearchApi = StoreHost + "/api/storesearch/";

    /// <summary>正常节奏：约 1.1 秒一条。767 款游戏摊下来十分钟出头，换来的是不被封。</summary>
    private const int BaseIntervalMs = 900;

    /// <summary>被限流后最慢的节奏，再慢就不如让用户明天再同步了。</summary>
    private const int MaxIntervalMs = 3000;

    private static HttpClient? _http;

    private static HttpClient Http => _http ??= CreateClient();

    // ==================== 全局限速闸 ====================

    /// <summary>同一时刻只放一条请求出去算「下一个可用时间」，其余并发在闸外排队。</summary>
    private static readonly SemaphoreSlim PaceGate = new(1, 1);

    /// <summary>下一条请求最早什么时候能发（Environment.TickCount64）。</summary>
    private static long _nextSlotAt;

    /// <summary>撞了 429 之后整体冷却到什么时候，期间谁都别发。</summary>
    private static long _cooldownUntil;

    /// <summary>当前每条间隔，被限流后会往上调。</summary>
    private static int _intervalMs = BaseIntervalMs;

    /// <summary>这一轮撞限流的次数，用来算退避和写日志。</summary>
    private static int _throttled;

    /// <summary>当前节奏，自检日志用。</summary>
    public static string PaceSummary =>
        $"{Volatile.Read(ref _intervalMs)}ms/条，撞限流 {Volatile.Read(ref _throttled)} 次";

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgentProvider.UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.6");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", StoreHost + "/");
        return client;
    }

    /// <summary>
    /// 排队等到自己的发车时间。拿到槽位就把下一条的时间往后推一个间隔，
    /// 不在锁里 sleep —— 等的时候别的请求还能继续排队算时间。
    /// <para>
    /// 返回 <c>false</c> 表示等的时候被取消了。<b>这里刻意不把取消标记交给 Task.Delay</b>：
    /// 那样取消会抛 TaskCanceledException，调试器和日志都会把它当成一次崩溃，
    /// 而「用户按了取消」本来就该是安静退出。
    /// </para>
    /// </summary>
    private static async Task<bool> PaceAsync(CancellationToken ct)
    {
        while (true)
        {
            if (ct.IsCancellationRequested) return false;

            long waitMs;

            try
            {
                await PaceGate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            try
            {
                var now = Environment.TickCount64;
                var target = Math.Max(Volatile.Read(ref _nextSlotAt), Volatile.Read(ref _cooldownUntil));

                if (target <= now)
                {
                    // 加一点随机抖动，别让请求像节拍器一样整整齐齐
                    var interval = Volatile.Read(ref _intervalMs) + Random.Shared.Next(0, 120);
                    Volatile.Write(ref _nextSlotAt, now + interval);
                    return true;
                }

                waitMs = target - now;
            }
            finally
            {
                PaceGate.Release();
            }

            // 一小段一小段地等（不带标记，最多 200ms 就能察觉取消），
            // 冷却时间被别的请求改长了也能跟上
            await Task.Delay((int)Math.Min(waitMs, 200)).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return false;
        }
    }

    /// <summary>
    /// 被限流：整体冷却一段时间（优先听 Retry-After），并把单条间隔上调一档。
    /// 冷却结束时所有排队的请求会一起被放出来，所以要顺带把槽位时间也推到冷却之后。
    /// </summary>
    private static void NoteThrottled(HttpResponseMessage? res)
    {
        var hits = Interlocked.Increment(ref _throttled);

        // 20s → 40s → 80s，最多两分钟
        var seconds = Math.Min(120, 20 * Math.Pow(2, Math.Min(3, hits - 1)));
        if (res?.Headers.RetryAfter?.Delta is { } delta) seconds = Math.Min(300, delta.TotalSeconds + 2);
        else if (res?.Headers.RetryAfter?.Date is { } date)
            seconds = Math.Min(300, Math.Max(seconds, (date - DateTimeOffset.UtcNow).TotalSeconds + 2));

        var until = Environment.TickCount64 + (long)(seconds * 1000);

        long current;
        do
        {
            current = Volatile.Read(ref _cooldownUntil);
        } while (current < until && Interlocked.CompareExchange(ref _cooldownUntil, until, current) != current);

        var slower = Math.Min(MaxIntervalMs, Volatile.Read(ref _intervalMs) + 600);
        Volatile.Write(ref _intervalMs, slower);
    }

    /// <summary>一条成功了：慢慢把间隔收回去，别一直按最慢的跑。</summary>
    private static void NoteSucceeded()
    {
        Interlocked.Exchange(ref _throttled, 0);

        var interval = Volatile.Read(ref _intervalMs);
        if (interval > BaseIntervalMs) Volatile.Write(ref _intervalMs, Math.Max(BaseIntervalMs, interval - 60));
    }

    /// <summary>
    /// 按英文名换中文名。返回值三种情况：
    /// 有中文名 → 中文名；接口通但没搜到中文 → 空串；
    /// 请求本身失败（网络不通 / Steam 被墙 / 超时 / 一直被限流）→ <c>null</c>，同步时据此判断「是不是该提前收工」。
    /// </summary>
    public static async Task<string?> FetchChineseNameAsync(string englishName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(englishName)) return "";

        var url = $"{SearchApi}?term={Uri.EscapeDataString(englishName.Trim())}&l=schinese&cc=CN";

        // 429 最多重试两次：第一次等冷却，第二次还不给就认了（当这一条失败，别把整轮拖死）
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // 排队时被取消：安静收工，不抛异常（上层看 ct 就知道是取消）
                if (!await PaceAsync(ct).ConfigureAwait(false)) return null;

                using var res = await Http.GetAsync(url, ct).ConfigureAwait(false);

                if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                {
                    NoteThrottled(res);
                    continue;
                }

                res.EnsureSuccessStatusCode();

                var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                NoteSucceeded();

                if (!doc.RootElement.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array ||
                    items.GetArrayLength() == 0)
                {
                    return "";
                }

                var name = items[0].TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                return HasCjk(name) ? name.Trim() : "";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 取消时在飞的请求会被掐断（TaskCanceledException 也是这一类）：
                // 用户主动取消不是失败，安静返回，由上层看 ct 判断
                return null;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // 取消时连接被掐断，抛出来的是 IOException 之类的，同样算「取消」，不算失败
                return null;
            }
            catch
            {
                // 网络层面失败（超时、连接被重置）也算一条失败，交给上层决定要不要提前收工
                return null;
            }
        }

        return null;
    }

    /// <summary>名字里有没有汉字——纯英文结果说明这款游戏没出中文。</summary>
    private static bool HasCjk(string text)
    {
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        }

        return false;
    }
}
