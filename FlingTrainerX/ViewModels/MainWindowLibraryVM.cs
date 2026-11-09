using System.Collections.ObjectModel;
using System.Windows.Threading;
using FlingTrainerX.Controls;
using FlingTrainerX.Models;
using FlingTrainerX.Services;

namespace FlingTrainerX.ViewModels;

/// <summary>首页当前显示哪一块。</summary>
public enum ViewMode { Library, Favorites, Queue, Settings }

/// <summary>游戏库的两个页签。</summary>
public enum LibraryTab { Recent, Popular }

/// <summary>
/// 一份分页状态：当前页、总页数、总条数、加载 / 失败、每页缓存。
/// 游戏库和搜索结果各一份，退出搜索后游戏库的页码进度不会被打乱。
/// </summary>
public sealed class PageCursor
{
    /// <summary>当前页（从 1 开始）。</summary>
    public int Page { get; set; } = 1;

    /// <summary>一共几页（至少 1）。</summary>
    public int TotalPages { get; set; } = 1;

    /// <summary>官网 X-WP-Total / 本地条数；0 = 还不知道。</summary>
    public int TotalCount { get; set; }

    /// <summary>正在向官网要这一页。</summary>
    public bool Loading { get; set; }

    /// <summary>这一页要失败了（右下角显示「重试」）。</summary>
    public bool Failed { get; set; }

    /// <summary>上次想跳去但没加载成功的页码（重试的目标）。</summary>
    public int PendingPage { get; set; }

    /// <summary>已经拿到手的页：页码 → 这一页的条目。</summary>
    public Dictionary<int, List<TrainerItem>> Cache { get; } = new();

    /// <summary>回到初始态（换搜索词 / 换库时调）。</summary>
    public void Reset()
    {
        Page = 1;
        TotalPages = 1;
        TotalCount = 0;
        Loading = false;
        Failed = false;
        PendingPage = 0;
        Cache.Clear();
    }
}

/// <summary>
/// 首页（游戏库 / 收藏夹 / 下载队列 / 设置）的 ViewModel，也是整个窗口唯一一处
/// 知道「现在显示哪一页、这一页有哪些卡片、搜索到哪一步」的地方。
/// <para>
/// 数据从 <see cref="LibraryCatalog"/> 来，翻页 / 搜索 / 页签 / 分页栏全部是这里的命令；
/// XAML 只写 {Binding Library.xxx} 和 Command，代码里不再出现 <c>Text = ...</c> 这种赋值。
/// 需要弹窗（确认框 / 详情）和滚动的部分由 <see cref="IAppDialogService"/> 代劳，
/// 状态栏交给 <see cref="IStatusSink"/>，VM 不引用任何控件。
/// </para>
/// </summary>
public sealed partial class MainWindowLibraryVM : ObservableObject
{
    private readonly LibraryCatalog _catalog;
    private readonly IAppDialogService _dialogs;
    private readonly IStatusSink _status;

    /// <summary>打字停下来 160ms 再查提示库，避免每敲一个字都打一次 SQL。</summary>
    private readonly DispatcherTimer _suggestTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };

    private int _searchSeq;          // 连续搜索的序号，晚到的旧响应直接丢
    private bool _applyingSuggest;   // 正在把提示回填进搜索框，别再触发一次提示查询
    private string _queueSubtitle = "暂无下载任务";

    public MainWindowLibraryVM(LibraryCatalog catalog, IAppDialogService dialogs, IStatusSink status)
    {
        _catalog = catalog;
        _dialogs = dialogs;
        _status = status;

        _suggestTimer.Tick += async (_, _) => await LoadSuggestionsAsync();
    }

    // ==================== 数据 ====================

    /// <summary>一行 5 张的卡片行（按当前页切好）。</summary>
    public ObservableCollection<CardRow> Rows { get; } = new();

    /// <summary>当前这一页的条目。</summary>
    public List<TrainerItem> Items { get; } = new();

    /// <summary>游戏库自己的分页进度。</summary>
    public PageCursor LibraryPager { get; } = new();

    /// <summary>搜索结果的分页进度。</summary>
    public PageCursor SearchPager { get; } = new();

    /// <summary>收藏夹里有多少款（侧栏角标用）。</summary>
    public int FavoriteCount => _catalog.Favorites.Count;

    /// <summary>「更新于 …」那句话。</summary>
    public string SyncTimeText => string.IsNullOrEmpty(_catalog.Library.FetchedAt)
        ? "等待更新"
        : $"更新于 {_catalog.Library.FetchedAt}";

    /// <summary>侧栏「收藏夹」后面的数量。</summary>
    public string FavoritesLabel => _catalog.Favorites.Count > 0
        ? $"收藏夹 · {_catalog.Favorites.Count}"
        : "收藏夹";

    /// <summary>侧栏文字变了（窗口把它转成根 VM 的属性）。</summary>
    public event EventHandler? StatsChanged;

    /// <summary>当前页的卡片换了（窗口要按新条数重算卡片尺寸）。</summary>
    public event EventHandler? ItemsChanged;

    /// <summary>需要把导航切到某一页（搜索命中时要回到游戏库）。</summary>
    public event EventHandler<ViewMode>? ModeChangeRequested;

    /// <summary>启动修改器成功后要把窗口收进托盘。</summary>
    public event EventHandler? LaunchCompleted;

    // ==================== 页面状态 ====================

    [ObservableProperty]
    private ViewMode _mode = ViewMode.Library;

    [ObservableProperty]
    private LibraryTab _tab = LibraryTab.Recent;

    /// <summary>搜索框里的字（双向绑定，输入即写回这里）。</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>当前生效的官网搜索词；空串表示没在搜索。</summary>
    [ObservableProperty]
    private string _keyword = "";

    /// <summary>正在向官网搜索（本地没有，第一页还没回来）。</summary>
    [ObservableProperty]
    private bool _searchInFlight;

    /// <summary>这次搜索结果是不是直接来自本地库（true 时副标题 / 状态栏标「本地」）。</summary>
    [ObservableProperty]
    private bool _searchFromLocal;

    [ObservableProperty]
    private string? _searchError;

    /// <summary>首屏正在读本地库（启动那一刻还没有数据）。</summary>
    [ObservableProperty]
    private bool _isFirstLoading = true;

    [ObservableProperty]
    private string? _firstError;

    /// <summary>0 = 英文，1 = 中文。</summary>
    [ObservableProperty]
    private int _titleLanguageIndex;

    /// <summary>提示列表开着没有（Popup 的 IsOpen 直接双向绑这个）。</summary>
    [ObservableProperty]
    private bool _suggestOpen;

    /// <summary>提示列表里高亮的那一条。</summary>
    [ObservableProperty]
    private int _suggestIndex = -1;

    /// <summary>本地游戏名提示（最多 8 条）。</summary>
    public ObservableCollection<GameNameRow> Suggestions { get; } = new();

    /// <summary>搜索框为空时显示占位提示。</summary>
    public bool SearchHintVisible => SearchText.Length == 0;

    /// <summary>是否处于「搜索结果」状态。</summary>
    public bool IsSearching => Keyword.Length > 0;

    /// <summary>当前生效的分页状态：搜索时用搜索那份，否则用游戏库那份。</summary>
    public PageCursor CurrentPager => IsSearching ? SearchPager : LibraryPager;

    // ---- 以下都是给 XAML 绑定的显示属性，由 ApplyDisplayState() 统一算 ----

    [ObservableProperty] private string _sectionTitle = "Steam 单机游戏库";
    [ObservableProperty] private string _sectionSubtitle = "";
    [ObservableProperty] private bool _libraryTabsVisible;
    [ObservableProperty] private bool _recentTabEnabled = true;
    [ObservableProperty] private bool _popularTabEnabled = true;
    [ObservableProperty] private bool _queuePanelVisible;
    [ObservableProperty] private bool _settingsPanelVisible;
    [ObservableProperty] private bool _cardListVisible = true;
    [ObservableProperty] private bool _emptyVisible;
    [ObservableProperty] private bool _isFavoritesView;

    [ObservableProperty] private string _emptyGlyph = "\uE895";
    [ObservableProperty] private string _emptyTitle = "";
    [ObservableProperty] private string _emptyDesc = "";
    [ObservableProperty] private double _emptyDescMaxWidth = 460;
    [ObservableProperty] private bool _emptySyncVisible;

    [ObservableProperty] private bool _pagerVisible;
    [ObservableProperty] private string _pagerTotal = "";
    [ObservableProperty] private bool _pagerTotalVisible;
    [ObservableProperty] private string _pagerPage = "第 1 页";
    [ObservableProperty] private bool _pagerPrevEnabled;
    [ObservableProperty] private bool _pagerNextEnabled;
    [ObservableProperty] private bool _pagerLoadingVisible;
    [ObservableProperty] private bool _pagerRetryVisible;

    // ==================== 生命周期（由窗口调） ====================

    /// <summary>启动数据装好之后调一次：清掉「正在读取」、按本地库重排。</summary>
    public void OnLibraryLoaded()
    {
        IsFirstLoading = false;
        FirstError = null;

        LibraryPager.Reset();
        LibraryPager.TotalCount = _catalog.Library.Items.Count;
        LibraryPager.TotalPages = _catalog.PageCount;
        LibraryPager.Page = 1;
        if (_catalog.Library.Items.Count > 0) LibraryPager.Cache[1] = _catalog.LocalPage(1);

        Refresh();
    }

    /// <summary>读库失败（首次运行建库炸了）：空状态里写原因。</summary>
    /// <param name="message">异常信息。</param>
    public void OnLibraryLoadFailed(string message)
    {
        IsFirstLoading = false;
        FirstError = message;
        Refresh();
    }

    /// <summary>切到某一张页面（导航、搜索命中、空状态里的按钮都走它）。</summary>
    /// <param name="mode">目标页。</param>
    public void ShowMode(ViewMode mode)
    {
        if (Mode == mode) return;

        Mode = mode;
        Refresh();
        _dialogs.ScrollToTop();
    }

    // ==================== 整页重排 ====================

    /// <summary>
    /// 重建当前页：先取这一页的数据，再算标题 / 页签 / 空状态 / 分页栏。
    /// 换页、换视图、搜索状态变化之后都调它。
    /// </summary>
    public void Refresh()
    {
        if (Mode == ViewMode.Queue)
        {
            _queueSubtitle = DownloadManager.Instance.SummaryText;
        }
        else if (Mode != ViewMode.Settings)
        {
            // 列表按当前页取数（搜索与游戏库各自一份分页缓存）
            SetItems(SourceItems);
        }

        ApplyDisplayState();
        UpdateStats();
    }

    /// <summary>本地库换了（同步完 / 清完缓存）：重排分页并重建当前页。</summary>
    public void ReloadFromLocal()
    {
        _catalog.Invalidate();
        _catalog.Reload();

        LibraryPager.Reset();
        LibraryPager.TotalCount = _catalog.Library.Items.Count;
        LibraryPager.TotalPages = _catalog.PageCount;
        LibraryPager.Page = 1;
        if (_catalog.Library.Items.Count > 0) LibraryPager.Cache[1] = _catalog.LocalPage(1);

        IsFirstLoading = false;
        FirstError = null;

        Refresh();
    }

    /// <summary>
    /// 右上角那颗「刷新」：重读一遍本地库。
    /// <para>首页只读 SQLite，秒回，不碰网络；联网更新在「设置 → 数据更新」里。</para>
    /// </summary>
    [RelayCommand]
    private void RefreshLocal()
    {
        ReloadFromLocal();

        var count = _catalog.Library.Items.Count;
        _status.Report(count > 0
            ? $"已重新读取本地数据 · {count} 款"
            : "本地还没有数据，去「设置 → 数据更新」更新一次");
    }

    /// <summary>整体重建（换页 / 搜索 / 切换视图）。</summary>
    /// <param name="items">这一页的条目。</param>
    public void SetItems(IEnumerable<TrainerItem> items)
    {
        Items.Clear();
        Items.AddRange(items);
        ApplyCardTitles();

        Rows.Clear();
        foreach (var row in CardRow.Split(Items))
            Rows.Add(row);

        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>首页 / 翻页是否还有请求在跑（清缓存前要等它们收尾）。</summary>
    public bool IsBusyLoading => IsFirstLoading || LibraryPager.Loading || SearchPager.Loading;

    // ==================== 页签 ====================

    /// <summary>切「最近更新 / 热门游戏」。</summary>
    /// <param name="tab">目标页签。</param>
    [RelayCommand]
    private void SelectTab(LibraryTab tab)
    {
        Tab = tab;
        Refresh();
        _dialogs.ScrollToTop();
    }

    /// <summary>ComboBox 那种「传字符串」的入口（页签按钮用 Tag 传 Recent / Popular）。</summary>
    [RelayCommand]
    private void SelectTabByName(string? tab)
    {
        Tab = tab == "Popular" ? LibraryTab.Popular : LibraryTab.Recent;
        Refresh();
        _dialogs.ScrollToTop();
    }

    // ==================== 分页 ====================

    [RelayCommand]
    private Task PrevPage() => GoToPageAsync(CurrentPager.Page - 1);

    [RelayCommand]
    private Task NextPage() => GoToPageAsync(CurrentPager.Page + 1);

    /// <summary>失败重试：目标还是「上次想去的那一页」，页码停在旧页不影响当前显示。</summary>
    [RelayCommand]
    private Task RetryPage()
    {
        var pager = CurrentPager;
        var target = pager.PendingPage > 0 ? pager.PendingPage : pager.Page + 1;
        pager.Failed = false;
        return GoToPageAsync(target);
    }

    /// <summary>
    /// 跳到第 <paramref name="target"/> 页：已经缓存过的页直接切换，
    /// 没缓存的向官网请求（期间列表停留在当前页，分页栏显示加载状态）。
    /// </summary>
    /// <param name="target">目标页码。</param>
    public async Task GoToPageAsync(int target)
    {
        var pager = CurrentPager;
        target = Math.Clamp(target, 1, Math.Max(1, pager.TotalPages));
        if (pager.Loading) return;

        // 首页只读本地：游戏库的分页就是本地数组切片，不发请求、无需加载态
        if (!IsSearching)
        {
            pager.Cache[target] = _catalog.LocalPage(target);
            pager.PendingPage = 0;
            pager.Page = target;
            pager.Failed = false;
            ShowPageChanged();
            return;
        }

        if (pager.Cache.ContainsKey(target))
        {
            pager.PendingPage = 0;
            pager.Page = target;
            pager.Failed = false;
            ShowPageChanged();
            return;
        }

        pager.PendingPage = target;
        pager.Loading = true;
        pager.Failed = false;
        ApplyPager();
        _status.Report($"正在加载搜索结果第 {target} 页…");

        var searching = IsSearching;
        var keyword = Keyword;
        var searchSeq = _searchSeq;
        var librarySeq = _catalog.Generation;

        // 换了关键词 / 退出搜索 / 清了缓存 / 手动同步：这一页的响应作废
        bool Stale() => searching ? searchSeq != _searchSeq : librarySeq != _catalog.Generation;

        try
        {
            var page = await TrainerWeb.SearchAsync(keyword, target);
            if (Stale()) return;

            // 新页里既可能有见过的（把本地路径搬过去），也有全新的（并入内存库，收藏关联才找得到）
            _catalog.Merge(page.Items);
            if (_catalog.RelinkFavorites() && Mode == ViewMode.Favorites) Refresh();

            pager.Cache[target] = page.Items;
            pager.TotalPages = Math.Max(1, page.TotalPages);
            pager.TotalCount = page.TotalCount;
            pager.Page = target;
            pager.PendingPage = 0;

            // 新的一页同样落库，下次启动直接读缓存
            TrainerDataService.SaveCache(page.Items);
            _ = LibraryCatalog.CacheCoversAsync(page.Items);

            ShowPageChanged();
        }
        catch
        {
            if (Stale()) return;
            pager.Failed = true;
            _status.Report($"第 {target} 页加载失败，可点右下角的「重试」");
        }
        finally
        {
            pager.Loading = false;
            if (!Stale()) ApplyPager();
        }
    }

    private void ShowPageChanged()
    {
        Refresh();
        _dialogs.ScrollToTop();
    }

    // ==================== 搜索 ====================

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(SearchHintVisible));

        // 整段删空：退出搜索结果，回到原来的游戏库
        if (!_applyingSuggest && IsSearching && value.Trim().Length == 0) ExitSearch();

        // 从提示里选了一条、正在回填：别再弹一次提示
        if (_applyingSuggest) return;

        QueueSuggest();
    }

    partial void OnTitleLanguageIndexChanged(int value)
    {
        var chinese = value == 1;
        if (TrainerItem.UseChineseTitle == chinese) return;

        TrainerItem.UseChineseTitle = chinese;

        // 窗口还没加载完时不用重排，首次 Refresh 会带上
        if (IsFirstLoading) return;
        Refresh();
    }

    /// <summary>按回车搜索：<b>先查本地库</b>，命中直接显示、不联网；本地没有才向官网要第 1 页。</summary>
    public async Task SubmitSearchAsync()
    {
        var typed = SearchText.Trim();

        // 清空后回车 = 退出搜索，回到原来的游戏库
        if (typed.Length == 0)
        {
            ExitSearch();
            return;
        }

        // 官网只有英文内容，中文关键词先翻成英文名再搜（查询本身仍然走官网）
        var keyword = typed;
        var translatedFrom = "";
        if (IsChinese(typed) && AppDatabase.Instance.IsAvailable)
        {
            var english = AppDatabase.Instance.FindEnglishByChinese(typed);
            if (!string.IsNullOrWhiteSpace(english) &&
                !string.Equals(english.Trim(), typed, StringComparison.OrdinalIgnoreCase))
            {
                keyword = english.Trim();
                translatedFrom = typed;
            }
        }

        // ① 本地库先搜一遍：中文按中文名找，英文按官网标题找
        var local = _catalog.SearchLocal(keyword, typed);
        if (local.Count > 0)
        {
            ShowLocalSearchResult(keyword, local, translatedFrom);
            return;
        }

        // ② 本地没有才联网
        if (SearchInFlight) return;

        SwitchToLibrary();

        var seq = ++_searchSeq;

        Keyword = keyword;
        SearchFromLocal = false;
        SearchPager.Reset();
        SearchError = null;
        SearchInFlight = true;

        Refresh();
        _dialogs.ScrollToTop();
        _status.Report(translatedFrom.Length > 0
            ? $"本地没有，正在官网搜索“{translatedFrom}” → {keyword}…"
            : $"本地没有，正在官网搜索“{keyword}”…");

        try
        {
            var page = await TrainerWeb.SearchAsync(keyword, 1);

            // 期间又搜了别的关键词：这次的结果作废
            if (seq != _searchSeq) return;

            SearchPager.Cache[1] = page.Items;
            SearchPager.Page = 1;
            SearchPager.TotalPages = Math.Max(1, page.TotalPages);
            SearchPager.TotalCount = page.TotalCount;

            if (page.Items.Count > 0)
            {
                // 官网搜到的直接落进本地库：卡片缓存给下次搜索 / 首页用，
                // 游戏名库补一条，以后按这个名字就能在本地命中
                TrainerDataService.SaveCache(page.Items);
                AppDatabase.Instance.AddGameNamesIfMissing(page.Items);
                _ = LibraryCatalog.CacheCoversAsync(page.Items);
            }

            var label = translatedFrom.Length > 0 ? $"“{translatedFrom}” → {keyword}" : $"“{keyword}”";

            _status.Report(page.TotalCount > 0
                ? $"搜索{label} · 已收进本地库"
                : translatedFrom.Length > 0
                    ? $"搜索{label} · 没有结果"
                    : IsChinese(typed)
                        ? $"搜索{label} · 没有结果（库里的名字是英文，可以试试英文名）"
                        : $"搜索{label} · 没有结果");
        }
        catch (Exception ex)
        {
            if (seq != _searchSeq) return;
            SearchError = ex.Message;
            _status.Report($"搜索失败：{ex.Message}");
        }
        finally
        {
            if (seq == _searchSeq)
            {
                SearchInFlight = false;
                Refresh();
            }
        }
    }

    /// <summary>
    /// 显示本地搜索的结果：不再发请求，直接把命中的条目按每页 15 条铺进分页缓存，
    /// 分页栏照常能翻；状态栏和副标题都会标一句「本地」。
    /// </summary>
    private void ShowLocalSearchResult(string keyword, List<TrainerItem> items, string translatedFrom)
    {
        // 让还在飞的官网搜索响应作废
        _searchSeq++;

        SwitchToLibrary();

        Keyword = keyword;
        SearchInFlight = false;
        SearchFromLocal = true;
        SearchError = null;
        SearchPager.Reset();

        var pages = Math.Max(1, (int)Math.Ceiling(items.Count / (double)TrainerWeb.PageSize));
        for (var p = 1; p <= pages; p++)
        {
            SearchPager.Cache[p] = items
                .Skip((p - 1) * TrainerWeb.PageSize)
                .Take(TrainerWeb.PageSize)
                .ToList();
        }

        SearchPager.TotalPages = pages;
        SearchPager.TotalCount = items.Count;
        SearchPager.Page = 1;

        Refresh();
        _dialogs.ScrollToTop();

        var label = translatedFrom.Length > 0 ? $"“{translatedFrom}” → {keyword}" : $"“{keyword}”";
        _status.Report($"搜索{label} · 本地 {items.Count} 款");
    }

    /// <summary>退出搜索：清掉结果，列表回到游戏库（页码还停在原来那一页）。</summary>
    /// <param name="clearText">要不要顺手把搜索框清空。</param>
    public void ExitSearch(bool clearText = false)
    {
        var was = IsSearching;

        // 先把状态清干净，再去改输入框内容——否则 TextChanged 会再进来一次
        _searchSeq++;
        Keyword = "";
        SearchPager.Reset();
        SearchInFlight = false;
        SearchFromLocal = false;
        SearchError = null;

        if (clearText && SearchText.Length > 0)
        {
            _applyingSuggest = true;   // 借这个闸，别让清空动作又触发一次提示
            SearchText = "";
            _applyingSuggest = false;
        }

        Refresh();

        if (!was) return;

        _dialogs.ScrollToTop();
        UpdateStats();
    }

    // ==================== 搜索智能提示 ====================

    private void QueueSuggest()
    {
        _suggestTimer.Stop();

        if (SearchText.Trim().Length == 0)
        {
            CloseSuggest();
            return;
        }

        _suggestTimer.Start();
    }

    private async Task LoadSuggestionsAsync()
    {
        _suggestTimer.Stop();

        var keyword = SearchText.Trim();
        if (keyword.Length == 0)
        {
            CloseSuggest();
            return;
        }

        if (!AppDatabase.Instance.IsAvailable) return;

        // 查 SQLite 放到线程池，别卡输入
        var rows = await Task.Run(() => AppDatabase.Instance.SearchGameNames(keyword, 8));

        // 查询期间用户又改了字：这次结果作废
        if (SearchText.Trim() != keyword) return;

        if (rows.Count == 0)
        {
            CloseSuggest();
            return;
        }

        Suggestions.Clear();
        foreach (var row in rows) Suggestions.Add(row);

        SuggestIndex = -1;
        SuggestOpen = true;
    }

    /// <summary>收起提示列表。</summary>
    public void CloseSuggest()
    {
        if (!SuggestOpen) return;

        SuggestOpen = false;
        SuggestIndex = -1;
        Suggestions.Clear();
    }

    /// <summary>把提示项回填进搜索框，并直接搜一次。</summary>
    /// <param name="row">选中的那一条。</param>
    public void ApplySuggest(GameNameRow? row)
    {
        if (row is null) return;

        _applyingSuggest = true;
        SearchText = row.CleanEn;
        _applyingSuggest = false;

        CloseSuggest();
        _ = SubmitSearchAsync();
    }

    /// <summary>提示列表里上下挪一格（循环）。</summary>
    /// <param name="delta">+1 向下，-1 向上。</param>
    public void MoveSuggest(int delta)
    {
        if (Suggestions.Count == 0) return;

        var index = SuggestIndex + delta;
        if (index < 0) index = Suggestions.Count - 1;
        if (index >= Suggestions.Count) index = 0;

        SuggestIndex = index;
    }

    [RelayCommand]
    private void PickSuggestion(GameNameRow? row) => ApplySuggest(row);

    /// <summary>窗口退出搜索时调（Esc、清空按钮）。</summary>
    [RelayCommand]
    private void ClearSearch() => ExitSearch(clearText: true);

    [RelayCommand]
    private Task Submit() => SubmitSearchAsync();

    // ==================== 卡片与收藏 ====================

    /// <summary>点卡片：打开详情弹框。</summary>
    /// <param name="item">那一款游戏。</param>
    [RelayCommand]
    private void OpenDetail(TrainerItem? item)
    {
        if (item is null) return;

        _dialogs.ShowDetail(
            item,
            _catalog.IsFavorite(item),
            favorite => OnFavoriteChanged(item, favorite),
            () => LaunchCompleted?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>收藏夹卡片左上角的「取消收藏」。</summary>
    /// <param name="item">那一款游戏。</param>
    [RelayCommand]
    private void RemoveFavorite(TrainerItem? item)
    {
        if (item is null) return;
        OnFavoriteChanged(item, false);
    }

    /// <summary>加 / 取消收藏（详情弹框回调和自检都走这里）。</summary>
    /// <param name="item">那一款游戏。</param>
    /// <param name="favorite">true = 加入，false = 移除。</param>
    public void ApplyFavorite(TrainerItem item, bool favorite) => OnFavoriteChanged(item, favorite);

    private void OnFavoriteChanged(TrainerItem item, bool favorite)
    {
        _catalog.SetFavorite(item, favorite);

        if (Mode == ViewMode.Favorites) Refresh();
        UpdateStats();

        _status.Report(favorite
            ? $"已收藏「{item.DisplayTitle}」· 共 {_catalog.Favorites.Count} 款"
            : $"已取消收藏「{item.DisplayTitle}」· 共 {_catalog.Favorites.Count} 款");
    }

    // ==================== 内部 ====================

    /// <summary>列表接口没有浏览量字段，热门页用每款游戏的修改项目数作排序依据（当前页内排）。</summary>
    private IEnumerable<TrainerItem> SourceItems
    {
        get
        {
            if (Mode == ViewMode.Favorites) return _catalog.Favorites;

            var pager = CurrentPager;
            if (pager.Cache.TryGetValue(pager.Page, out var page))
            {
                return Tab == LibraryTab.Popular
                    ? page.OrderByDescending(i => i.RealOptionCount)
                        .ThenByDescending(i => DateOrder(i.UpdatedText))
                    : page;
            }

            // 第 1 页还没到手：搜索中 / 已经开始按页取数就先空着（显示加载状态），
            // 本地缓存里有旧卡片就先顶一屏，避免启动时白屏
            return IsSearching || LibraryPager.Cache.Count > 0 || _catalog.Library.Items.Count == 0
                ? Array.Empty<TrainerItem>()
                : _catalog.Library.Items.OrderByDescending(i => DateOrder(i.UpdatedText));
        }
    }

    private static DateTime DateOrder(string value) =>
        DateTime.TryParse(value.Replace('.', '-'), out var date) ? date : DateTime.MinValue;

    /// <summary>
    /// 卡片标题切到中文时，给当前页每张卡补上中文名：以游戏数据表为准，
    /// 按官网链接左连 game_names 取一次（一页十几条，一次 IN 查询）。
    /// 查不到的保持空串，<see cref="TrainerItem.DisplayTitle"/> 会退回官网英文名。
    /// </summary>
    private void ApplyCardTitles()
    {
        if (!TrainerItem.UseChineseTitle || Items.Count == 0) return;
        if (!AppDatabase.Instance.IsAvailable) return;

        var map = AppDatabase.Instance.LoadChineseNames(Items.Select(i => i.Url));
        foreach (var item in Items)
        {
            var key = GameNameRow.KeyOf(item.Url);
            item.NameCn = key.Length > 0 && map.TryGetValue(key, out var cn) ? cn : "";
        }
    }

    /// <summary>搜索命中时把导航切回游戏库。</summary>
    private void SwitchToLibrary()
    {
        if (Mode == ViewMode.Library) return;

        ModeChangeRequested?.Invoke(this, ViewMode.Library);
    }

    /// <summary>刷新左侧栏的同步时间与收藏数量，状态栏写当前条数。</summary>
    public void UpdateStats()
    {
        StatsChanged?.Invoke(this, EventArgs.Empty);

        if (Mode is ViewMode.Queue or ViewMode.Settings) return;

        if (IsSearching)
        {
            _status.Report(SearchInFlight
                ? $"本地没有，正在官网搜索“{Keyword}”…"
                : SearchPager.TotalCount > 0
                    ? SearchFromLocal
                        ? $"搜索“{Keyword}” · 本地 {SearchPager.TotalCount} 款"
                        : $"搜索“{Keyword}”"
                    : $"搜索“{Keyword}” · 没有结果");
            return;
        }

        // 页码和官网总数都在右下角分页栏里，这里只报「本页加载了多少」
        _status.Report($"已加载 {Items.Count} 款游戏");
    }

    private void ApplyDisplayState()
    {
        ApplyHeader();

        LibraryTabsVisible = Mode == ViewMode.Library && !IsSearching;
        RecentTabEnabled = Tab != LibraryTab.Recent;
        PopularTabEnabled = Tab != LibraryTab.Popular;
        QueuePanelVisible = Mode == ViewMode.Queue;
        SettingsPanelVisible = Mode == ViewMode.Settings;
        IsFavoritesView = Mode == ViewMode.Favorites;

        if (Mode is ViewMode.Queue or ViewMode.Settings)
        {
            CardListVisible = false;
            EmptyVisible = false;
            ApplyPager();
            return;
        }

        ApplyEmptyState();
        ApplyPager();
    }

    private void ApplyHeader()
    {
        switch (Mode)
        {
            case ViewMode.Library when IsSearching:
                SectionTitle = "搜索结果";
                SectionSubtitle = SearchPager.TotalCount > 0
                    ? SearchFromLocal
                        ? $"“{Keyword}” · 本地 {SearchPager.TotalCount} 款"
                        : $"“{Keyword}” · 共 {SearchPager.TotalCount} 款"
                    : $"“{Keyword}”";
                break;

            case ViewMode.Library:
                SectionTitle = "Steam 单机游戏库";
                SectionSubtitle = LibraryPager.TotalCount > 0
                    ? $"已经收录 {LibraryPager.TotalCount} 款游戏"
                    : $"已经收录 {CachedGameCount()} 款游戏";
                break;

            case ViewMode.Favorites:
                SectionTitle = "收藏夹";
                SectionSubtitle = "";
                break;

            case ViewMode.Queue:
                SectionTitle = "下载队列";
                SectionSubtitle = _queueSubtitle;
                break;

            case ViewMode.Settings:
                SectionTitle = "设置";
                SectionSubtitle = "";
                break;
        }
    }

    private int CachedGameCount() =>
        _catalog.CachedCount > 0 ? _catalog.CachedCount : _catalog.Library.Items.Count;

    private void ApplyEmptyState()
    {
        // 「同步数据」按钮只在「本地一条都没有」时露出来；
        // 搜索没结果、收藏夹为空这些情况不该出现同步入口
        EmptySyncVisible = false;
        EmptyDescMaxWidth = 460;

        if (Items.Count > 0)
        {
            CardListVisible = true;
            EmptyVisible = false;
            return;
        }

        CardListVisible = false;
        EmptyVisible = true;

        // 首屏还没拿到数据：只有这一个状态需要特殊处理
        if (Mode == ViewMode.Library && !IsSearching && (IsFirstLoading || FirstError is not null))
        {
            EmptyDescMaxWidth = 520;

            if (IsFirstLoading)
            {
                EmptyGlyph = "\uE895";
                EmptyTitle = "正在读取本地数据";
                EmptyDesc = "首页的数据全部来自本地 SQLite。";
            }
            else
            {
                EmptyGlyph = "\uE783";
                EmptyTitle = "读取失败";
                EmptyDesc = $"读取本地数据库失败：{FirstError}\n\n" +
                            "可以到「设置 → 数据更新」里重新更新一次。";
            }

            return;
        }

        if (Mode == ViewMode.Favorites)
        {
            EmptyGlyph = "\uE734";
            EmptyTitle = "收藏夹还是空的";
            EmptyDesc = "打开任意游戏详情，点击「加入收藏夹」";
        }
        else if (IsSearching)
        {
            EmptyGlyph = "\uE721";
            EmptyTitle = SearchInFlight ? "正在搜索…" : "没有搜到";
            EmptyDesc = SearchInFlight
                ? $"正在搜索“{Keyword}”"
                : SearchError is not null
                    ? $"搜索失败：{SearchError}"
                    : $"没有找到与“{Keyword}”相关的修改器，试试英文游戏名或换个关键词。";
        }
        else
        {
            // 空库不再弹框问「要不要同步」，就在页面正中给一个按钮，点了直接抓官网
            EmptyGlyph = "\uE895";
            EmptyTitle = "本地还没有数据";
            EmptyDesc = "点击从官方获取最新游戏数据";
            EmptySyncVisible = true;
        }
    }

    private void ApplyPager()
    {
        var pager = CurrentPager;

        PagerVisible = Mode == ViewMode.Library && (pager.Cache.Count > 0 || pager.Loading || pager.Failed);
        if (!PagerVisible) return;

        PagerTotal = pager.TotalCount > 0 ? $"共 {pager.TotalCount} 款" : "";
        PagerTotalVisible = pager.TotalCount > 0;
        // 只显示当前页码：总数官网知道，但界面上不需要「第几 / 共几页」这种完整页码
        PagerPage = $"第 {pager.Page} 页";
        PagerPrevEnabled = !pager.Loading && pager.Page > 1;
        PagerNextEnabled = !pager.Loading && pager.Page < pager.TotalPages;
        PagerLoadingVisible = pager.Loading;
        PagerRetryVisible = pager.Failed && !pager.Loading;
    }

    private static bool IsChinese(string text) =>
        text.Any(c => (c >= 0x3400 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF));
}
