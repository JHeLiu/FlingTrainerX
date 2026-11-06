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
/// 主窗口的一部分：导航切换、下载队列、滚动辅助。
/// </summary>
public partial class MainWindow
{
    // ==================== 交互 ====================

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || NavList.SelectedIndex < 0) return;

        _mode = NavList.SelectedIndex switch
        {
            1 => ViewMode.Favorites,
            2 => ViewMode.Queue,
            3 => ViewMode.Settings,
            _ => ViewMode.Library
        };

        Render();
        ScrollToTop();

        // 每次进设置页都从第一组开始：菜单归零、只显示「数据更新」那一块
        if (_mode == ViewMode.Settings) ShowSettingsGroup(SettingsViewModel.Groups.Data);
    }

    private void LibraryTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tab }) return;

        _libraryTab = tab == "Popular" ? LibraryTab.Popular : LibraryTab.Recent;
        Render();
        ScrollToTop();
    }

    // ==================== 下载队列 ====================

    private void OnDownloadsChanged()
    {
        UpdateQueueBadge();
        if (_mode == ViewMode.Queue) RefreshQueue();
    }

    /// <summary>左侧导航角标显示正在下载的数量。</summary>
    private void UpdateQueueBadge()
    {
        var tasks = DownloadManager.Instance.Tasks;
        var active = tasks.Count(t => t.IsActive);

        NavQueueLabel.Text = active > 0 ? $"下载队列 · {active}" : "下载队列";

        var empty = tasks.Count == 0;
        QueueEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        QueueList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshQueue()
    {
        UpdateQueueBadge();
        _queueSubtitle = DownloadManager.Instance.SummaryText;
        ViewModel.Refresh(PageState());
    }

    private void QueueCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DownloadTask task }) DownloadManager.Instance.Cancel(task);
    }

    private void QueueRetry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DownloadTask task }) DownloadManager.Instance.Retry(task);
    }

    private void QueueOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DownloadTask task }) DownloadManager.Instance.OpenFolder(task);
    }

    private void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DownloadTask task }) DownloadManager.Instance.Remove(task);
    }

    private void OpenDownloadDir_Click(object sender, RoutedEventArgs e)
        => DownloadManager.Instance.OpenFolder(null);

    /// <summary>点左下角的 flingtrainer.com：用系统默认浏览器打开官网。</summary>
    private void DataSourceLink_Click(object sender, MouseButtonEventArgs e)
    {
        OpenExternal("https://flingtrainer.com/");
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        var count = DownloadManager.Instance.ClearFinished();
        StatusText.Text = count == 0 ? "没有可清理的任务" : $"已清理 {count} 个任务";
        RefreshQueue();
    }

    private void ScrollToTop()
    {
        _listScroll ??= FindScrollViewer(CardList);
        _listScroll?.ScrollToTop();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }

        return null;
    }
}
