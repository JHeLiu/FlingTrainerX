using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;
using FlingTrainerX.ViewModels;

namespace FlingTrainerX.Views;

/// <summary>
/// 托盘右键小菜单。菜单项本身不在这里做（见 <see cref="TrayMenuVM"/>），
/// 这个窗口只管「弹在光标旁边」「淡入」「点空白处收起」这些纯显示的事情。
/// </summary>
public partial class TrayMenuWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>菜单的视图模型：两个菜单项就是它身上的两条命令。</summary>
    public TrayMenuVM VM { get; } = new();

    public TrayMenuWindow()
    {
        InitializeComponent();
        DataContext = VM;

        // 菜单项点完先收起自己，再通知外面（先挂，保证比主窗口的处理早执行）
        VM.OpenRequested += (_, _) => Hide();
        VM.ExitRequested += (_, _) => Hide();

        Deactivated += (_, _) => Hide();

        // Esc 关菜单，和系统菜单一致
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            Hide();
            e.Handled = true;
        };
    }

    /// <summary>点「打开主界面」：先收起菜单，再通知外面。</summary>
    public event EventHandler? OpenRequested
    {
        add => VM.OpenRequested += value;
        remove => VM.OpenRequested -= value;
    }

    /// <summary>点「退出程序」。</summary>
    public event EventHandler? ExitRequested
    {
        add => VM.ExitRequested += value;
        remove => VM.ExitRequested -= value;
    }

    /// <summary>
    /// 在鼠标位置弹出：托盘菜单的习惯是从光标往上、往左铺开（贴着右下角的图标），
    /// 贴到屏幕边时再往里推，保证整块菜单都在工作区里。
    /// </summary>
    public void ShowAtCursor()
    {
        // 菜单已经开着时再右键一下 = 收起来，跟系统托盘菜单的手感一致
        if (IsVisible)
        {
            Hide();
            return;
        }

        var cursor = WinForms.Cursor.Position;
        var area = WinForms.Screen.FromPoint(cursor).WorkingArea;
        var scale = GetMonitorScale(cursor);

        // 菜单右边缘离光标 12px、下边缘离光标 4px，看起来像是从图标上「长」出来的
        var left = (cursor.X - Width * scale - 12 * scale) / scale;
        var top = (cursor.Y - Height * scale - 4 * scale) / scale;

        var minLeft = area.Left / scale;
        var minTop = area.Top / scale;
        var maxLeft = Math.Max(minLeft, (area.Right - Width * scale) / scale);
        var maxTop = Math.Max(minTop, (area.Bottom - Height * scale) / scale);

        Left = Math.Clamp(left, minLeft, maxLeft);
        Top = Math.Clamp(top, minTop, maxTop);

        if (!IsVisible) Show();

        // 90ms 淡入：比「啪」地一下蹦出来更接近 Steam 的手感
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90)));

        Activate();
    }

    /// <summary>取鼠标所在显示器的缩放比例（拿不到就按 100%）。</summary>
    /// <param name="point">屏幕坐标点。</param>
    private static double GetMonitorScale(Drawing.Point point)
    {
        try
        {
            var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96d;
        }
        catch (DllNotFoundException)
        {
        }

        return 1d;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Drawing.Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
