using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FlingTrainerX.Views;

/// <summary>
/// 「已经启动」提示：第二次启动程序时弹一下，2.6 秒后自己消失，也可以点「知道了」
/// 或者在提示框空白处拖一下窗口。
/// </summary>
public partial class AlreadyRunningWindow : Window
{
    private readonly DispatcherTimer _autoClose;

    public AlreadyRunningWindow()
    {
        InitializeComponent();

        _autoClose = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        _autoClose.Tick += (_, _) => Close();
        _autoClose.Start();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        try { DragMove(); } catch { /* 忽略拖动异常 */ }
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoClose.Stop();
        base.OnClosed(e);
    }
}
