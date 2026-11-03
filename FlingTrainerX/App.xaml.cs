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
