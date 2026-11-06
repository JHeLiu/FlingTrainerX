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
/// 主窗口的一部分：卡片交互与详情弹层。
/// </summary>
public partial class MainWindow
{
    private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TrainerItem item }) return;
        e.Handled = true;
        OnFavoriteChanged(item, false);
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TrainerItem item }) return;
        OpenDetail(item);
    }

    // ==================== 卡片悬停：封面轻微放大 ====================

    /// <summary>卡片封面那个 Border 在模板里的名字。</summary>
    private const string CoverArtName = "CoverArt";

    private void Card_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => ZoomCover(sender as DependencyObject, 1.08);

    private void Card_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => ZoomCover(sender as DependencyObject, 1.0);

    /// <summary>
    /// 悬停时把封面画刷放大一点点（1.08 倍），退出时回到原样。
    /// 放大的是画刷而不是布局：不重排、不动原图，圆角裁剪保证放大后也不会溢出卡片。
    /// </summary>
    private static void ZoomCover(DependencyObject? card, double target)
    {
        if (card is null) return;

        var cover = FindCoverArt(card);
        if (cover?.Background is not ImageBrush template) return;

        // 模板里的 Freezable 会被 WPF 冻结以便多实例共享，直接动画会抛异常，先克隆出可写副本
        var brush = template;
        if (brush.IsFrozen)
        {
            brush = (ImageBrush)template.CloneCurrentValue();
            cover.Background = brush;
        }

        if (brush.RelativeTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform(1, 1, 0.5, 0.5);
            brush.RelativeTransform = scale;
        }

        var duration = TimeSpan.FromMilliseconds(target > 1 ? 260 : 320);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease });
    }

    private static Border? FindCoverArt(DependencyObject root)
    {
        if (root is Border { Name: CoverArtName } self) return self;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindCoverArt(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }

        return null;
    }

    private void OpenDetail(TrainerItem item)
    {
        var isFavorite = _favorites.Any(f => f.Id == item.Id);
        var view = new TrainerDetailView(item, isFavorite);
        view.CloseRequested += (_, _) => CloseDetail();
        view.FavoriteChanged += (_, _) => OnFavoriteChanged(item, view.IsFavorite);

        // 启动修改器成功 → 本程序自动收进托盘（右下角图标，右键可「打开主界面 / 退出程序」）
        view.Launched += (_, _) => HideForLaunchedApp();
        view.FitTo(RootGrid.ActualWidth, RootGrid.ActualHeight);

        DetailHost.Content = view;
        DetailLayer.Visibility = Visibility.Visible;
        _detailOpen = true;

        // 封面跟着弹窗一起上屏，不再等动画播完：卡片刚显示过同一张图，
        // 位图基本还在 NetImages 的缓存里（本地文件解码也就十几毫秒），
        // 网络图本来就是后台下载、下完自动刷新，不会卡住开场动画。
        _ = view.LoadImagesAsync();

        // 先把布局算完，动画只做位图合成：期间不重排、不重绘文字，稳定 60 帧
        DetailBackdrop.BeginAnimation(OpacityProperty, null);
        DetailBackdrop.Opacity = 0;
        DetailHost.BeginAnimation(OpacityProperty, null);
        DetailHost.Opacity = 0;
        DetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        DetailScale.ScaleX = DetailScale.ScaleY = DetailScaleFrom;
        DetailSlide.BeginAnimation(TranslateTransform.YProperty, null);
        DetailSlide.Y = 10;
        DetailHost.UpdateLayout();

        DetailHost.CacheMode = new BitmapCache();

        var token = ++_detailAnimToken;

        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        // 和关于弹窗同一档回弹（0.5）：从 0.96 起跳，实测过冲约 0.5%、350ms 左右落定
        var spring = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };

        DetailBackdrop.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(DetailBackdropTime)) { EasingFunction = fade });

        DetailHost.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170))) { EasingFunction = fade });

        // X / Y 各用一条独立时间线：同一个 Timeline 复用给两个属性会各自生成时钟，Completed 会触发两次
        var zoomX = new DoubleAnimation(DetailScaleFrom, 1, new Duration(DetailOpenTime)) { EasingFunction = spring };
        var zoomY = new DoubleAnimation(DetailScaleFrom, 1, new Duration(DetailOpenTime)) { EasingFunction = spring };

        // 注意：Completed 必须在 BeginAnimation 之前挂，先启动再挂回调的话永远不会触发
        zoomY.Completed += (_, _) =>
        {
            // 开场还没播完就被关掉（token 变了）就别再去构建列表了，那个详情页已经不要了
            if (token != _detailAnimToken) return;

            // 动画结束后恢复实时渲染，滚动与文字才清晰；选项列表再依次上屏
            // （33~46 行的模板 + 文本排版约 80ms，放在开场动画之前会明显卡一下）
            DetailHost.CacheMode = null;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                view.LoadOptions();
            }));
        };

        DetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoomX);
        DetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoomY);
        DetailSlide.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, new Duration(DetailOpenTime)) { EasingFunction = fade });
    }

    /// <summary>收藏状态变化：写 SQLite + 更新内存列表 + 刷新收藏夹视图。</summary>
    private void OnFavoriteChanged(TrainerItem item, bool favorite)
    {
        if (favorite)
        {
            if (_favorites.All(f => f.Id != item.Id)) _favorites.Insert(0, item);
        }
        else
        {
            _favorites.RemoveAll(f => f.Id == item.Id);
        }

        TrainerDataService.SetFavorite(item, favorite);

        if (_mode == ViewMode.Favorites) Render();
        UpdateStats();

        StatusText.Text = favorite
            ? $"已收藏「{item.DisplayTitle}」· 共 {_favorites.Count} 款"
            : $"已取消收藏「{item.DisplayTitle}」· 共 {_favorites.Count} 款";
    }

    /// <summary>点遮罩（弹框外那片半透明区域）关闭弹框；点在弹框内部不触发。</summary>
    private void DetailLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_detailOpen) return;

        // 只有事件源就是那层遮罩矩形才算「点在外面」，点到弹框的任何内容都不关
        if (!ReferenceEquals(e.OriginalSource, DetailBackdrop)) return;

        e.Handled = true;
        CloseDetail();
    }

    private void CloseDetail()
    {
        if (!_detailOpen) return;
        _detailOpen = false;

        DetailHost.CacheMode = new BitmapCache();

        var token = ++_detailAnimToken;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(DetailCloseTime);

        var cardFade = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };

        // 注意：Completed 必须在 BeginAnimation 之前挂，先启动再挂回调的话永远不会触发
        cardFade.Completed += (_, _) =>
        {
            // 播到一半又被点开（token 变了）就别收层，否则会把刚打开的详情页一起关掉
            if (token != _detailAnimToken) return;

            DetailLayer.Visibility = Visibility.Collapsed;
            DetailHost.Content = null;
            DetailHost.CacheMode = null;

            // 清掉动画值，回到 XAML 里的初始态（0.96 / 透明），下次打开才能从头播
            DetailHost.BeginAnimation(OpacityProperty, null);
            DetailBackdrop.BeginAnimation(OpacityProperty, null);
            DetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            DetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            DetailSlide.BeginAnimation(TranslateTransform.YProperty, null);
            DetailHost.Opacity = 0;
            DetailBackdrop.Opacity = 0;
            DetailScale.ScaleX = DetailScale.ScaleY = DetailScaleFrom;
            DetailSlide.Y = 10;
        };

        // 面板比关于弹窗大得多，收场只缩到 0.97、位移 6，够柔和又不晃
        DetailHost.BeginAnimation(OpacityProperty, cardFade);
        DetailScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = ease });
        DetailScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = ease });
        DetailSlide.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, 6, duration) { EasingFunction = ease });
        DetailBackdrop.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, 0, duration) { EasingFunction = ease });
    }
}
