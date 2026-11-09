using FlingTrainerX.Models;

namespace FlingTrainerX.Services;

/// <summary>
/// 本地游戏库的数据门面：内存里那份库、收藏夹、本地分页切片、本地搜索、封面缓存。
/// <para>
/// 这一层没有界面、没有控件、也没有「当前在第几页」这类展示状态 —— 它只回答
/// 「库里有几款 / 第 N 页是哪 15 条 / 这个名字本地有没有 / 收藏是加还是减」。
/// 分页与搜索的<b>状态机</b>在 <c>MainWindowLibraryVM</c>，写库读库都从这里走。
/// </para>
/// <para>
/// 首页只读本地 SQLite，所以这里的每个读取都是同步且快的；唯一的异步是封面下载。
/// </para>
/// </summary>
public sealed class LibraryCatalog
{
    /// <summary>启动阶段并行读出来的数据。</summary>
    /// <param name="Library">本地库。</param>
    /// <param name="Favorites">收藏夹（按收藏时间倒序）。</param>
    /// <param name="CachedCount">SQLite 缓存表里的条数。</param>
    public sealed record StartupSnapshot(TrainerLibrary Library, List<TrainerItem> Favorites, int CachedCount);

    /// <summary>当前内存里的库。换库（重读 / 同步完）时整个对象会被替换。</summary>
    public TrainerLibrary Library { get; private set; } = new() { Source = TrainerWeb.Site, Pages = 1 };

    /// <summary>收藏夹，按收藏时间倒序；详情页和列表共用同一批对象引用。</summary>
    public List<TrainerItem> Favorites { get; } = new();

    /// <summary>缓存库里有多少款（SQLite 为准；读不到库时退回内存条数）。</summary>
    public int CachedCount { get; private set; }

    /// <summary>
    /// 换代序号：清缓存 / 重读 / 手动同步时自增。
    /// 翻页和搜索的异步响应回来时对一下这个号，晚到的旧结果直接丢掉。
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>本地库按每页 <see cref="TrainerWeb.PageSize"/> 条算出来有多少页（至少 1 页）。</summary>
    public int PageCount => Math.Max(1, (int)Math.Ceiling(Library.Items.Count / (double)TrainerWeb.PageSize));

    /// <summary>
    /// 启动数据：读缓存库 + 还原收藏 + 预热首屏封面，全部在后台线程。
    /// 由 App 在创建窗口之前就开跑，和「解析 XAML / 建窗口」并行。
    /// </summary>
    public static Task<StartupSnapshot> LoadStartupAsync() => Task.Run(() =>
    {
        var library = TrainerDataService.LoadCachedLibrary()
                      ?? new TrainerLibrary { Source = TrainerWeb.Site, Pages = 1 };
        var favorites = TrainerDataService.LoadFavorites(library);
        var cached = AppDatabase.Instance.IsAvailable
            ? AppDatabase.Instance.CacheCount()
            : library.Items.Count;

        // 首屏那几张封面顺手解码好：不预热的话，绑定求值时会在 UI 线程上
        // 一张张读盘 + 解码，卡片冒出来的那一下明显顿一下
        NetImages.WarmLocal(library.Items.Take(TrainerWeb.PageSize).Select(i => i.CoverSource));

        return new StartupSnapshot(library, favorites, cached);
    });

    /// <summary>把启动数据装进内存（UI 线程调）。</summary>
    /// <param name="snapshot">后台读好的那份。</param>
    public void Apply(StartupSnapshot snapshot) => Apply(snapshot.Library, snapshot.Favorites, snapshot.CachedCount);

    /// <summary>
    /// 把一份读好的库 / 收藏装进来：同步完重读、清完缓存重读都走这里，状态处理只写一份。
    /// </summary>
    /// <param name="library">新库。</param>
    /// <param name="favorites">新的收藏列表。</param>
    /// <param name="cachedCount">缓存条数。</param>
    public void Apply(TrainerLibrary library, List<TrainerItem> favorites, int cachedCount)
    {
        Library = library;
        TrainerDataService.CurrentLibrary = library;
        CachedCount = cachedCount;

        // 库换了对象，收藏里的引用也要换成新实例，否则详情页拿到的还是旧快照
        Favorites.Clear();
        Favorites.AddRange(favorites);
    }

    /// <summary>
    /// 从本地 SQLite 重新读一遍（不联网）。清缓存之后、同步之后都用它。
    /// </summary>
    public void Reload()
    {
        var library = TrainerDataService.LoadCachedLibrary()
                      ?? new TrainerLibrary { Source = TrainerWeb.Site, Pages = 1 };
        var favorites = TrainerDataService.LoadFavorites(library);
        var cached = AppDatabase.Instance.IsAvailable ? AppDatabase.Instance.CacheCount() : library.Items.Count;

        Apply(library, favorites, cached);
    }

    /// <summary>让还在飞的翻页 / 搜索响应作废（重读或清缓存之前调）。</summary>
    public void Invalidate() => Generation++;

    /// <summary>本地库的第 <paramref name="page"/> 页（每页 <see cref="TrainerWeb.PageSize"/> 条）。</summary>
    /// <param name="page">页码，从 1 开始。</param>
    public List<TrainerItem> LocalPage(int page) =>
        Library.Items
            .Skip((page - 1) * TrainerWeb.PageSize)
            .Take(TrainerWeb.PageSize)
            .ToList();

    /// <summary>
    /// 把一页的新数据并进内存库：同 ID 的用新对象<b>替换</b>（把本地路径搬过去），新的追加。
    /// 换掉旧对象是为了让收藏夹 / 详情页拿到的永远是最新版本。
    /// </summary>
    /// <param name="items">新到达的一页。</param>
    public void Merge(IEnumerable<TrainerItem> items)
    {
        var index = new Dictionary<int, int>(Library.Items.Count);
        for (var i = 0; i < Library.Items.Count; i++) index[Library.Items[i].Id] = i;

        foreach (var item in items)
        {
            if (index.TryGetValue(item.Id, out var at))
            {
                TrainerDataService.MergeLocal(item, Library.Items[at]);
                Library.Items[at] = item;
            }
            else
            {
                index[item.Id] = Library.Items.Count;
                Library.Items.Add(item);
            }
        }
    }

    /// <summary>
    /// 收藏夹是启动时按 SQLite 快照还原的（那时列表还空着）。首屏到达后用实时数据替换同 ID 的
    /// 快照，封面和版本号才不会停在收藏当时的旧值。
    /// </summary>
    /// <returns>有引用被换掉时返回 true（收藏夹视图要重排）。</returns>
    public bool RelinkFavorites()
    {
        if (Favorites.Count == 0) return false;

        var live = Library.Items.ToDictionary(i => i.Id);
        var changed = false;

        for (var i = 0; i < Favorites.Count; i++)
        {
            if (!live.TryGetValue(Favorites[i].Id, out var item)) continue;
            if (ReferenceEquals(Favorites[i], item)) continue;

            Favorites[i] = item;
            changed = true;
        }

        // 早期收藏的快照里没有头图地址，按 ID 补一次，封面才不会停在旧图
        foreach (var fav in Favorites.Where(f => string.IsNullOrWhiteSpace(f.Cover)))
            _ = FillFavoriteCoverAsync(fav);

        return changed;
    }

    /// <summary>加 / 取消收藏：写 SQLite + 更新内存列表。</summary>
    /// <param name="item">那一款游戏。</param>
    /// <param name="favorite">true = 加入，false = 移除。</param>
    public void SetFavorite(TrainerItem item, bool favorite)
    {
        if (favorite)
        {
            if (Favorites.All(f => f.Id != item.Id)) Favorites.Insert(0, item);
        }
        else
        {
            Favorites.RemoveAll(f => f.Id == item.Id);
        }

        TrainerDataService.SetFavorite(item, favorite);
    }

    /// <summary>这一款是不是已经收藏了。</summary>
    /// <param name="item">那一款游戏。</param>
    public bool IsFavorite(TrainerItem item) => Favorites.Any(f => f.Id == item.Id);

    /// <summary>
    /// 本地库搜索：官网标题 / 中文名包含关键词的条目。中文关键词会连同翻好的英文名一起搜，
    /// 命中后换成内存里的实时对象，卡片上的版本号、本地封面才是最新的。
    /// </summary>
    /// <param name="keyword">用来搜的关键词（中文已经翻过的话就是英文名）。</param>
    /// <param name="typed">用户原始输入，用来再按中文搜一次。</param>
    public List<TrainerItem> SearchLocal(string keyword, string typed)
    {
        if (!AppDatabase.Instance.IsAvailable) return new List<TrainerItem>();

        var keys = new List<string>();
        foreach (var raw in new[] { keyword, typed })
        {
            if (raw.Length == 0) continue;

            keys.Add(raw);

            // 官网标题里的撇号是 ’，用户键盘敲出来的是 '：两种形态都搜一遍才不会漏掉整串命中
            if (raw.Contains('\'') || raw.Contains('’'))
            {
                keys.Add(raw.Replace('\'', '’'));
                keys.Add(raw.Replace('’', '\''));
            }
        }

        var found = new Dictionary<int, TrainerItem>();
        foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var item in AppDatabase.Instance.SearchCache(key, TrainerWeb.PageSize * 4))
                found[item.Id] = item;
        }

        var live = Library.Items.ToDictionary(i => i.Id);
        return found.Values
            .Select(i => live.TryGetValue(i.Id, out var real) ? real : i)
            .OrderBy(i => i.CleanTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 把封面图片下载到 f\&lt;id&gt;\cover.jpg 并回写缓存表。
    /// 只跑 3 个并发，避免和列表请求抢带宽；落盘后 <see cref="TrainerItem.CoverPath"/> 变了，
    /// 卡片会自动从网络地址切到本地文件。
    /// </summary>
    /// <param name="items">要缓存封面的条目。</param>
    public static async Task CacheCoversAsync(IEnumerable<TrainerItem> items)
    {
        var todo = items.Where(i => i.Id > 0 && !i.HasLocalCover).ToList();
        if (todo.Count == 0) return;

        using var gate = new SemaphoreSlim(3);

        var jobs = todo.Select(async item =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (AppDatabase.CacheMuted) return;   // 正在清缓存，别往 f 里写

                if (await TrainerFiles.CacheCoverAsync(item).ConfigureAwait(false) && !AppDatabase.CacheMuted)
                    AppDatabase.Instance.SaveCache(item);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(jobs).ConfigureAwait(false);
    }

    private static async Task FillFavoriteCoverAsync(TrainerItem item)
    {
        var cover = await TrainerWeb.FetchCoverAsync(item.Id);
        if (!string.IsNullOrWhiteSpace(cover)) item.Cover = cover;
    }
}
