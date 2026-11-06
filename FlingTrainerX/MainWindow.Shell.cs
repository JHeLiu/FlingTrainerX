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
/// 主窗口的一部分：外链与窗口生命周期。
/// </summary>
public partial class MainWindow
{
    // ==================== 外链 ====================

    /// <summary>用系统默认浏览器打开链接；失败就把原因写进状态栏，不弹异常。</summary>
    private void OpenExternal(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打不开浏览器：{ex.Message}";
        }
    }

    // ==================== 窗口 ====================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 窗口是固定尺寸（不能拉大缩小），双击标题栏也不做最大化，只负责拖动
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* 忽略拖动异常 */ }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 收进托盘：不是「最小化」，是把窗口真正藏起来（SW_HIDE 那一档）——
    /// 任务栏按钮、Alt+Tab 条目、屏幕左下角那截小窗一起消失，只剩右下角的托盘图标。
    /// </summary>
    private void HideToTray()
    {
        // 记一下藏之前是普通还是最大化，回来时原样还原
        if (WindowState != WindowState.Minimized) _stateBeforeTray = WindowState;

        // 只调 Hide()：可见性交给系统，任务栏按钮会被真正移除。
        // 别再写 ShowInTaskbar=false —— 那个属性会重建任务栏按钮，反而在左下角留下残影
        Hide();
    }

    /// <summary>
    /// 「启动程序」成功之后把自己收进托盘：停 0.6 秒，一来让 Toast 露个脸，
    /// 二来等修改器的窗口先拿到前台，不跟它抢焦点。收进去就是静默的，不弹任何提示。
    /// </summary>
    private void HideForLaunchedApp()
    {
        if (_exitRequested) return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_exitRequested) return;

            // 直接藏，不弹气泡 / 系统通知：托盘图标本来就在，别再打扰一次
            HideToTray();
        };
        timer.Start();
    }

    /// <summary>
    /// 把主界面亮出来并置前。托盘菜单用它；「双击第二次图标」时也走这里
    /// （那时候窗口可能正藏在托盘里）。
    /// </summary>
    public void ShowMainWindow()
    {
        if (!IsVisible) Show();

        // 隐藏期间窗口状态可能被系统压成 Minimized，按藏之前的样子（普通 / 最大化）还原
        if (WindowState == WindowState.Minimized) WindowState = _stateBeforeTray;

        Activate();
        Topmost = true;      // 抢一次前台：Windows 不允许后台进程直接抢焦点，先置顶再放开
        Topmost = false;
        Focus();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        if (_trayIcon is not null) _trayIcon.Visible = false;

        // 后台还在同步的话，先把它掐掉再退，别留一堆 http 请求挂着
        try { _syncCts?.Cancel(); } catch { }

        System.Windows.Application.Current.Shutdown();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
        {
            if (_trayIcon is not null) _trayIcon.Visible = false;
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _trayMenu?.Close();
        _trayIconImage?.Dispose();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 同步框是模态的：Esc 不关它，要停只能点「取消同步」
        if (e.Key == Key.Escape && SyncDialogOpen)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && AboutLayer.Visibility == Visibility.Visible)
        {
            CloseAbout();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && UpdateLayer.Visibility == Visibility.Visible)
        {
            // 正在下载 / 安装时不许用 Esc 糊掉弹窗，安装流程一旦开始就不能半途而废
            if (!_updating) CloseUpdateDialog();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SupportLayer.Visibility == Visibility.Visible)
        {
            CloseSupport();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _confirm is not null)
        {
            CloseConfirm(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _detailOpen)
        {
            CloseDetail();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SuggestPopup.IsOpen)
        {
            // 提示列表开着时，Esc 只收提示，不动搜索框里的字（和搜索框自己的处理一致）
            CloseSuggest();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Searching)
        {
            // 搜索框有焦点时自己的 KeyDown 已经处理过，这里兜住「焦点不在搜索框」的情况
            ExitSearch(clearText: true);
            e.Handled = true;
        }
        else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // 弹窗开着时 Ctrl+K 不去抢焦点：用户多半是想在上面复制条款
            if (AboutLayer.Visibility == Visibility.Visible || _confirm is not null || _detailOpen) return;

            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }
}
