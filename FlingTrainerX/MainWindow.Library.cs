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
/// 主窗口的一部分：首页卡片：尺寸计算、渲染、翻页。
/// </summary>
public partial class MainWindow
{
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateShellClip();
        UpdateCardMetrics();

        // 弹框尺寸是窗口的百分比，窗口一变就跟着重算
        if (_detailOpen && DetailHost.Content is TrainerDetailView view)
            view.FitTo(RootGrid.ActualWidth, RootGrid.ActualHeight);

        // 窗口尺寸变了，贴在按钮下面的弹出层也要重新摆一次
        TrackOpenPopups();
    }

    // ==================== 卡片尺寸 ====================

    /// <summary>
    /// 固定版心布局：内容区最宽 <see cref="MaxContentWidth"/>，多出来的宽度平均留在两侧。
    /// 一行恒定 5 张，卡片宽度随窗口宽度等比变化（1080 窗口 ≈ 156，1280 窗口 ≈ 193），
    /// 高度按封面 16:9 + 固定标题区跟随，永不变形。
    /// </summary>
    private void UpdateCardMetrics()
    {
        // 直接拿窗口宽度反推，不依赖子控件排版：Window.SizeChanged 触发时子控件拿到的
        // ActualWidth 还可能是上一帧的，按它算会差一档
        var slot = ActualWidth - SidebarWidth - ContentSideMargin * 2;
        if (slot < 200) slot = 1280 - SidebarWidth - ContentSideMargin * 2; // 还没真正显示时按默认窗口算

        var contentW = Math.Min(MaxContentWidth, slot);

        // 显式给宽度 + 居中，宽屏上才能把整块摆在中间
        // （Width 默认是 NaN，NaN 参与比较恒为 false，必须先判 NaN 再判差值）
        if (double.IsNaN(CardArea.Width) || Math.Abs(CardArea.Width - contentW) > 0.5)
            CardArea.Width = contentW;

        // 列表右侧给滚动条留了 6px（CardListBox 的 Padding），剩下的整条宽度由 5 张卡片 + 4 个间距精确分完，
        // 这样第一张贴左、最后一张贴右，和上方标题的左右边缘对得上
        var avail = Math.Max(0, contentW - CardListGutter);
        var w = CardRowPanel.CardWidthOf(avail, CardRow.Capacity, CardGap);
        w = Math.Clamp(w, MinCardWidth, MaxCardWidth);

        // 每页 15 条 = 3 行：卡片再宽也不能让 3 行撑出列表可视高度，
        // 否则默认一屏就会冒出滚动条。窗口很高时才允许跟着放大（上限仍是 MaxCardWidth）。
        // 留 4px 余量，避免取整后差一两像素刚好溢出。
        // 超过一页（收藏夹、以后要是有更大的页）就不压了：按宽度自然排，让列表自己滚动，
        // 否则条数越多卡片越小，越滚越难看清。
        var viewport = CardList.ActualHeight;
        if (viewport > 120 && _items.Count <= TrainerWeb.PageSize)
        {
            var maxRowH = (viewport - CardRowGap * 3 - 4) / 3;
            var maxCover = maxRowH - CardInfoHeight;
            if (maxCover > 0)
            {
                var hLimit = Math.Floor(maxCover * 16d / 9d);
                w = Math.Min(w, Math.Max(MinCardWidth, hLimit));
            }
        }

        // 封面按 16:9 撑满上半部（官网头图就是这个比例），下半部固定放文字
        var h = Math.Round(w * 9d / 16d) + CardInfoHeight;

        var oldRowHeight = _cardH + CardRowGap;
        if (Math.Abs(w - _cardW) < 0.5 && Math.Abs(h - _cardH) < 0.5) return;

        _cardW = w;
        _cardH = h;

        Resources["CardW"] = w;
        Resources["CardH"] = h;
        Resources["CardInfoH"] = new GridLength(CardInfoHeight);

        // 行高跟着变了：停在原来那一行，别让画面弹回顶部
        KeepRowAnchor(oldRowHeight);
    }

    /// <summary>
    /// 行高变了（窗口缩放）之后，按「原来停在第几行」把滚动位置换算过去，
    /// 避免改窗口大小时内容一下子跳走。行数不变，所以不用重新分行。
    /// </summary>
    private void KeepRowAnchor(double oldRowHeight)
    {
        if (_items.Count == 0 || oldRowHeight <= 0) return;

        var scroll = _listScroll ??= FindScrollViewer(CardList);
        if (scroll is null) return;

        var anchorRow = scroll.VerticalOffset / oldRowHeight;
        var target = Math.Max(0, anchorRow * (_cardH + CardRowGap));

        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            // 先让新行高真正排完，ScrollViewer 的 Extent 更新之后再滚
            CardList.UpdateLayout();
            scroll.ScrollToVerticalOffset(target);
        }));
    }

    // ==================== 数据渲染 ====================

    /// <summary>是否处于「官网搜索结果」状态。</summary>
    private bool Searching => _keyword.Length > 0;

    // 列表接口没有浏览量字段，热门页用每款游戏的修改项目数作排序依据（当前页内排）。
    private IEnumerable<TrainerItem> SourceItems
    {
        get
        {
            if (_mode == ViewMode.Favorites) return _favorites;

            var pager = CurrentPager;
            if (pager.Cache.TryGetValue(pager.Page, out var page))
            {
                return _libraryTab == LibraryTab.Popular
                    ? page.OrderByDescending(i => i.RealOptionCount)
                        .ThenByDescending(i => DateOrder(i.UpdatedText))
                    : page;
            }

            // 第 1 页还没到手：搜索中 / 已经开始按页取数就先空着（显示加载状态），
            // 本地缓存里有旧卡片就先顶一屏，避免启动时白屏
            return Searching || _libraryPager.Cache.Count > 0 || _library.Items.Count == 0
                ? Array.Empty<TrainerItem>()
                : _library.Items.OrderByDescending(i => DateOrder(i.UpdatedText));
        }
    }

    private static DateTime DateOrder(string value) =>
        DateTime.TryParse(value.Replace('.', '-'), out var date) ? date : DateTime.MinValue;

    /// <summary>
    /// 重建当前页：窗口先把数据准备好（队列统计 / 当前页卡片），再交给 ViewModel 算标题、
    /// 空状态与分页栏。界面的文字和可见性全部走绑定，这里不再逐个控件赋值。
    /// </summary>
    private void Render()
    {
        if (_mode == ViewMode.Queue)
        {
            UpdateQueueBadge();
            _queueSubtitle = DownloadManager.Instance.SummaryText;
        }
        else if (_mode != ViewMode.Settings)
        {
            // 列表按当前页取数（搜索与游戏库各自一份分页缓存）
            SetItems(SourceItems);
        }

        ViewModel.Refresh(PageState());
    }

    /// <summary>把窗口当前状态打包成一份只读快照交给 ViewModel（VM 不反向引用窗口）。</summary>
    private LibraryPageState PageState()
    {
        var pager = CurrentPager;
        return new LibraryPageState(
            _mode,
            _libraryTab,
            _keyword,
            Searching,
            _searching,
            _searchFromLocal,
            _searchError,
            _firstLoading,
            _firstError,
            _items.Count,
            pager.TotalCount,
            CachedGameCount(),
            _queueSubtitle,
            pager.Cache.Count > 0 || pager.Loading || pager.Failed,
            pager.Page,
            pager.TotalPages,
            pager.Loading,
            pager.Failed);
    }

    /// <summary>整体重建（换页 / 搜索 / 切换视图）。</summary>
    private void SetItems(IEnumerable<TrainerItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
        ApplyCardTitles();

        ViewModel.Rows.Clear();
        foreach (var row in CardRow.Split(_items))
            ViewModel.Rows.Add(row);

        // 条数跨过「一页装得下 / 装不下」这条线时，卡片尺寸规则要换一套（见 UpdateCardMetrics）
        var scrollable = _items.Count > TrainerWeb.PageSize;
        if (scrollable != _cardScrollable)
        {
            _cardScrollable = scrollable;
            UpdateCardMetrics();
        }
    }

    /// <summary>
    /// 卡片标题切到中文时，给当前页每张卡补上中文名：以游戏数据表为准，
    /// 按官网链接左连 game_names 取一次（一页十几条，一次 IN 查询）。
    /// 查不到的保持空串，<see cref="TrainerItem.DisplayTitle"/> 会退回官网英文名。
    /// </summary>
    private void ApplyCardTitles()
    {
        if (!TrainerItem.UseChineseTitle || _items.Count == 0) return;
        if (!AppDatabase.Instance.IsAvailable) return;

        var map = AppDatabase.Instance.LoadChineseNames(_items.Select(i => i.Url));
        foreach (var item in _items)
        {
            var key = GameNameRow.KeyOf(item.Url);
            item.NameCn = key.Length > 0 && map.TryGetValue(key, out var cn) ? cn : "";
        }
    }

    /// <summary>
    /// 右下角「当前游戏名」下拉：只显示一种语言，默认英文。
    /// 切完重建当前页（标题是计算属性，重建行最省事，也不联网）。
    /// </summary>
    private void TitleLangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TitleLangCombo.SelectedIndex < 0) return;

        var chinese = TitleLangCombo.SelectedIndex == 1;
        if (TrainerItem.UseChineseTitle == chinese) return;

        TrainerItem.UseChineseTitle = chinese;

        // 窗口还没加载完（XAML 里设默认值）时不用重排，首次 Render 会带上
        if (!IsLoaded) return;
        Render();
    }

    // ==================== 上下分页 ====================

    /// <summary>游戏库和搜索结果都走分页；收藏夹 / 队列 / 设置不分页。</summary>
    private bool CanPage => _mode == ViewMode.Library;

    /// <summary>刷新右下角分页栏：总数、当前页码、上一页 / 下一页可用态、加载与重试。</summary>
    /// <summary>分页栏的状态同样交给 ViewModel 算，这里只递一份快照。</summary>
    private void UpdatePager() => ViewModel.RefreshPager(PageState());

    private void PagerPrev_Click(object sender, RoutedEventArgs e) => _ = GoToPageAsync(CurrentPager.Page - 1);

    private void PagerNext_Click(object sender, RoutedEventArgs e) => _ = GoToPageAsync(CurrentPager.Page + 1);

    /// <summary>失败重试：目标还是「上次想去的那一页」，页码停在旧页不影响当前显示。</summary>
    private void PagerRetry_Click(object sender, RoutedEventArgs e)
    {
        var pager = CurrentPager;
        var target = pager.PendingPage > 0 ? pager.PendingPage : pager.Page + 1;
        pager.Failed = false;
        _ = GoToPageAsync(target);
    }

    /// <summary>页码变了：重排卡片、滚回顶部、刷新副标题 / 状态栏 / 分页栏。</summary>
    private void ShowPageChanged()
    {
        Render();
        ScrollToTop();
        UpdateStats();
    }

    /// <summary>
    /// 跳到第 <paramref name="target"/> 页：已经缓存过的页直接切换，
    /// 没缓存的向官网请求（期间列表停留在当前页，分页栏显示加载状态）。
    /// </summary>
    private async Task GoToPageAsync(int target)
    {
        var pager = CurrentPager;
        target = Math.Clamp(target, 1, Math.Max(1, pager.TotalPages));
        if (pager.Loading) return;

        // 首页只读本地：游戏库的分页就是本地数组切片，不发请求、无需加载态
        if (!Searching)
        {
            pager.Cache[target] = LocalPage(target);
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
        UpdatePager();
        StatusText.Text = Searching
            ? $"正在加载搜索结果第 {target} 页…"
            : $"正在加载第 {target} 页…";

        var searching = Searching;
        var keyword = _keyword;
        var searchSeq = _searchSeq;
        var librarySeq = _librarySeq;

        // 换了关键词 / 退出搜索 / 清了缓存 / 手动同步：这一页的响应作废
        bool Stale() => searching ? searchSeq != _searchSeq : librarySeq != _librarySeq;

        try
        {
            var (items, totalPages, totalCount) = searching
                ? await TrainerWeb.SearchAsync(keyword, target)
                : await TrainerWeb.FetchPageAsync(target);

            if (Stale()) return;

            // 新页里既可能有见过的（把本地路径搬过去），也有全新的（并入内存库，收藏关联才找得到）
            MergeIntoLibrary(items);
            RelinkFavorites();

            pager.Cache[target] = items;
            pager.TotalPages = Math.Max(1, totalPages);
            pager.TotalCount = totalCount;
            pager.Page = target;
            pager.PendingPage = 0;

            // 新的一页同样落库，下次启动直接读缓存
            TrainerDataService.SaveCache(items);
            _ = CacheCoversAsync(items);

            ShowPageChanged();
        }
        catch
        {
            if (Stale()) return;
            pager.Failed = true;
            StatusText.Text = $"第 {target} 页加载失败，可点右下角的「重试」";
        }
        finally
        {
            pager.Loading = false;
            if (!Stale()) UpdatePager();
        }
    }
}
