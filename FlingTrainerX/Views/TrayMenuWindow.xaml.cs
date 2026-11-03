using System.Runtime.InteropServices;
using System.Windows;
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
    }

    public void ShowAtCursor()
    {
        var cursor = WinForms.Cursor.Position;
        var area = WinForms.Screen.FromPoint(cursor).WorkingArea;
        var scale = GetMonitorScale(cursor);
        var width = Width * scale;
        var height = Height * scale;

        Left = Math.Clamp(cursor.X / scale, area.Left / scale, Math.Max(area.Left, area.Right - width) / scale);
        Top = Math.Clamp(cursor.Y / scale, area.Top / scale, Math.Max(area.Top, area.Bottom - height) / scale);

        if (!IsVisible) Show();
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
