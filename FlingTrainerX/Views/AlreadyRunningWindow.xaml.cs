using System.Windows;
using System.Windows.Input;
using FlingTrainerX.ViewModels;

namespace FlingTrainerX.Views;

/// <summary>
/// 「已经启动」提示：第二次启动程序时弹一下，等一会儿自己消失，也可以点「知道了」
/// 或者在提示框空白处拖一下窗口。
/// <para>
/// 关不关、什么时候关由 <see cref="AlreadyRunningVM"/> 决定；这里只剩窗口自己的事：
/// 拖动、关闭动作、收尾停表。
/// </para>
/// </summary>
public partial class AlreadyRunningWindow : Window
{
    /// <summary>提示框的视图模型。</summary>
    public AlreadyRunningVM VM { get; } = new();

    public AlreadyRunningWindow()
    {
        InitializeComponent();
        DataContext = VM;

        VM.CloseRequested += (_, _) => Close();
    }

    /// <summary>按住窗口空白处拖动（无边框窗口自己实现拖拽）。</summary>
    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        try { DragMove(); } catch { /* 忽略拖动异常 */ }
    }

    protected override void OnClosed(EventArgs e)
    {
        VM.StopTimer();
        base.OnClosed(e);
    }
}
