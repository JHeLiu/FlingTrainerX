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
/// 主窗口的一部分：搜索：本地优先 + 官网兜底，以及输入框联想。
/// </summary>
public partial class MainWindow
{
    // ==================== 搜索：本地优先，查不到再走官网 ====================

    /// <summary>关键词里有没有汉字：有汉字的话官网（英文站）搜不到，要先在本地翻成英文名。</summary>
    private static bool IsChinese(string text) =>
        text.Any(c => (c >= 0x3400 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF));

    /// <summary>
    /// 按回车才走这里。顺序固定：<b>先查本地库</b>（英文名 / 中文名都认），命中就直接显示本地结果、不联网；
    /// 本地查不到才向 flingtrainer.com 的搜索接口要第 1 页，抓到的结果顺手写回本地缓存和游戏名库，
    /// 下次同一个游戏就能在本地命中。输入过程中不会发请求，也不会动当前列表；后续页走右下角分页栏。
    /// </summary>
    private async Task SubmitSearchAsync()
    {
        var typed = SearchBox.Text.Trim();

        // 清空后回车 = 退出搜索，回到原来的游戏库
        if (typed.Length == 0)
        {
            ExitSearch();
            return;
        }

        // 官网接口按 URL 编码传关键词（见 TrainerWeb.SearchEndpointOf），
        // 但官网只有英文内容，中文关键词发过去一定是空结果 ——
        // 本地有中文名库，先把它翻成英文名，再拿英文名去官网搜（查询本身仍然走官网）。
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
        var local = SearchLocal(keyword, typed);
        if (local.Count > 0)
        {
            ShowLocalSearchResult(keyword, local, translatedFrom);
            return;
        }

        // ② 本地没有才联网
        if (_searching) return;

        // 搜索结果显示在「游戏库」这一页里，先把导航切过去
        if (_mode != ViewMode.Library)
        {
            NavList.SelectedIndex = 0;
            if (_mode != ViewMode.Library) _mode = ViewMode.Library;
        }

        var seq = ++_searchSeq;

        _keyword = keyword;
        _searchFromLocal = false;
        _searchPager.Reset();
        _searchError = null;
        _searching = true;

        Render();
        ScrollToTop();
        StatusText.Text = translatedFrom.Length > 0
            ? $"本地没有，正在官网搜索“{translatedFrom}” → {keyword}…"
            : $"本地没有，正在官网搜索“{keyword}”…";

        try
        {
            var (items, totalPages, totalCount) = await TrainerWeb.SearchAsync(keyword, 1);

            // 期间又搜了别的关键词：这次的结果作废
            if (seq != _searchSeq) return;

            _searchPager.Cache[1] = items;
            _searchPager.Page = 1;
            _searchPager.TotalPages = Math.Max(1, totalPages);
            _searchPager.TotalCount = totalCount;

            if (items.Count > 0)
            {
                // 官网搜到的直接落进本地库：卡片缓存给下次搜索 / 首页用，
                // 游戏名库补一条，以后按这个名字就能在本地命中
                TrainerDataService.SaveCache(items);
                AppDatabase.Instance.AddGameNamesIfMissing(items);
                _ = CacheCoversAsync(items);
            }

            var label = translatedFrom.Length > 0 ? $"“{translatedFrom}” → {keyword}" : $"“{keyword}”";

            StatusText.Text = totalCount > 0
                ? $"搜索{label} · 已收进本地库"
                : translatedFrom.Length > 0
                    ? $"搜索{label} · 没有结果"
                    : IsChinese(typed)
                        ? $"搜索{label} · 没有结果（库里的名字是英文，可以试试英文名）"
                        : $"搜索{label} · 没有结果";
        }
        catch (Exception ex)
        {
            if (seq != _searchSeq) return;
            _searchError = ex.Message;
            StatusText.Text = $"搜索失败：{ex.Message}";
        }
        finally
        {
            if (seq == _searchSeq)
            {
                _searching = false;
                Render();
            }
        }
    }

    /// <summary>
    /// 本地库搜索：官网标题 / 中文名包含关键词的条目。中文关键词会连同翻好的英文名一起搜，
    /// 命中后换成内存里的实时对象，卡片上的版本号、本地封面才是最新的。
    /// </summary>
    /// <param name="keyword">用来搜的关键词（中文已经翻过的话就是英文名）。</param>
    /// <param name="typed">用户原始输入，用来再按中文搜一次。</param>
    private List<TrainerItem> SearchLocal(string keyword, string typed)
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

        var live = _library.Items.ToDictionary(i => i.Id);
        return found.Values
            .Select(i => live.TryGetValue(i.Id, out var real) ? real : i)
            .OrderBy(i => i.CleanTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 显示本地搜索的结果：不再发请求，直接把命中的条目按每页 15 条铺进分页缓存，
    /// 分页栏照常能翻；状态栏和副标题都会标一句「本地」。
    /// </summary>
    /// <param name="keyword">生效的关键词（英文名）。</param>
    /// <param name="items">本地命中的条目。</param>
    /// <param name="translatedFrom">中文原文，没有翻译就是空串。</param>
    private void ShowLocalSearchResult(string keyword, List<TrainerItem> items, string translatedFrom)
    {
        // 让还在飞的官网搜索响应作废
        _searchSeq++;

        // 搜索结果统一显示在「游戏库」这一页
        if (_mode != ViewMode.Library)
        {
            NavList.SelectedIndex = 0;
            if (_mode != ViewMode.Library) _mode = ViewMode.Library;
        }

        _keyword = keyword;
        _searching = false;
        _searchFromLocal = true;
        _searchError = null;
        _searchPager.Reset();

        var pages = Math.Max(1, (int)Math.Ceiling(items.Count / (double)TrainerWeb.PageSize));
        for (var p = 1; p <= pages; p++)
        {
            _searchPager.Cache[p] = items
                .Skip((p - 1) * TrainerWeb.PageSize)
                .Take(TrainerWeb.PageSize)
                .ToList();
        }

        _searchPager.TotalPages = pages;
        _searchPager.TotalCount = items.Count;
        _searchPager.Page = 1;

        Render();
        ScrollToTop();

        var label = translatedFrom.Length > 0 ? $"“{translatedFrom}” → {keyword}" : $"“{keyword}”";
        StatusText.Text = $"搜索{label} · 本地 {items.Count} 款";
    }

    /// <summary>退出搜索：清掉结果，列表回到游戏库（页码还停在原来那一页）。</summary>
    private void ExitSearch(bool clearText = false)
    {
        var was = Searching;

        // 先把状态清干净，再去改输入框内容——否则 TextChanged 会再进来一次
        _searchSeq++;
        _keyword = "";
        _searchPager.Reset();
        _searching = false;
        _searchFromLocal = false;
        _searchError = null;

        if (clearText && SearchBox.Text.Length > 0) SearchBox.Text = "";

        Render();

        if (!was) return;

        ScrollToTop();
        UpdateStats();
    }

    /// <summary>
    /// 「已经收录」的数字以 SQLite 缓存表为准，不用内存里那份列表：
    /// 一键清除缓存之后库里已经是 0 条，内存里的卡片还没散，用内存数就会显示成几百款。
    /// 数据库不可用（建库失败）时退回内存数量，至少不会一直报 0。
    /// </summary>
    /// <summary>
    /// 缓存库里有多少款。以前每次 <see cref="PageState"/> 都去 SQLite 数一遍，
    /// 而 PageState 每渲染一次就要调一次——启动时这一下还会把建库拖到 UI 线程上。
    /// 现在读进内存，随本地库一起刷新。
    /// </summary>
    private int _cachedGameCount;

    private int CachedGameCount() => _cachedGameCount > 0 ? _cachedGameCount : _library.Items.Count;

    /// <summary>刷新左侧栏的同步时间与收藏数量，状态栏写当前页码与官网总数。</summary>
    private void UpdateStats()
    {
        SidebarSyncText.Text = string.IsNullOrEmpty(_library.FetchedAt)
            ? "等待更新"
            : $"更新于 {_library.FetchedAt}";

        NavFavLabel.Text = _favorites.Count > 0 ? $"收藏夹 · {_favorites.Count}" : "收藏夹";

        if (Searching)
        {
            StatusText.Text = _searching
                ? $"本地没有，正在官网搜索“{_keyword}”…"
                : _searchPager.TotalCount > 0
                    ? _searchFromLocal
                        ? $"搜索“{_keyword}” · 本地 {_searchPager.TotalCount} 款"
                        : $"搜索“{_keyword}”"
                    : $"搜索“{_keyword}” · 没有结果";
            return;
        }

        // 页码和官网总数都在右下角分页栏里，这里只报「本页加载了多少」
        StatusText.Text = $"已加载 {_items.Count} 款游戏";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        // 输入过程中不发请求、不动列表；但整段删空时直接退出搜索结果，回到原来的游戏库
        if (IsLoaded && Searching && SearchBox.Text.Trim().Length == 0)
            ExitSearch();

        // 从提示里选了一条、正在回填：别再弹一次提示
        if (_applyingSuggest) return;

        QueueSuggest();
    }

    // ==================== 搜索智能提示（走本地 SQLite） ====================
    //
    // 提示只做「帮你把名字打对」，真正的搜索还是发给官网：
    // 选中一条 → 把英文名回填搜索框 → 走原来的 SubmitSearchAsync。

    /// <summary>输入停下来才查；删空就立刻收起提示。</summary>
    private void QueueSuggest()
    {
        _suggestTimer.Stop();

        if (SearchBox.Text.Trim().Length == 0)
        {
            CloseSuggest();
            return;
        }

        _suggestTimer.Start();
    }

    private async void SuggestTimer_Tick(object? sender, EventArgs e)
    {
        _suggestTimer.Stop();

        var keyword = SearchBox.Text.Trim();
        if (keyword.Length == 0)
        {
            CloseSuggest();
            return;
        }

        if (!AppDatabase.Instance.IsAvailable) return;

        // 查 SQLite 放到线程池，别卡输入
        var rows = await Task.Run(() => AppDatabase.Instance.SearchGameNames(keyword, 8));

        // 查询期间用户又改了字：这次结果作废
        if (SearchBox.Text.Trim() != keyword) return;

        if (rows.Count == 0)
        {
            CloseSuggest();
            return;
        }

        _suggests = rows;
        SuggestList.ItemsSource = rows;
        SuggestList.SelectedIndex = -1;
        SuggestPopup.IsOpen = true;

        SearchBox.Focus();
    }

    private void CloseSuggest()
    {
        if (!SuggestPopup.IsOpen) return;

        CheckLog("CloseSuggest 执行");
        SuggestPopup.IsOpen = false;
        SuggestList.ItemsSource = null;
        _suggests = new List<GameNameRow>();
    }

    /// <summary>把提示项回填进搜索框，并直接按官网搜索走一次。</summary>
    private void ApplySuggest(GameNameRow row)
    {
        _applyingSuggest = true;
        SearchBox.Text = row.CleanEn;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        _applyingSuggest = false;

        SearchHint.Visibility = Visibility.Collapsed;
        CloseSuggest();

        _ = SubmitSearchAsync();
    }

    /// <summary>
    /// 鼠标点提示项：不依赖 ListBox 的选中，直接从命中的元素往上找到那一行的 DataContext。
    /// 按下就处理（Up 太晚，Popup 是独立窗口，鼠标一按就可能已经被判成「点了外面」）。
    /// </summary>
    private void SuggestList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = RowFromElement(e.OriginalSource);
        CheckLog($"SuggestList PreviewMouseDown：src={e.OriginalSource?.GetType().Name}，row={row?.CleanEn ?? "null"}");

        if (row is null) return;

        e.Handled = true;   // 别让 ListBox 再走自己的选中 / 焦点逻辑，否则会抢走搜索框焦点
        ApplySuggest(row);
    }

    private static GameNameRow? RowFromElement(object? source)
    {
        var dep = source as DependencyObject;
        while (dep is not null and not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);

        return (dep as ListBoxItem)?.DataContext as GameNameRow;
    }

    private void MoveSuggest(int delta)
    {
        if (_suggests.Count == 0) return;

        var index = SuggestList.SelectedIndex + delta;
        if (index < 0) index = _suggests.Count - 1;
        if (index >= _suggests.Count) index = 0;

        SuggestList.SelectedIndex = index;
        SuggestList.ScrollIntoView(SuggestList.SelectedItem);
    }

    /// <summary>
    /// 提示打开时：↑↓ 挪高亮、回车把那一条拿去搜官网、Esc 先收提示。
    /// 必须挂在 PreviewKeyDown 上——单行 TextBox 自己的类处理器会先把 ↑↓ 当成
    /// 「光标去行首 / 行尾」吃掉，写在 KeyDown 里永远收不到方向键。
    /// </summary>
    private void SearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (SuggestPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    e.Handled = true;
                    MoveSuggest(+1);
                    return;
                case Key.Up:
                    e.Handled = true;
                    MoveSuggest(-1);
                    return;
                case Key.Escape:
                    e.Handled = true;
                    CloseSuggest();
                    return;
                case Key.Enter:
                    e.Handled = true;
                    // 选中了就用选中的那一条，没选就直接搜输入框里的字
                    if (SuggestList.SelectedItem is GameNameRow row)
                        ApplySuggest(row);
                    else
                    {
                        CloseSuggest();
                        _ = SubmitSearchAsync();
                    }
                    return;
            }
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CloseSuggest();
            _ = SubmitSearchAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ExitSearch(clearText: true);
        }
    }
}
