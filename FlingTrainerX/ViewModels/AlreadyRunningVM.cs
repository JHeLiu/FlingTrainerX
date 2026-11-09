using System.Windows.Threading;

namespace FlingTrainerX.ViewModels;

/// <summary>
/// 「已经启动」提示框（AlreadyRunningWindow.xaml）的视图模型：
/// 2.6 秒后自动关掉，点「知道了」也关掉。
/// <para>
/// 关窗口这个动作不在这里做：VM 只把「该关了」这件事抛给窗口，
/// 窗口自己 Close()，VM 不引用任何窗口对象。
/// </para>
/// </summary>
public sealed partial class AlreadyRunningVM : ObservableObject
{
    /// <summary>自动关闭的等待时间。</summary>
    private static readonly TimeSpan AutoCloseDelay = TimeSpan.FromMilliseconds(2600);

    private readonly DispatcherTimer _autoClose;

    public AlreadyRunningVM()
    {
        _autoClose = new DispatcherTimer { Interval = AutoCloseDelay };
        _autoClose.Tick += (_, _) =>
        {
            _autoClose.Stop();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };
        _autoClose.Start();
    }

    /// <summary>提示框该关了（自动超时或用户点了「知道了」）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>提示正文，XAML 直接绑定。</summary>
    [ObservableProperty]
    private string _message = "已经在运行，已为你切回原来的窗口。";

    /// <summary>按钮：知道了。</summary>
    [RelayCommand]
    private void Close()
    {
        _autoClose.Stop();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>窗口关掉时停掉计时器，别让它在后台继续跑。</summary>
    public void StopTimer() => _autoClose.Stop();
}
