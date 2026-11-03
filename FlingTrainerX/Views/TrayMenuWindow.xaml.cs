using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace FlingTrainerX.Views;

public partial class TrayMenuWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;

    public TrayMenuWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => Hide();

        // Esc 关菜单，和系统菜单一致
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            Hide();
            e.Handled = true;
        };
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

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Drawing.Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
