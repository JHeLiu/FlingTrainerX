using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FlingTrainerX.Models;

namespace FlingTrainerX.ViewModels;

public enum ViewMode { Library, Favorites, Queue, Settings }

public enum LibraryTab { Recent, Popular }

/// <summary>
/// 首页（游戏库 / 收藏夹 / 下载队列 / 设置）当前这一帧的页面状态。
/// 由窗口在数据或导航变化时填一份，ViewModel 只负责把它翻译成界面要显示的样子，
/// 不回头去碰任何控件。
/// </summary>
public readonly record struct LibraryPageState(
    ViewMode Mode,
    LibraryTab Tab,
    string Keyword,
    bool Searching,
    bool SearchInFlight,
    bool SearchFromLocal,
    string? SearchError,
    bool FirstLoading,
    string? FirstError,
    int ItemCount,
    int TotalCount,
    int CachedCount,
    string QueueSubtitle,
    bool PagerAvailable,
    int Page,
    int TotalPages,
    bool PagerLoading,
    bool PagerFailed);

/// <summary>
/// 首页的 ViewModel：标题 / 页签 / 空状态 / 分页栏的文字与可见性全部由它算，
/// XAML 只写 {Binding}，代码里不再出现 SectionTitle.Text = ... 这种逐控件赋值。
///
/// 数据仍然由窗口（作为 Controller）去官网 / SQLite 取，取完把状态喂进 <see cref="Refresh"/>；
/// 卡片集合本身由窗口持有并传进来（虚拟滚动要按行切，属于视图排版逻辑）。
/// </summary>
public sealed class LibraryViewModel : ObservableObject
{
    private string _sectionTitle = "Steam 单机游戏库";
    private string _sectionSubtitle = "";
    private bool _libraryTabsVisible;
    private bool _recentTabEnabled = true;
    private bool _popularTabEnabled = true;
    private bool _queuePanelVisible;
    private bool _settingsPanelVisible;
    private bool _cardListVisible = true;
    private bool _emptyVisible;
    private bool _isFavoritesView;

    private string _emptyGlyph = "\uE895";
    private string _emptyTitle = "";
    private string _emptyDesc = "";
    private double _emptyDescMaxWidth = 460;
    private bool _emptySyncVisible;

    private bool _pagerVisible;
    private string _pagerTotal = "";
    private bool _pagerTotalVisible;
    private string _pagerPage = "第 1 页";
    private bool _pagerPrevEnabled;
    private bool _pagerNextEnabled;
    private bool _pagerLoadingVisible;
    private bool _pagerRetryVisible;

    public LibraryViewModel(ObservableCollection<CardRow> rows) => Rows = rows;

    /// <summary>一行 5 张的卡片行集合（窗口按当前页切好，VM 只负责暴露给列表绑定）。</summary>
    public ObservableCollection<CardRow> Rows { get; }

    public string SectionTitle
    {
        get => _sectionTitle;
        private set => SetProperty(ref _sectionTitle, value);
    }

    public string SectionSubtitle
    {
        get => _sectionSubtitle;
        private set => SetProperty(ref _sectionSubtitle, value);
    }

    /// <summary>副标题为空时 XAML 用 TextToVis 把这一行收掉。</summary>

    public bool LibraryTabsVisible
    {
        get => _libraryTabsVisible;
        private set => SetProperty(ref _libraryTabsVisible, value);
    }

    public bool RecentTabEnabled
    {
        get => _recentTabEnabled;
        private set => SetProperty(ref _recentTabEnabled, value);
    }

    public bool PopularTabEnabled
    {
        get => _popularTabEnabled;
        private set => SetProperty(ref _popularTabEnabled, value);
    }

    public bool QueuePanelVisible
    {
        get => _queuePanelVisible;
        private set => SetProperty(ref _queuePanelVisible, value);
    }

    public bool SettingsPanelVisible
    {
        get => _settingsPanelVisible;
        private set => SetProperty(ref _settingsPanelVisible, value);
    }

    public bool CardListVisible
    {
        get => _cardListVisible;
        private set => SetProperty(ref _cardListVisible, value);
    }

    public bool EmptyVisible
    {
        get => _emptyVisible;
        private set => SetProperty(ref _emptyVisible, value);
    }

    /// <summary>收藏夹视图：卡片左上角多一个「取消收藏」按钮，由列表的 Tag 传下去。</summary>
    public bool IsFavoritesView
    {
        get => _isFavoritesView;
        private set => SetProperty(ref _isFavoritesView, value);
    }

    public string EmptyGlyph
    {
        get => _emptyGlyph;
        private set => SetProperty(ref _emptyGlyph, value);
    }

    public string EmptyTitle
    {
        get => _emptyTitle;
        private set => SetProperty(ref _emptyTitle, value);
    }

    public string EmptyDesc
    {
        get => _emptyDesc;
        private set => SetProperty(ref _emptyDesc, value);
    }

    public double EmptyDescMaxWidth
    {
        get => _emptyDescMaxWidth;
        private set => SetProperty(ref _emptyDescMaxWidth, value);
    }

    public bool EmptySyncVisible
    {
        get => _emptySyncVisible;
        private set => SetProperty(ref _emptySyncVisible, value);
    }

    public bool PagerVisible
    {
        get => _pagerVisible;
        private set => SetProperty(ref _pagerVisible, value);
    }

    public string PagerTotal
    {
        get => _pagerTotal;
        private set => SetProperty(ref _pagerTotal, value);
    }

    public bool PagerTotalVisible
    {
        get => _pagerTotalVisible;
        private set => SetProperty(ref _pagerTotalVisible, value);
    }

    public string PagerPage
    {
        get => _pagerPage;
        private set => SetProperty(ref _pagerPage, value);
    }

    public bool PagerPrevEnabled
    {
        get => _pagerPrevEnabled;
        private set => SetProperty(ref _pagerPrevEnabled, value);
    }

    public bool PagerNextEnabled
    {
        get => _pagerNextEnabled;
        private set => SetProperty(ref _pagerNextEnabled, value);
    }

    public bool PagerLoadingVisible
    {
        get => _pagerLoadingVisible;
        private set => SetProperty(ref _pagerLoadingVisible, value);
    }

    public bool PagerRetryVisible
    {
        get => _pagerRetryVisible;
        private set => SetProperty(ref _pagerRetryVisible, value);
    }

    /// <summary>重算整页：标题、页签、面板切换、空状态、分页栏。</summary>
    public void Refresh(LibraryPageState state)
    {
        ApplyHeader(state);

        LibraryTabsVisible = state.Mode == ViewMode.Library && !state.Searching;
        RecentTabEnabled = state.Tab != LibraryTab.Recent;
        PopularTabEnabled = state.Tab != LibraryTab.Popular;
        QueuePanelVisible = state.Mode == ViewMode.Queue;
        SettingsPanelVisible = state.Mode == ViewMode.Settings;
        IsFavoritesView = state.Mode == ViewMode.Favorites;

        if (state.Mode is ViewMode.Queue or ViewMode.Settings)
        {
            CardListVisible = false;
            EmptyVisible = false;
            ApplyPager(state);
            return;
        }

        ApplyEmptyState(state);
        ApplyPager(state);
    }

    /// <summary>只刷分页栏（翻页 / 加载状态变化时调），不动标题和空状态。</summary>
    public void RefreshPager(LibraryPageState state) => ApplyPager(state);

    private void ApplyHeader(LibraryPageState state)
    {
        switch (state.Mode)
        {
            case ViewMode.Library when state.Searching:
                SectionTitle = "搜索结果";
                SectionSubtitle = state.TotalCount > 0
                    ? state.SearchFromLocal
                        ? $"“{state.Keyword}” · 本地 {state.TotalCount} 款"
                        : $"“{state.Keyword}” · 共 {state.TotalCount} 款"
                    : $"“{state.Keyword}”";
                break;

            case ViewMode.Library:
                SectionTitle = "Steam 单机游戏库";
                // 「已经收录」按官网总数（X-WP-Total）实时显示；当前页码在右下角分页栏和状态栏
                SectionSubtitle = state.TotalCount > 0
                    ? $"已经收录 {state.TotalCount} 款游戏"
                    : $"已经收录 {state.CachedCount} 款游戏";
                break;

            case ViewMode.Favorites:
                SectionTitle = "收藏夹";
                SectionSubtitle = "";
                break;

            case ViewMode.Queue:
                SectionTitle = "下载队列";
                SectionSubtitle = state.QueueSubtitle;
                break;

            case ViewMode.Settings:
                SectionTitle = "设置";
                SectionSubtitle = "";
                break;
        }
    }

    private void ApplyEmptyState(LibraryPageState state)
    {
        // 「同步数据」按钮只在「本地一条都没有」时露出来；
        // 搜索没结果、收藏夹为空这些情况不该出现同步入口
        EmptySyncVisible = false;
        EmptyDescMaxWidth = 460;

        if (state.ItemCount > 0)
        {
            CardListVisible = true;
            EmptyVisible = false;
            return;
        }

        CardListVisible = false;
        EmptyVisible = true;

        // 首屏还没拿到数据：只有这一个状态需要特殊处理
        if (state.Mode == ViewMode.Library && !state.Searching &&
            (state.FirstLoading || state.FirstError is not null))
        {
            EmptyDescMaxWidth = 520;

            if (state.FirstLoading)
            {
                EmptyGlyph = "\uE895";
                EmptyTitle = "正在读取本地数据";
                EmptyDesc = "首页的数据全部来自本地 SQLite。";
            }
            else
            {
                EmptyGlyph = "\uE783";
                EmptyTitle = "读取失败";
                EmptyDesc = $"读取本地数据库失败：{state.FirstError}\n\n" +
                            "可以到「设置 → 数据更新」里重新更新一次。";
            }

            return;
        }

        if (state.Mode == ViewMode.Favorites)
        {
            EmptyGlyph = "\uE734";
            EmptyTitle = "收藏夹还是空的";
            EmptyDesc = "打开任意游戏详情，点击「加入收藏夹」";
        }
        else if (state.Searching)
        {
            EmptyGlyph = "\uE721";
            EmptyTitle = state.SearchInFlight ? "正在搜索…" : "没有搜到";
            EmptyDesc = state.SearchInFlight
                ? $"正在搜索“{state.Keyword}”"
                : state.SearchError is not null
                    ? $"搜索失败：{state.SearchError}"
                    : $"没有找到与“{state.Keyword}”相关的修改器，试试英文游戏名或换个关键词。";
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

    private void ApplyPager(LibraryPageState state)
    {
        PagerVisible = state.Mode == ViewMode.Library && state.PagerAvailable;
        if (!PagerVisible) return;

        PagerTotal = state.TotalCount > 0 ? $"共 {state.TotalCount} 款" : "";
        PagerTotalVisible = state.TotalCount > 0;
        // 只显示当前页码：总数官网知道，但界面上不需要「第几 / 共几页」这种完整页码
        PagerPage = $"第 {state.Page} 页";
        PagerPrevEnabled = !state.PagerLoading && state.Page > 1;
        PagerNextEnabled = !state.PagerLoading && state.Page < state.TotalPages;
        PagerLoadingVisible = state.PagerLoading;
        PagerRetryVisible = state.PagerFailed && !state.PagerLoading;
    }
}
