using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FlingTrainerX.Models;
using FlingTrainerX.Services;
using FlingTrainerX.ViewModels;

namespace FlingTrainerX.Views;

/// <summary>
/// 官网详细页的弹框：介绍图 + 修改选项 / 下载文件 选项卡 + 内置下载进度。
/// <para>
/// 这里只剩「排练版」的活儿：按窗口比例算尺寸、横幅上圆下方裁剪、开场与关闭动画、
/// 转圈和提示的淡入淡出。页面上的文字、数量、显隐全部绑 <see cref="TrainerDetailVM"/>，
/// 按钮都是命令，代码里不再出现 <c>xxx.Text = ...</c>。
/// </para>
/// </summary>
public partial class TrainerDetailView : System.Windows.Controls.UserControl
{
    /// <summary>弹框的视图模型：页签、选项、下载表、截图、收藏与启动状态都在里面。</summary>
    public TrainerDetailVM VM { get; }

    private bool _hooked;

    public event EventHandler? CloseRequested;
    public event EventHandler? FavoriteChanged;

    /// <summary>修改器已经成功启动（本程序该让位了：收到后缩到右下角托盘）。</summary>
    public event EventHandler? Launched;

    public TrainerDetailView(TrainerItem item, bool isFavorite)
    {
        InitializeComponent();

        VM = new TrainerDetailVM(item, isFavorite);
        DataContext = VM;

        // VM 只说要干什么，真正动控件 / 动窗口的事在这里做
        VM.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        VM.FavoriteChanged += (_, _) => FavoriteChanged?.Invoke(this, EventArgs.Empty);
        VM.Launched += (_, _) => Launched?.Invoke(this, EventArgs.Empty);
        VM.ToastRequested += (_, message) => _ = ShowToast(message);
        VM.LoadingOverlayChanged += (_, show) =>
        {
            if (show) ShowLoadingOverlay();
            else HideLoadingOverlay();
        };

        Loaded += OnLoaded;
    }

    /// <summary>当前是不是已收藏（主窗口据此把收藏状态写回库里）。</summary>
    public bool IsFavorite => VM.IsFavorite;

    /// <summary>
    /// 弹框尺寸按窗口的百分比算（宽 84%、高 86%），窗口放大缩小它跟着变，
    /// 不再是固定 1000×664；同时保一个下限，窗口很小时也不至于挤成一团。
    /// </summary>
    /// <param name="hostWidth">宿主窗口的可用宽度。</param>
    /// <param name="hostHeight">宿主窗口的可用高度。</param>
    public void FitTo(double hostWidth, double hostHeight)
    {
        const double widthRatio = 0.84;
        const double heightRatio = 0.86;
        const double minW = 700;
        const double minH = 500;

        // 先留 24px 边距再保下限；窗口比下限还小时以窗口为准（宁可缩也不溢出屏幕）
        var maxW = Math.Max(320, hostWidth - 24);
        var maxH = Math.Max(280, hostHeight - 24);

        var w = Math.Clamp(hostWidth * widthRatio, Math.Min(minW, maxW), maxW);
        var h = Math.Clamp(hostHeight * heightRatio, Math.Min(minH, maxH), maxH);

        w = Math.Round(w);
        h = Math.Round(h);

        if (Math.Abs(Panel.Width - w) > 0.5) Panel.Width = w;
        if (Math.Abs(Panel.Height - h) > 0.5) Panel.Height = h;

        // 横幅高度跟着宽度走，但压矮一些，把竖向空间多留给下面的选项 / 下载表；
        // 同时不超过弹框高度的 34%，小窗口下不会把内容区挤没
        var maxBanner = Math.Max(120, h * 0.34);
        var banner = Math.Round(Math.Clamp(w * 0.24, 128, Math.Min(240, maxBanner)));
        if (Math.Abs(BannerRow.Height.Value - banner) > 0.5)
            BannerRow.Height = new GridLength(banner);
    }

    /// <summary>开场动画播完之后再建选项列表（那一步要排版几十行，放动画前面会卡）。</summary>
    public void LoadOptions() => VM.LoadOptions();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshLaunchState();
        UpdateBannerClip();
        BannerShell.SizeChanged += (_, _) => UpdateBannerClip();

        if (!_hooked)
        {
            _hooked = true;
            DownloadManager.Instance.Changed += OnDownloadsChanged;
            Unloaded += (_, _) => DownloadManager.Instance.Changed -= OnDownloadsChanged;
        }

        // 截图落盘放到弹框开场动画之后再做，别和动画抢 UI 线程
        Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => _ = VM.CacheScreenshotsAsync()));

        if (!VM.Item.HasDownloads)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => _ = VM.EnsureDownloadsAsync()));
        }
    }

    /// <summary>
    /// 横幅只圆上面两个角，并且是真裁剪：模糊底衬（BlurEffect）的半透明像素会糊到
    /// 圆弧外面，把方角填成灰块 —— 只靠各层 Border 的 CornerRadius 挡不住，
    /// 所以按实际尺寸生成「上圆下方」的几何做 Clip。
    /// </summary>
    private void UpdateBannerClip()
    {
        var w = BannerShell.ActualWidth;
        var h = BannerShell.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 面板有 1px 描边，横幅在里面，半径比面板的 16 小 1
        var r = Math.Min(15, Math.Min(w, h) / 2);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, r), true, true);
            ctx.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(w - r, 0), true, false);
            ctx.ArcTo(new Point(w, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(w, h), true, false);
            ctx.LineTo(new Point(0, h), true, false);
        }

        geo.Freeze();
        BannerShell.Clip = geo;
    }

    /// <summary>横幅（本地有封面文件就直接读盘，没有才走网络），不阻塞弹框。</summary>
    public Task LoadImagesAsync()
    {
        if (NetImages.Get(VM.Item.BannerSource) is { } banner)
        {
            BannerBrush.ImageSource = banner;
            BannerBackBrush.ImageSource = banner;
        }

        RefreshLaunchState();
        return Task.CompletedTask;
    }

    /// <summary>下载队列有变化（下完了 / 失败了）就重新判断「启动」能不能点。</summary>
    private void OnDownloadsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshLaunchState));

    /// <summary>重新判断「启动程序」按钮的状态。</summary>
    private void RefreshLaunchState() => VM.RefreshLaunchState();

    /// <summary>鼠标移到某一行就等于选中它（移出表格后保持，方便去点右侧的「启动程序」）。</summary>
    private void Row_Enter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TrainerDetailRowVM row }) VM.SelectRow(row);
    }

    /// <summary>点一行同样会选中它。</summary>
    private void Row_Select(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TrainerDetailRowVM row }) VM.SelectRow(row);
    }

    /// <summary>打开遮层并开始转圈（只有需要联网取数据时才调）。</summary>
    private void ShowLoadingOverlay()
    {
        if (LoadingOverlay.Visibility == Visibility.Visible) return;

        LoadingOverlay.Visibility = Visibility.Visible;
        SpinnerRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    /// <summary>收起遮层并停掉动画（留着动画会一直占着渲染线程）。</summary>
    private void HideLoadingOverlay()
    {
        if (LoadingOverlay.Visibility != Visibility.Visible) return;

        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>右下角那句提示：淡入 → 停 1.6 秒 → 淡出（文字已经由绑定写进控件）。</summary>
    /// <param name="message">要显示的话，仅用于日志与可读性。</param>
    private async Task ShowToast(string message)
    {
        _ = message;

        ToastText.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        await Task.Delay(1600);
        ToastText.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(360)));
    }

#if DEBUG
    /// <summary>自检用：封面是否已经挂上（验证「打开即有图」而不是等动画播完）。</summary>
    public bool BannerReady => BannerBrush.ImageSource is not null;

    /// <summary>自检用：切到文件下载选项卡。</summary>
    public void ShowDownloadTab() => VM.ShowDownloadsCommand.Execute(null);

    /// <summary>自检用：切到选项功能选项卡。</summary>
    public void ShowOptionsTab() => VM.ShowOptionsCommand.Execute(null);

    /// <summary>自检用：切到软件截图选项卡。</summary>
    public void ShowShotsTab() => VM.ShowShotsCommand.Execute(null);

    /// <summary>自检用：强制打开加载遮层，返回它的可见状态。</summary>
    public bool ShowLoadingForCheck()
    {
        VM.ShowLoadingForCheck();
        return LoadingOverlay.Visibility == Visibility.Visible;
    }

    /// <summary>自检用：遮层当前是否可见（本地数据进来应该是 false）。</summary>
    public bool OverlayVisible => LoadingOverlay.Visibility == Visibility.Visible;

    /// <summary>自检用：等同于点右上角那个刷新按钮。</summary>
    public void ReloadForCheck() => _ = VM.ReloadForCheckAsync();

    /// <summary>自检用：下载第一条文件。</summary>
    public void DownloadFirst() => VM.DownloadFirstForCheck();

    /// <summary>自检用：走一遍「启动程序」的完整流程（含启动成功后的收进托盘）。</summary>
    /// <param name="path">要启动的文件。</param>
    public void LaunchFile(string path) => VM.LaunchFileForCheck(path);
#endif
}
