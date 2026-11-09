using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;
using FlingTrainerX.Views;

namespace FlingTrainerX.Services;

/// <summary>
/// 右下角托盘图标：建图标、挂右键菜单、左键叫回主界面。
/// <para>
/// 它只管「托盘里那个图标」，不认识主窗口的布局：要打开界面 / 要退出都只抛事件，
/// 由外壳 ViewModel 收到后交给窗口执行。
/// </para>
/// </summary>
public sealed class TrayService : IDisposable
{
    private WinForms.NotifyIcon? _icon;
    private TrayMenuWindow? _menu;
    private Drawing.Icon? _image;

    /// <summary>右键菜单窗口（自检要直接调 ShowAtCursor 拍照）。</summary>
    internal TrayMenuWindow? Menu => _menu;

    /// <summary>托盘图标本体（自检要读 Visible）。</summary>
    internal WinForms.NotifyIcon? Icon => _icon;

    /// <summary>左键点图标 / 菜单里点「打开主界面」。</summary>
    public event EventHandler? OpenRequested;

    /// <summary>菜单里点「退出程序」。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// 建图标 + 右键菜单（可重复调用，只建一次）。
    /// </summary>
    /// <param name="tooltip">鼠标悬停时的提示文字。</param>
    public void EnsureCreated(string tooltip)
    {
        if (_icon is not null) return;

        var executablePath = Environment.ProcessPath;
        _image = !string.IsNullOrWhiteSpace(executablePath)
            ? Drawing.Icon.ExtractAssociatedIcon(executablePath) ?? (Drawing.Icon)Drawing.SystemIcons.Application.Clone()
            : (Drawing.Icon)Drawing.SystemIcons.Application.Clone();

        _menu = new TrayMenuWindow();
        _menu.OpenRequested += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _menu.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _icon = new WinForms.NotifyIcon
        {
            Icon = _image,
            Text = tooltip,
            Visible = true
        };

        _icon.MouseUp += (_, e) =>
        {
            // 右键 → 小菜单（打开主界面 / 退出程序）；左键 → 直接把主界面叫回来
            if (e.Button == WinForms.MouseButtons.Right)
                _menu?.Dispatcher.BeginInvoke(new Action(() => _menu?.ShowAtCursor()));
            else if (e.Button == WinForms.MouseButtons.Left)
                _menu?.Dispatcher.BeginInvoke(new Action(() => OpenRequested?.Invoke(this, EventArgs.Empty)));
        };
    }

    /// <summary>把图标藏起来（退出程序 / 更新前用）。</summary>
    public void HideIcon()
    {
        if (_icon is not null) _icon.Visible = false;
    }

    /// <summary>拆掉图标和菜单，释放 GDI 句柄。</summary>
    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }

        _menu?.Close();
        _menu = null;

        _image?.Dispose();
        _image = null;
    }
}
