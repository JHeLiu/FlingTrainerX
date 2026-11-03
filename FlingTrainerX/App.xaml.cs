using System.Windows;
#if DEBUG
using System.Windows.Threading;
#endif

namespace FlingTrainerX
{
    /// <summary>应用入口。</summary>
    public partial class App : System.Windows.Application
    {
#if DEBUG
        /// <summary>开发期把未处理异常写到临时目录，方便定位「点一下就退出」这类问题。</summary>
        private static void LogCrash(Exception? ex)
        {
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fling-crash.log");
                System.IO.File.AppendAllText(path,
                    $"[{DateTime.Now:HH:mm:ss}] {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}\n\n");
            }
            catch
            {
                // 日志写不出来就算了
            }
        }

#endif

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

#if DEBUG
            DispatcherUnhandledException += (_, args) =>
            {
                LogCrash(args.Exception);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);
#endif

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();

#if DEBUG
            // 托盘自检（仅 Debug）：<程序>.exe --tray-check <目录>
            var trayIndex = Array.IndexOf(e.Args, "--tray-check");
            if (trayIndex >= 0 && trayIndex + 1 < e.Args.Length && MainWindow is MainWindow trayWindow)
            {
                var trayDir = e.Args[trayIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await trayWindow.RunTrayCheckAsync(trayDir);
                    Shutdown();
                }));
            }

            // 卡片自适应自检（仅 Debug）：<程序>.exe --layout-check <目录>
            var layoutIndex = Array.IndexOf(e.Args, "--layout-check");
            if (layoutIndex >= 0 && layoutIndex + 1 < e.Args.Length && MainWindow is MainWindow layoutWindow)
            {
                var layoutDir = e.Args[layoutIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await layoutWindow.RunLayoutCheckAsync(layoutDir);
                    Shutdown();
                }));
            }

            // 清除缓存自检（仅 Debug）：<程序>.exe --clear-check <目录>
            var clearIndex = Array.IndexOf(e.Args, "--clear-check");
            if (clearIndex >= 0 && clearIndex + 1 < e.Args.Length && MainWindow is MainWindow clearWindow)
            {
                var clearDir = e.Args[clearIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await clearWindow.RunClearCacheCheckAsync(clearDir);
                    Shutdown();
                }));
            }

            // 托盘退出自检（仅 Debug）：<程序>.exe --tray-exit <目录>
            var exitIndex = Array.IndexOf(e.Args, "--tray-exit");
            if (exitIndex >= 0 && exitIndex + 1 < e.Args.Length && MainWindow is MainWindow exitWindow)
            {
                var exitDir = e.Args[exitIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await exitWindow.RunTrayExitCheckAsync(exitDir);
                }));
            }

            // 开发期自检（仅 Debug）：<程序>.exe --capture <目录>，跑一遍主要界面并截图后退出
            var index = Array.IndexOf(e.Args, "--capture");
            if (index < 0 || index + 1 >= e.Args.Length) return;

            var dir = e.Args[index + 1];
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                if (MainWindow is MainWindow window) await window.CaptureTourAsync(dir);
                Shutdown();
            }));
#endif
        }
    }
}
