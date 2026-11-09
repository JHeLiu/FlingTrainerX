namespace FlingTrainerX.ViewModels;

/// <summary>
/// 托盘右键小菜单（TrayMenuWindow.xaml）的视图模型：两个菜单项就是两条命令。
/// <para>
/// 菜单自己不知道怎么「打开主界面」、也不知道怎么退出程序 —— 它只抛事件，
/// 由主窗口订阅后执行，VM 里没有任何 Window / NotifyIcon 引用。
/// </para>
/// </summary>
public sealed partial class TrayMenuVM : ObservableObject
{
    /// <summary>要打开主界面（由主窗口执行 Show）。</summary>
    public event EventHandler? OpenRequested;

    /// <summary>要退出程序（由主窗口执行真正退出）。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>菜单项：打开主界面。</summary>
    [RelayCommand]
    private void Open() => OpenRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>菜单项：退出程序。</summary>
    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);
}
