using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlingTrainerX.Models;
using FlingTrainerX.Services;
using FlingTrainerX.ViewModels;

namespace FlingTrainerX.Views;

/// <summary>
/// 主窗口里那几层浮层（关于 / 灵石结缘 / 更新说明 / 详情）需要的控件引用。
/// 由 MainWindow 在构造时一次性填好，交给 <see cref="AppOverlays"/> 播动画用。
/// </summary>
public sealed class OverlayElements
{
    public required Grid AboutLayer { get; init; }
    public required Rectangle AboutBackdrop { get; init; }
    public required Border AboutCard { get; init; }
    public required ScaleTransform AboutScale { get; init; }
    public required TranslateTransform AboutShift { get; init; }

    /// <summary>声明正文：每次打开都从 Assets\disclaimer.txt 重新灌一遍，改了文本不用重启。</summary>
    public required TextBox AboutBody { get; init; }

    public required Grid SupportLayer { get; init; }
    public required Rectangle SupportBackdrop { get; init; }
    public required Border SupportCard { get; init; }
    public required ScaleTransform SupportScale { get; init; }
    public required TranslateTransform SupportShift { get; init; }
    public required FrameworkElement AlipayFrame { get; init; }
    public required FrameworkElement WxpayFrame { get; init; }
    public required ImageBrush AlipayBrush { get; init; }
    public required ImageBrush WxpayBrush { get; init; }

    public required Grid UpdateLayer { get; init; }
    public required Rectangle UpdateBackdrop { get; init; }
    public required Border UpdateCard { get; init; }
    public required ScaleTransform UpdateScale { get; init; }
    public required TranslateTransform UpdateShift { get; init; }

    public required Grid DownloadLayer { get; init; }
    public required Rectangle DownloadBackdrop { get; init; }
    public required Border DownloadCard { get; init; }
    public required ScaleTransform DownloadScale { get; init; }
    public required TranslateTransform DownloadShift { get; init; }

    /// <summary>确认框 / 提示框的「确定」：主色 / 警示色两套样式和初始焦点只有视图能给，VM 只给 bool。</summary>
    public required Button ConfirmOkButton { get; init; }

    /// <summary>确认框整层（遮罩 + 卡片），显隐和动画都由 <see cref="AppOverlays"/> 管。</summary>
    public required Grid ConfirmLayer { get; init; }
    public required Rectangle ConfirmBackdrop { get; init; }
    public required Border ConfirmCard { get; init; }
    public required ScaleTransform ConfirmScale { get; init; }
    public required TranslateTransform ConfirmShift { get; init; }

    public required Grid DetailLayer { get; init; }
    public required Rectangle DetailBackdrop { get; init; }
    public required ContentControl DetailHost { get; init; }
    public required ScaleTransform DetailScale { get; init; }
    public required TranslateTransform DetailSlide { get; init; }
}

/// <summary>
/// 所有需要「碰控件」的界面动作都收在这里：开合关于 / 打赏 / 更新 / 详情四层浮层、
/// 弹确认框、打开浏览器、滚动列表。
/// <para>
/// 它是 <see cref="IAppDialogService"/> 的实现 —— ViewModel 只调方法，不认识任何控件；
/// 动画（淡入 + 轻微放大回弹）也只在这个文件里，窗口本体保持干净。
/// </para>
/// </summary>
public sealed class AppOverlays : IAppDialogService
{
    // 「关于」「打赏」「更新」共用同一套观感：淡入 + 从 0.93 轻微放大（带回弹），关闭时缩一点再淡出
    private static readonly TimeSpan OpenTime = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan CloseTime = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan BackdropTime = TimeSpan.FromMilliseconds(240);

    // 详情弹层比这几个弹窗大得多，起始缩放收到 0.96、回弹幅度收到 0.35，避免 1000 宽的面板晃得夸张
    private const double DetailScaleFrom = 0.96;
    private static readonly TimeSpan DetailOpenTime = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DetailCloseTime = TimeSpan.FromMilliseconds(220);

    private readonly Window _owner;
    private readonly OverlayElements _ui;
    private readonly ConfirmDialogVM _confirm;
    private readonly UpdateDialogVM _update;
    private readonly UpdateDownloadVM _download;
    private readonly Func<ScrollViewer?> _findListScroll;

    private int _aboutAnimToken;
    private int _supportAnimToken;
    private int _updateAnimToken;
    private int _detailAnimToken;
    private int _confirmAnimToken;

    /// <summary>流光只在第一次打开「灵石结缘」时挂一次。</summary>
    private bool _supportPostersReady;

    /// <summary>详情弹框现在开着没有。</summary>
    public bool IsDetailOpen { get; private set; }

    /// <summary>更新弹窗现在是不是「发现新版本」（决定点主按钮是安装还是只关闭）。</summary>
    public bool UpdatePromptMode => _update.Prompt;

    /// <summary>用户在更新弹窗里点了「立即更新」：由窗口接着调 ViewModel 装更新。</summary>
    public event EventHandler? UpdateAccepted;

    public AppOverlays(
        Window owner,
        OverlayElements ui,
        ConfirmDialogVM confirm,
        UpdateDialogVM update,
        UpdateDownloadVM updateDownload,
        Func<ScrollViewer?> findListScroll)
    {
        _owner = owner;
        _ui = ui;
        _confirm = confirm;
        _update = update;
        _download = updateDownload;
        _findListScroll = findListScroll;

        // 「取消更新 / 立即更新」两个按钮在 XAML 里绑的是 UpdateDialogVM 的命令，
        // 收场动画和下载得在这里做，所以把信号接回来。
        _update.CloseRequested += OnUpdateCloseRequested;

        // 确认框的字都绑在 VM 上，只剩两件视图的事：按 IsPrimary 换样式、开框时把焦点放「确定」上
        _confirm.PropertyChanged += OnConfirmChanged;
    }

    /// <summary>更新弹窗的按钮回调：true = 立即更新，false = 只关掉。</summary>
    private void OnUpdateCloseRequested(object? sender, bool accept)
    {
        if (accept) AcceptUpdate();
        else CloseUpdate();
    }

    /// <summary>
    /// 确认框开合的时刻：开的时候换样式、交焦点、播进场动画，
    /// 关的时候播收场动画再收层（和「关于」「更新」这些弹窗同一套观感）。
    /// </summary>
    private void OnConfirmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConfirmDialogVM.IsOpen)) return;

        if (_confirm.IsOpen)
        {
            _ui.ConfirmOkButton.Style =
                (Style)_owner.FindResource(_confirm.IsPrimary ? "PrimaryBtn" : "DangerBtn");
            _ui.ConfirmOkButton.Focus();

            ShowConfirmLayer();
            return;
        }

        HideConfirmLayer();
    }

    // ==================== 确认框 ====================

    /// <summary>弹一个确认框，等用户点确定 / 取消。</summary>
    public async Task<bool> ConfirmAsync(string title, string body, string okText, bool primary = false)
    {
        var answer = await ConfirmWithOptionAsync(title, body, okText, primary, null);
        return answer.Ok;
    }

    /// <summary>只带「知道了」的提示框：没有取消按钮，用户关掉之后才返回。</summary>
    public async Task NotifyAsync(string title, string body, string okText = "知道了")
    {
        var task = _confirm.Show(title, body, okText, true, null, cancelVisible: false);
        if (task is null) return;

        await task;
    }

    /// <summary>带一个可选项的确认框；<paramref name="option"/> 为 null 时不显示可选项。</summary>
    public Task<(bool Ok, bool Checked)> ConfirmWithOptionAsync(
        string title, string body, string okText, string optionText, string optionHint)
        => ConfirmWithOptionAsync(title, body, okText, true, (optionText, optionHint));

    private async Task<(bool Ok, bool Checked)> ConfirmWithOptionAsync(
        string title, string body, string okText, bool primary, (string Text, string Hint)? option)
    {
        // 已经有一个框开着（连点两下）就当作取消，别叠第二个
        var task = _confirm.Show(title, body, okText, primary, option);
        if (task is null) return (false, false);

        return await task;
    }

    /// <summary>确认框的确定 / 取消 / 点遮罩。</summary>
    /// <param name="ok">true = 确定。</param>
    public void CloseConfirm(bool ok) => _confirm.Close(ok);

    /// <summary>确认框进场：淡入 + 从 0.93 轻微放大（带回弹），和「关于 / 更新」弹窗同一套参数。</summary>
    private void ShowConfirmLayer()
    {
        ++_confirmAnimToken;
        _ui.ConfirmLayer.Visibility = Visibility.Visible;

        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        var spring = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };

        _ui.ConfirmBackdrop.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(BackdropTime)) { EasingFunction = fade });

        _ui.ConfirmCard.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170))) { EasingFunction = fade });

        _ui.ConfirmScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.93, 1, new Duration(OpenTime)) { EasingFunction = spring });
        _ui.ConfirmScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.93, 1, new Duration(OpenTime)) { EasingFunction = spring });

        _ui.ConfirmShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, new Duration(OpenTime)) { EasingFunction = fade });
    }

    /// <summary>确认框收场：缩一点再淡出，播完才收层；期间又开了新框就不收。</summary>
    private void HideConfirmLayer()
    {
        if (_ui.ConfirmLayer.Visibility != Visibility.Visible) return;

        var token = ++_confirmAnimToken;
        var fade = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(CloseTime);

        var cardFade = new DoubleAnimation(1, 0, duration) { EasingFunction = fade };

        // Completed 必须在 BeginAnimation 之前挂
        cardFade.Completed += (_, _) =>
        {
            if (token != _confirmAnimToken) return;

            _ui.ConfirmLayer.Visibility = Visibility.Collapsed;

            _ui.ConfirmCard.BeginAnimation(UIElement.OpacityProperty, null);
            _ui.ConfirmBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
            _ui.ConfirmScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _ui.ConfirmScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _ui.ConfirmShift.BeginAnimation(TranslateTransform.YProperty, null);
        };

        _ui.ConfirmCard.BeginAnimation(UIElement.OpacityProperty, cardFade);
        _ui.ConfirmScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        _ui.ConfirmScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        _ui.ConfirmShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, 6, duration) { EasingFunction = fade });
        _ui.ConfirmBackdrop.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, duration) { EasingFunction = fade });
    }

    // ==================== 关于 ====================

    /// <summary>打开「关于 / 免责声明」。</summary>
    public void ShowAbout()
    {
        // 正文来自程序目录下的 Assets\disclaimer.txt。
        // 之前这里漏了这一步，盒子一直是空的 —— 打开「关于」只看到标题没有声明，就是这个原因。
        // 每次打开都重读：文件小，读一次可以忽略；好处是改完文本不用重启程序。
        _ui.AboutBody.Text = DisclaimerText.Load();

        ++_aboutAnimToken;
        _ui.AboutLayer.Visibility = Visibility.Visible;

        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        // 回弹：Amplitude 越大弹得越明显；0.5 实测大约多冲出去 0.5% 再回落，肉眼刚好能察觉
        var spring = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };

        _ui.AboutBackdrop.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(BackdropTime)) { EasingFunction = fade });

        _ui.AboutCard.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170))) { EasingFunction = fade });

        _ui.AboutScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.93, 1, new Duration(OpenTime)) { EasingFunction = spring });
        _ui.AboutScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.93, 1, new Duration(OpenTime)) { EasingFunction = spring });

        _ui.AboutShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, new Duration(OpenTime)) { EasingFunction = fade });
    }

    /// <summary>收起「关于」。</summary>
    public void CloseAbout()
    {
        if (_ui.AboutLayer.Visibility != Visibility.Visible) return;

        var token = ++_aboutAnimToken;
        var fade = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(CloseTime);

        var cardFade = new DoubleAnimation(1, 0, duration) { EasingFunction = fade };

        // 注意：Completed 必须在 BeginAnimation 之前挂，先启动再挂回调的话永远不会触发
        cardFade.Completed += (_, _) =>
        {
            // 播完时如果用户又点开了（token 变了），就别收层、也别清动画
            if (token != _aboutAnimToken) return;

            _ui.AboutLayer.Visibility = Visibility.Collapsed;

            // 清掉动画值，让元素回到 XAML 里的初始态，下次打开才能从头播
            _ui.AboutCard.BeginAnimation(UIElement.OpacityProperty, null);
            _ui.AboutBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
            _ui.AboutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _ui.AboutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _ui.AboutShift.BeginAnimation(TranslateTransform.YProperty, null);
        };

        // 卡片先淡出、缩一点点（0.97）再收层，比直接消失柔和
        _ui.AboutCard.BeginAnimation(UIElement.OpacityProperty, cardFade);
        _ui.AboutScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        _ui.AboutScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        _ui.AboutShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, 6, duration) { EasingFunction = fade });
        _ui.AboutBackdrop.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, duration) { EasingFunction = fade });
    }

    // ==================== 灵石结缘 ====================

    /// <summary>打开「灵石结缘」。</summary>
    public void ShowSupport()
    {
        ++_supportAnimToken;
        _ui.SupportLayer.Visibility = Visibility.Visible;
        EnsureSupportPosters();

        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        var spring = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };

        _ui.SupportBackdrop.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(BackdropTime)) { EasingFunction = fade });

        _ui.SupportCard.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170))) { EasingFunction = fade });

        _ui.SupportScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.93, 1, new Duration(OpenTime)) { EasingFunction = spring });
        _ui.SupportScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.93, 1, new Duration(OpenTime)) { EasingFunction = spring });

        _ui.SupportShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, new Duration(OpenTime)) { EasingFunction = fade });
    }

    /// <summary>收起「灵石结缘」。</summary>
    public void CloseSupport()
    {
        if (_ui.SupportLayer.Visibility != Visibility.Visible) return;

        var token = ++_supportAnimToken;
        var fade = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(CloseTime);

        var cardFade = new DoubleAnimation(1, 0, duration) { EasingFunction = fade };

        cardFade.Completed += (_, _) =>
        {
            if (token != _supportAnimToken) return;

            _ui.SupportLayer.Visibility = Visibility.Collapsed;
            _ui.SupportCard.BeginAnimation(UIElement.OpacityProperty, null);
            _ui.SupportBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
            _ui.SupportScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _ui.SupportScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _ui.SupportShift.BeginAnimation(TranslateTransform.YProperty, null);
        };

        _ui.SupportCard.BeginAnimation(UIElement.OpacityProperty, cardFade);
        _ui.SupportScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        _ui.SupportScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        _ui.SupportShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, 6, duration) { EasingFunction = fade });
        _ui.SupportBackdrop.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, duration) { EasingFunction = fade });
    }

    /// <summary>第一次打开弹窗时把两张收款码读进画笔；读不到就把对应那格收掉。</summary>
    private void EnsureSupportPosters()
    {
        if (_supportPostersReady) return;

        _supportPostersReady = true;

        if (!LoadPoster(_ui.AlipayBrush, "Assets/alipay.jpeg")) _ui.AlipayFrame.Visibility = Visibility.Collapsed;
        if (!LoadPoster(_ui.WxpayBrush, "Assets/wxpay.jpeg")) _ui.WxpayFrame.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 把收款码读进画笔。读不到就当作没有这张图（返回 false，调用方把整块收掉），
    /// 绝不让「图片资源没打进程序集」变成启动就崩。
    /// </summary>
    private static bool LoadPoster(ImageBrush brush, string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/{path}", UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;   // 立刻解码，缺资源在这里就能抛出来
            image.EndInit();
            image.Freeze();

            brush.ImageSource = image;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[灵石结缘] 收款码加载失败 {path}：{ex.Message}");
            return false;
        }
    }

    // ==================== 更新说明 ====================

    /// <summary>打开更新说明 / 发现新版本弹窗。</summary>
    public void ShowUpdate(UpdateRelease release, bool prompt)
    {
        _update.Show(release, prompt);
        OpenUpdateLayer();
    }

    /// <summary>打开「更新内容」：正文来自本地 Assets\changelog.txt，永远只有「知道了」。</summary>
    public void ShowUpdateNotes(string title, string versionLine, string notes)
    {
        _update.ShowNotes(title, versionLine, notes);
        OpenUpdateLayer();
    }

    /// <summary>更新弹窗的进场动画（淡入 + 从 0.93 回弹放大），两种形态共用。</summary>
    private void OpenUpdateLayer()
    {
        ++_updateAnimToken;

        _update.IsOpen = true;
        _ui.UpdateLayer.Visibility = Visibility.Visible;

        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        var spring = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };

        _ui.UpdateBackdrop.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(BackdropTime)) { EasingFunction = fade });

        _ui.UpdateCard.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170))) { EasingFunction = fade });

        _ui.UpdateScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.93, 1, new Duration(OpenTime)) { EasingFunction = spring });
        _ui.UpdateScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.93, 1, new Duration(OpenTime)) { EasingFunction = spring });

        _ui.UpdateShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, new Duration(OpenTime)) { EasingFunction = fade });
    }

    /// <summary>收起更新弹窗（点遮罩、Esc、「取消更新」「知道了」都走这里）。</summary>
    public void CloseUpdate()
    {
        if (_ui.UpdateLayer.Visibility != Visibility.Visible) return;

        var token = ++_updateAnimToken;
        var fade = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(CloseTime);

        var cardFade = new DoubleAnimation(1, 0, duration) { EasingFunction = fade };

        cardFade.Completed += (_, _) =>
        {
            if (token != _updateAnimToken) return;

            _ui.UpdateLayer.Visibility = Visibility.Collapsed;
            _update.Close();

            _ui.UpdateCard.BeginAnimation(UIElement.OpacityProperty, null);
            _ui.UpdateBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
            _ui.UpdateScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _ui.UpdateScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _ui.UpdateShift.BeginAnimation(TranslateTransform.YProperty, null);
        };

        _ui.UpdateCard.BeginAnimation(UIElement.OpacityProperty, cardFade);
        _ui.UpdateScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        _ui.UpdateScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = fade });
        _ui.UpdateShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, 6, duration) { EasingFunction = fade });
        _ui.UpdateBackdrop.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, duration) { EasingFunction = fade });
    }

    /// <summary>「立即更新」/「知道了」：前者收框后开始下载，后者只关闭。</summary>
    public void AcceptUpdate()
    {
        var prompt = _update.Prompt;

        CloseUpdate();
        if (!prompt) return;

        // 等收场动画走完再开始下载，否则两层窗口会叠在一起
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            UpdateAccepted?.Invoke(this, EventArgs.Empty);
        };
        timer.Start();
    }

    /// <summary>打开下载浮层（淡入 + 轻微放大），数字由 <see cref="UpdateDownloadVM"/> 驱动。</summary>
    private void ShowDownload()
    {
        _ui.DownloadLayer.Visibility = Visibility.Visible;

        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        var spring = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };

        _ui.DownloadBackdrop.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(BackdropTime)) { EasingFunction = fade });

        _ui.DownloadCard.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170))) { EasingFunction = fade });

        _ui.DownloadScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.93, 1, new Duration(OpenTime)) { EasingFunction = spring });
        _ui.DownloadScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.93, 1, new Duration(OpenTime)) { EasingFunction = spring });

        _ui.DownloadShift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, new Duration(OpenTime)) { EasingFunction = fade });
    }

    /// <summary>收起下载浮层。</summary>
    private void CloseDownload()
    {
        if (_ui.DownloadLayer.Visibility != Visibility.Visible) return;

        var fade = new CubicEase { EasingMode = EasingMode.EaseIn };
        var cardFade = new DoubleAnimation(1, 0, new Duration(CloseTime)) { EasingFunction = fade };

        cardFade.Completed += (_, _) =>
        {
            _ui.DownloadLayer.Visibility = Visibility.Collapsed;
            _download.Finish();

            _ui.DownloadCard.BeginAnimation(UIElement.OpacityProperty, null);
            _ui.DownloadBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
        };

        _ui.DownloadCard.BeginAnimation(UIElement.OpacityProperty, cardFade);
        _ui.DownloadBackdrop.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, new Duration(BackdropTime)) { EasingFunction = fade });
    }

    /// <summary>
    /// 下载更新包：开浮层 → 边下边刷进度 → 收浮层，返回落地文件路径。
    /// 用户点「取消」返回 null；其它异常原样抛给调用方去提示。
    /// </summary>
    /// <param name="release">要下载的发行版。</param>
    public async Task<string?> DownloadUpdateAsync(UpdateRelease release)
    {
        _download.Begin(release);
        ShowDownload();

        try
        {
            // Progress<T> 会回到创建它的线程（这里是 UI 线程）回调，进度更新不用自己切线程
            var progress = new Progress<DownloadProgress>(_download.Report);
            var path = await UpdateService.DownloadAsync(release, progress, _download.Token).ConfigureAwait(true);

            CloseDownload();
            return path;
        }
        catch (OperationCanceledException)
        {
            CloseDownload();
            return null;
        }
        catch
        {
            CloseDownload();
            throw;
        }
    }

    /// <summary>把下载好的安装程序拉起来，安装界面由它自己接管。</summary>
    /// <param name="path">安装包路径。</param>
    public void LaunchInstaller(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? Environment.CurrentDirectory
        });
    }

    /// <summary>更新前的真退出：解压器要等本进程完全退出才替换文件，不能走「收进托盘」。</summary>
    public void ShutdownForUpdate() => Environment.Exit(0);

    // ==================== 外链 / 滚动 ====================

    /// <summary>用系统默认浏览器打开链接；失败就静默忽略。</summary>
    public void OpenExternal(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器不该影响别的事
        }
    }

    /// <summary>把卡片列表滚回顶部。</summary>
    public void ScrollToTop() => _findListScroll()?.ScrollToTop();

    // ==================== 详情弹层 ====================

    /// <summary>打开详情弹框。</summary>
    public void ShowDetail(TrainerItem item, bool favorite, Action<bool> favoriteChanged, Action launched)
    {
        var view = new TrainerDetailView(item, favorite);
        view.CloseRequested += (_, _) => CloseDetail();
        view.FavoriteChanged += (_, _) => favoriteChanged(view.IsFavorite);

        // 启动修改器成功 → 本程序自动收进托盘（右下角图标，右键可「打开主界面 / 退出程序」）
        view.Launched += (_, _) => launched();
        view.FitTo(_ui.DetailLayer.ActualWidth is > 0 ? _ui.DetailLayer.ActualWidth : 1280,
                   _ui.DetailLayer.ActualHeight is > 0 ? _ui.DetailLayer.ActualHeight : 780);

        _ui.DetailHost.Content = view;
        _ui.DetailLayer.Visibility = Visibility.Visible;
        IsDetailOpen = true;

        // 封面跟着弹窗一起上屏，不再等动画播完：卡片刚显示过同一张图，
        // 位图基本还在 NetImages 的缓存里
        _ = view.LoadImagesAsync();

        // 先把布局算完，动画只做位图合成：期间不重排、不重绘文字，稳定 60 帧
        _ui.DetailBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
        _ui.DetailBackdrop.Opacity = 0;
        _ui.DetailHost.BeginAnimation(UIElement.OpacityProperty, null);
        _ui.DetailHost.Opacity = 0;
        _ui.DetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _ui.DetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _ui.DetailScale.ScaleX = _ui.DetailScale.ScaleY = DetailScaleFrom;
        _ui.DetailSlide.BeginAnimation(TranslateTransform.YProperty, null);
        _ui.DetailSlide.Y = 10;
        _ui.DetailHost.UpdateLayout();

        _ui.DetailHost.CacheMode = new BitmapCache();

        var token = ++_detailAnimToken;

        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        // 和关于弹窗同一档回弹（0.5）：从 0.96 起跳，实测过冲约 0.5%、350ms 左右落定
        var spring = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };

        _ui.DetailBackdrop.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(BackdropTime)) { EasingFunction = fade });

        _ui.DetailHost.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170))) { EasingFunction = fade });

        // X / Y 各用一条独立时间线：同一个 Timeline 复用给两个属性会各自生成时钟，Completed 会触发两次
        var zoomX = new DoubleAnimation(DetailScaleFrom, 1, new Duration(DetailOpenTime)) { EasingFunction = spring };
        var zoomY = new DoubleAnimation(DetailScaleFrom, 1, new Duration(DetailOpenTime)) { EasingFunction = spring };

        // 注意：Completed 必须在 BeginAnimation 之前挂，先启动再挂回调的话永远不会触发
        zoomY.Completed += (_, _) =>
        {
            // 开场还没播完就被关掉（token 变了）就别再去构建列表了
            if (token != _detailAnimToken) return;

            // 动画结束后恢复实时渲染，滚动与文字才清晰；选项列表再依次上屏
            _ui.DetailHost.CacheMode = null;
            _owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(view.LoadOptions));
        };

        _ui.DetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoomX);
        _ui.DetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoomY);
        _ui.DetailSlide.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, new Duration(DetailOpenTime)) { EasingFunction = fade });
    }

    /// <summary>收起详情弹框。</summary>
    public void CloseDetail()
    {
        if (!IsDetailOpen) return;
        IsDetailOpen = false;

        _ui.DetailHost.CacheMode = new BitmapCache();

        var token = ++_detailAnimToken;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(DetailCloseTime);

        var cardFade = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };

        cardFade.Completed += (_, _) =>
        {
            // 播到一半又被点开（token 变了）就别收层，否则会把刚打开的详情页一起关掉
            if (token != _detailAnimToken) return;

            _ui.DetailLayer.Visibility = Visibility.Collapsed;
            _ui.DetailHost.Content = null;
            _ui.DetailHost.CacheMode = null;

            // 清掉动画值，回到 XAML 里的初始态（0.96 / 透明），下次打开才能从头播
            _ui.DetailHost.BeginAnimation(UIElement.OpacityProperty, null);
            _ui.DetailBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
            _ui.DetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _ui.DetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _ui.DetailSlide.BeginAnimation(TranslateTransform.YProperty, null);
            _ui.DetailHost.Opacity = 0;
            _ui.DetailBackdrop.Opacity = 0;
            _ui.DetailScale.ScaleX = _ui.DetailScale.ScaleY = DetailScaleFrom;
            _ui.DetailSlide.Y = 10;
        };

        // 面板比关于弹窗大得多，收场只缩到 0.97、位移 6，够柔和又不晃
        _ui.DetailHost.BeginAnimation(UIElement.OpacityProperty, cardFade);
        _ui.DetailScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = ease });
        _ui.DetailScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.97, duration) { EasingFunction = ease });
        _ui.DetailSlide.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, 6, duration) { EasingFunction = ease });
        _ui.DetailBackdrop.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, duration) { EasingFunction = ease });
    }

    /// <summary>窗口尺寸变了：详情弹框跟着重算尺寸。</summary>
    /// <param name="width">可用宽度。</param>
    /// <param name="height">可用高度。</param>
    public void FitDetail(double width, double height)
    {
        if (IsDetailOpen && _ui.DetailHost.Content is TrainerDetailView view)
            view.FitTo(width, height);
    }
}
