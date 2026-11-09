using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace FlingTrainerX.Interop;

/// <summary>
/// 窗口外框那点系统级操作：圆角、按显示器算尺寸、最大化矩形、内容裁剪、前台窗口句柄。
/// 原先都散在 <c>MainWindow</c> 的分部类里（混着一堆 DllImport 常量），挪到这里之后
/// 窗口本身只剩「什么时候调」这一件事。
/// </summary>
public static class WindowChrome
{
    /// <summary>窗口能被拖到的最小尺寸；再小布局就挤了（版心按 1080 宽屏设计）。</summary>
    public const double MinWindowWidth = 1080;

    /// <summary>窗口能被拖到的最小高度。</summary>
    public const double MinWindowHeight = 680;

    private const uint MonitorDefaultToNearest = 2;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE：Windows 11 起支持的窗口圆角开关。</summary>
    private const int DwmWindowCornerPreference = 33;

    /// <summary>DWMWCP_ROUND：系统默认圆角半径。</summary>
    private const int DwmWindowCornerRound = 2;

    /// <summary>DWMWCP_DONOTROUND：不要圆角（最大化时用，四角要贴屏幕边）。</summary>
    private const int DwmWindowCornerDoNotRound = 1;

    private const int WmGetMinMaxInfo = 0x0024;

    /// <summary>
    /// 高 DPI 适配（PerMonitorV2）：按当前显示器的工作区（物理像素 ÷ 当前 DPI 缩放）算窗口大小，
    /// 100%、150%、200% 混合缩放下窗口都不会超出屏幕。
    /// </summary>
    /// <param name="window">要调整的窗口。</param>
    public static void ApplyWindowBounds(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        // 用窗口自己的 DPI（而不是视觉树的），跨屏移动时它和 MonitorFromWindow 才是同一套
        var scale = GetDpiForWindow(hwnd) / 96d;
        if (scale <= 0) scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        if (scale <= 0) scale = 1;

        var workWidth = (info.Work.Right - info.Work.Left) / scale;
        var workHeight = (info.Work.Bottom - info.Work.Top) / scale;
        if (workWidth < 320 || workHeight < 240) return;

        var width = Math.Min(1280, Math.Max(900, workWidth - 32));
        var height = Math.Min(780, Math.Max(520, workHeight - 32));

        // 窗口是固定尺寸，只有工作区放不下时才缩到放得下（1080p + 150% 缩放这类场景）
        // 最大化时 WindowChrome 会把窗口向外撑出一个边框宽度（左上角跑到 -7,-7），
        // 上限如果正好卡在工作区大小，撑出来的部分会被截掉，右边和下边就会漏出一条桌面缝隙
        window.MaxWidth = workWidth + 40;
        window.MaxHeight = workHeight + 40;
        window.MinWidth = Math.Min(MinWindowWidth, width);
        window.MinHeight = Math.Min(MinWindowHeight, height);
        window.Width = Math.Max(window.MinWidth, width);
        window.Height = Math.Max(window.MinHeight, height);
    }

    /// <summary>
    /// 自己算最大化矩形。WindowChrome 会把窗口往外撑一圈隐形边框（本机 96 DPI 下是 7px），
    /// 不补这一圈的话，内容要么到不了屏幕边（露出桌面缝隙），要么伸到任务栏底下被裁掉。
    /// </summary>
    /// <param name="window">当前窗口（取 DPI 和最小尺寸用）。</param>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="lParam">WM_GETMINMAXINFO 的载荷。</param>
    /// <param name="handled">是否已经处理。</param>
    public static void ApplyMaximizedBounds(Window window, IntPtr hwnd, IntPtr lParam, ref bool handled)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        // 隐形边框宽度 = (窗口宽 - 客户区宽) / 2，直接用当前窗口实测，换 DPI 也不用改常数
        var insetX = 0;
        var insetY = 0;
        if (GetWindowRect(hwnd, out var win) && GetClientRect(hwnd, out var client) && client.Right > 0)
        {
            insetX = Math.Max(0, ((win.Right - win.Left) - client.Right) / 2);
            insetY = Math.Max(0, ((win.Bottom - win.Top) - client.Bottom) / 2);
        }

        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        if (scale <= 0) scale = 1;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        // 最大化后内容区正好等于工作区（任务栏以上），外面那圈隐形边框跑到屏幕外
        mmi.MaxPosition.X = info.Work.Left - info.Monitor.Left - insetX;
        mmi.MaxPosition.Y = info.Work.Top - info.Monitor.Top - insetY;
        mmi.MaxSize.X = (info.Work.Right - info.Work.Left) + insetX * 2;
        mmi.MaxSize.Y = (info.Work.Bottom - info.Work.Top) + insetY * 2;

        // 接管了这条消息就要自己守住最小尺寸
        mmi.MinTrackSize.X = (int)Math.Round(window.MinWidth * scale);
        mmi.MinTrackSize.Y = (int)Math.Round(window.MinHeight * scale);

        // 拖拽调整大小时系统送来的上限可能是 0，不补一个合理值窗口会被压到最小尺寸
        var maxTrackX = (info.Monitor.Right - info.Monitor.Left) + insetX * 2;
        var maxTrackY = (info.Monitor.Bottom - info.Monitor.Top) + insetY * 2;
        if (mmi.MaxTrackSize.X < mmi.MinTrackSize.X) mmi.MaxTrackSize.X = maxTrackX;
        if (mmi.MaxTrackSize.Y < mmi.MinTrackSize.Y) mmi.MaxTrackSize.Y = maxTrackY;

        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
    }

    /// <summary>
    /// 让窗口四角变圆。优先用 Windows 11 的 DWM 原生圆角（自带抗锯齿和阴影），
    /// 系统不支持时退回自己裁剪内容区域。最大化时反过来要求直角。
    /// </summary>
    /// <param name="window">要处理的窗口。</param>
    /// <param name="rounded">true = 圆角，false = 直角。</param>
    public static void ApplyRoundedCorners(Window window, bool rounded)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var preference = rounded ? DwmWindowCornerRound : DwmWindowCornerDoNotRound;
            DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // 老系统没有这个 API，靠 UpdateShellClip 的裁剪兜底
        }
    }

    /// <summary>内容跟着外框一起裁圆角，侧栏和图片不会溢出到圆角外面。</summary>
    /// <param name="shell">最外层那块内容。</param>
    /// <param name="radius">圆角半径；最大化时传 0。</param>
    public static void UpdateShellClip(FrameworkElement shell, double radius)
    {
        if (shell.ActualWidth <= 0 || shell.ActualHeight <= 0) return;

        shell.Clip = new RectangleGeometry(
            new Rect(0, 0, shell.ActualWidth, shell.ActualHeight), radius, radius);
    }

    /// <summary>当前前台窗口的句柄。</summary>
    public static IntPtr ForegroundWindow => GetForegroundWindow();

    /// <summary>取一个 Popup 内容所在的窗口句柄，没建出来就是 0。</summary>
    /// <param name="popup">要取句柄的弹出层。</param>
    public static IntPtr PopupHandle(Popup popup) =>
        popup.Child is FrameworkElement child &&
        PresentationSource.FromVisual(child) is HwndSource source
            ? source.Handle
            : IntPtr.Zero;

    /// <summary>不激活窗口地把它移到指定位置（托盘还原时用）。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="x">左上角 X。</param>
    /// <param name="y">左上角 Y。</param>
    public static void MoveWithoutActivation(IntPtr hwnd, int x, int y) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

    /// <summary>把鼠标移到屏幕坐标（物理像素）。</summary>
    /// <param name="x">X。</param>
    /// <param name="y">Y。</param>
    public static void SetCursorPos(int x, int y) => SetCursorPosNative(x, y);

    /// <summary>物理像素的矩形（自检和截图要用整数坐标）。</summary>
    public struct PixelRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>取窗口矩形（物理像素）。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="rect">拿到的矩形。</param>
    public static bool TryGetWindowRect(IntPtr hwnd, out PixelRect rect)
    {
        var ok = GetWindowRect(hwnd, out var raw);
        rect = new PixelRect { Left = raw.Left, Top = raw.Top, Right = raw.Right, Bottom = raw.Bottom };
        return ok;
    }

    /// <summary>某个屏幕坐标落在哪块显示器上。</summary>
    /// <param name="x">屏幕 X（物理像素）。</param>
    /// <param name="y">屏幕 Y（物理像素）。</param>
    public static IntPtr MonitorHandleAtPoint(int x, int y) =>
        MonitorFromPoint(new Point32 { X = x, Y = y }, MonitorDefaultToNearest);

    /// <summary>某块显示器的 DPI。</summary>
    /// <param name="monitor">显示器句柄。</param>
    /// <param name="dpiX">横向 DPI。</param>
    /// <param name="dpiY">纵向 DPI。</param>
    public static int DpiOfMonitor(IntPtr monitor, out uint dpiX, out uint dpiY) =>
        GetDpiForMonitor(monitor, 0, out dpiX, out dpiY);

    /// <summary>不改变大小地移动窗口。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="x">目标 X。</param>
    /// <param name="y">目标 Y。</param>
    /// <param name="flags">SWP_* 标志。</param>
    public static bool MoveWindow(IntPtr hwnd, int x, int y, uint flags) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, flags);

    /// <summary>某个窗口的 DPI（96 = 100%）。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    public static uint DpiOf(IntPtr hwnd) => GetDpiForWindow(hwnd);

    /// <summary>窗口现在可不可见。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    public static bool IsHwndVisible(IntPtr hwnd) => IsWindowVisible(hwnd);

    /// <summary>把窗口带到前台。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    public static bool BringToFront(IntPtr hwnd) => SetForegroundWindow(hwnd);

    /// <summary>鼠标事件（自检模拟点击用）。</summary>
    public static void SendMouse(uint flags, uint dx, uint dy, uint data, UIntPtr extra) =>
        mouse_event(flags, dx, dy, data, extra);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X, Y;
    }

    /// <summary>WM_GETMINMAXINFO 的载荷，只用到最大化尺寸、位置和最小拖动尺寸。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point32 Reserved;
        public Point32 MaxSize;
        public Point32 MaxPosition;
        public Point32 MinTrackSize;
        public Point32 MaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point32 point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    private static extern bool SetCursorPosNative(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
