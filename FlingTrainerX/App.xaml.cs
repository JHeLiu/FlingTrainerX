using System.Windows;
#if DEBUG
using System.Windows.Threading;
#endif

namespace FlingTrainerX
{
    /// <summary>应用入口。</summary>
    public partial class App : Application
    {
#if DEBUG
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 开发期自检（仅 Debug）：FlingTrainerX.exe --capture <目录>，跑一遍主要界面并截图后退出
            var index = Array.IndexOf(e.Args, "--capture");
            if (index < 0 || index + 1 >= e.Args.Length) return;

            var dir = e.Args[index + 1];
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                if (MainWindow is MainWindow window) await window.CaptureTourAsync(dir);
                Shutdown();
            }));
        }
#endif
    }
}
