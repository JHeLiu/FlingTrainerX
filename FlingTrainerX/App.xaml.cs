using System.Windows;
using FlingTrainerX.Services;
using FlingTrainerX.Views;
#if DEBUG
using System.Windows.Threading;
#endif

namespace FlingTrainerX
{
    /// <summary>
    /// 启动耗时打点：只有设了环境变量 <c>FTX_BOOT_TRACE=1</c> 才生效，
    /// 每次追加「从进程启动算起的毫秒数 + 阶段名」到 %TEMP%\fling-boot.log。
    /// 平时零开销，排查「启动慢 / 白屏久」时打开看各阶段的真实耗时。
    /// </summary>
    internal static class BootTrace
    {
        private static readonly bool Enabled =
            Environment.GetEnvironmentVariable("FTX_BOOT_TRACE") == "1";

        private static readonly DateTime Started = System.Diagnostics.Process.GetCurrentProcess().StartTime;
        private static readonly object Gate = new();

        public static void Mark(string what)
        {
            if (!Enabled) return;

            try
            {
                var ms = (long)(DateTime.Now - Started).TotalMilliseconds;
                lock (Gate)
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fling-boot.log"),
                        $"{ms,6} ms  {what}\n");
            }
            catch
            {
                // 打点写不进去不影响启动
            }
        }
    }

    /// <summary>应用入口。</summary>
    public partial class App : System.Windows.Application
    {
        /// <summary>
        /// 单实例用的名字：Mutex 判断「是不是已经有一个在跑」，
        /// 命名事件负责把「有人又双击了一次图标」转告给那个已经在跑的实例。
        /// </summary>
        private const string InstanceKey = @"Local\FlingTrainerX.FlingTrainer.Storebag";
        private const string ShowSignalName = InstanceKey + ".Show";

        private Mutex? _instanceMutex;

        /// <summary>当前进程是否真的拿到了这把锁（没拿到时退出不能去 Release，否则会抛异常）。</summary>
        private bool _ownsMutex;

        private EventWaitHandle? _showSignal;
        private RegisteredWaitHandle? _showWait;

#if DEBUG
        /// <summary>开发期自检参数：带这些参数启动时跳过单实例判断（仅 Debug 编译生效）。</summary>
        private static bool IsSelfCheckRun(string[] args) => args.Any(a => a is
            "--capture" or "--mask-check" or "--tip-check" or "--ua-check" or "--corner-check"
            or "--rim-check" or "--count-check" or "--clear-check" or "--tab-check"
            or "--detail-check" or "--tray-check" or "--layout-check" or "--tray-exit"
            or "--page-check" or "--scroll-check" or "--name-check"
            or "--search-check" or "--suggest-click-check"
            or "--about-check" or "--sync-ui-check"
            or "--sync-menu-check" or "--local-search-check" or "--sync-combo-check"
            or "--sync-run-check" or "--empty-sync-check"
            or "--sync-menu-track-check" or "--dpi-check" or "--text-mode-check" or "--font-check"
            or "--weight-check" or "--update-check" or "--quick-names-check"
            or "--sync-existing-check");
#endif

        /// <summary>
        /// 把未处理异常写到临时目录，方便定位「点一下就退出」这类问题。
        /// Debug / Release 都记，否则发布版闪退时无从查起。
        /// </summary>
        private static void LogCrash(Exception? ex)
        {
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fling-crash.log");

                // XAML 的异常外层只是一句「提供值时引发了异常」，真正的原因在 InnerException 里，
                // 所以整条链都写下来，不然等于没日志
                var text = new System.Text.StringBuilder();
                text.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n");

                var depth = 0;
                for (var e = ex; e is not null && depth < 6; e = e.InnerException, depth++)
                {
                    text.Append(depth == 0 ? "" : "  └─ 内层：");
                    text.Append($"{e.GetType().Name}: {e.Message}\n");
                    if (depth == 0) text.Append($"{e.StackTrace}\n");
                }

                text.Append('\n');
                System.IO.File.AppendAllText(path, text.ToString());
            }
            catch
            {
                // 日志写不出来就算了
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            BootTrace.Mark("OnStartup 进入");

            // 配置：启动最前面读一次 config.json（版本号 + 是否自动检查更新），之后全进程用静态字段里的值
            AppConfig.Initialize();
            BootTrace.Mark($"配置就绪 {AppConfig.Describe()}");

            // 官网偶尔有页面在响应头 / meta 里声明 iso-2022-cn 这类冷门编码。
            // .NET 默认只认 UTF-8 / UTF-16 等少数几种，遇到没注册的编码会直接抛
            // ArgumentException，那一款游戏的抓取就白跑了。启动时把代码页编码全注册上。
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            // 没有主窗口时（第二个实例）也要能自己退干净，别留一个没有窗口的僵尸进程
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            DispatcherUnhandledException += (_, args) =>
            {
                LogCrash(args.Exception);
#if DEBUG
                // 开发期吞掉，方便继续调试；发布版照常崩，不掩盖问题
                args.Handled = true;
#endif
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);

#if DEBUG
            // UA 自检（仅 Debug）：<程序>.exe --ua-check [输出文件]，把拼出来的 UA 写盘后退出
            var uaIndex = Array.IndexOf(e.Args, "--ua-check");
            if (uaIndex >= 0)
            {
                var uaPath = uaIndex + 1 < e.Args.Length
                    ? e.Args[uaIndex + 1]
                    : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fling-ua.txt");
                System.IO.File.WriteAllText(uaPath,
                    UserAgentProvider.UserAgent + "\n" + UserAgentProvider.Describe() + "\n");
                Shutdown(0);
                return;
            }
#endif

            // 已经有一个在跑：提示一句、把那个窗口叫到前面，然后本进程退出，不再开第二个窗口
#if DEBUG
            // 自检进程不参与单实例：本机开着一个实例时也能跑 --capture / --detail-check 这类检查，
            // 否则自检会被"已经启动"挡掉，什么也不产出
            if (!IsSelfCheckRun(e.Args) && !BecomeSingleInstance()) return;
#else
            if (!BecomeSingleInstance()) return;
#endif

            try
            {
                BootTrace.Mark("开始 new MainWindow");

                // 先把「读本地库」这个活扔到后台，再建窗口：解析 XAML + 建窗口大约要一秒，
                // 够它把 SQLite 读完、把首屏封面解好码，窗口第一次画出来就是满的卡片
                var startupData = FlingTrainerX.MainWindow.BeginStartupDataLoad();

                var mainWindow = new MainWindow(startupData);
                MainWindow = mainWindow;
                BootTrace.Mark("MainWindow 构造完成，Show");
                mainWindow.Show();
                BootTrace.Mark("Show 返回");
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                throw;
            }

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

            // 确认框遮罩自检（仅 Debug）：<程序>.exe --mask-check <目录>
            var maskIndex = Array.IndexOf(e.Args, "--mask-check");
            if (maskIndex >= 0 && maskIndex + 1 < e.Args.Length && MainWindow is MainWindow maskWindow)
            {
                var maskDir = e.Args[maskIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await maskWindow.RunMaskCheckAsync(maskDir);
                    Shutdown();
                }));
            }

            // 数量口径自检（仅 Debug）：<程序>.exe --count-check <目录>
            var countIndex = Array.IndexOf(e.Args, "--count-check");
            if (countIndex >= 0 && countIndex + 1 < e.Args.Length && MainWindow is MainWindow countWindow)
            {
                var countDir = e.Args[countIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await countWindow.RunCountCheckAsync(countDir);
                    Shutdown();
                }));
            }

            // 圆角画法对照（仅 Debug）：<程序>.exe --rim-check <目录>
            var rimIndex = Array.IndexOf(e.Args, "--rim-check");
            if (rimIndex >= 0 && rimIndex + 1 < e.Args.Length && MainWindow is MainWindow rimWindow)
            {
                var rimDir = e.Args[rimIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await rimWindow.RunRimCheckAsync(rimDir);
                    Shutdown();
                }));
            }

            // 页签圆角描边对照（仅 Debug）：<程序>.exe --tab-check <目录>
            var tabIndex = Array.IndexOf(e.Args, "--tab-check");
            if (tabIndex >= 0 && tabIndex + 1 < e.Args.Length && MainWindow is MainWindow tabWindow)
            {
                var tabDir = e.Args[tabIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await tabWindow.RunTabCheckAsync(tabDir);
                    Shutdown();
                }));
            }

            // 圆角边缘自检（仅 Debug）：<程序>.exe --corner-check <目录>
            var cornerIndex = Array.IndexOf(e.Args, "--corner-check");
            if (cornerIndex >= 0 && cornerIndex + 1 < e.Args.Length && MainWindow is MainWindow cornerWindow)
            {
                var cornerDir = e.Args[cornerIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await cornerWindow.RunCornerCheckAsync(cornerDir);
                    Shutdown();
                }));
            }

            // 「已经启动」提示框自检（仅 Debug）：<程序>.exe --tip-check <目录>
            var tipIndex = Array.IndexOf(e.Args, "--tip-check");
            if (tipIndex >= 0 && tipIndex + 1 < e.Args.Length && MainWindow is MainWindow tipWindow)
            {
                var tipDir = e.Args[tipIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await tipWindow.RunTipCheckAsync(tipDir);
                    Shutdown();
                }));
            }

            // 上下分页自检（仅 Debug）：<程序>.exe --page-check <目录>
            var pageIndex = Array.IndexOf(e.Args, "--page-check");
            if (pageIndex >= 0 && pageIndex + 1 < e.Args.Length && MainWindow is MainWindow pageWindow)
            {
                var pageDir = e.Args[pageIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await pageWindow.RunPageCheckAsync(pageDir);
                    Shutdown();
                }));
            }

            // 收藏夹搜索 / 分页自检（仅 Debug）：<程序>.exe --favorite-check <目录>
            var favoriteIndex = Array.IndexOf(e.Args, "--favorite-check");
            if (favoriteIndex >= 0 && favoriteIndex + 1 < e.Args.Length && MainWindow is MainWindow favoriteWindow)
            {
                var favoriteDir = e.Args[favoriteIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await favoriteWindow.RunFavoriteCheckAsync(favoriteDir);
                    Shutdown();
                }));
            }

            // 超过一页（>15 条）时列表可滚动自检（仅 Debug）：<程序>.exe --scroll-check <目录>
            var scrollIndex = Array.IndexOf(e.Args, "--scroll-check");
            if (scrollIndex >= 0 && scrollIndex + 1 < e.Args.Length && MainWindow is MainWindow scrollWindow)
            {
                var scrollDir = e.Args[scrollIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await scrollWindow.RunScrollCheckAsync(scrollDir);
                    Shutdown();
                }));
            }

            // 游戏名库 + 搜索提示 + Steam 中文名自检（仅 Debug）：<程序>.exe --name-check <目录>
            var nameIndex = Array.IndexOf(e.Args, "--name-check");
            if (nameIndex >= 0 && nameIndex + 1 < e.Args.Length && MainWindow is MainWindow nameWindow)
            {
                var nameDir = e.Args[nameIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await nameWindow.RunNameCheckAsync(nameDir);
                    Shutdown();
                }));
            }

            // 鼠标点智能提示项自检（仅 Debug）：<程序>.exe --suggest-click-check <目录>
            var clickIndex = Array.IndexOf(e.Args, "--suggest-click-check");
            if (clickIndex >= 0 && clickIndex + 1 < e.Args.Length && MainWindow is MainWindow clickWindow)
            {
                var clickDir = e.Args[clickIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await clickWindow.RunSuggestClickCheckAsync(clickDir);
                    Shutdown();
                }));
            }

            // 搜索关键词的编码 / 中文名翻译自检（仅 Debug）：<程序>.exe --search-check <目录>
            var searchIndex = Array.IndexOf(e.Args, "--search-check");
            if (searchIndex >= 0 && searchIndex + 1 < e.Args.Length && MainWindow is MainWindow searchWindow)
            {
                var searchDir = e.Args[searchIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await searchWindow.RunSearchCheckAsync(searchDir);
                    Shutdown();
                }));
            }

            // 空库首页的「同步数据」按钮自检（仅 Debug）：<程序>.exe --empty-sync-check <目录>
            var emptySyncIndex = Array.IndexOf(e.Args, "--empty-sync-check");
            if (emptySyncIndex >= 0 && emptySyncIndex + 1 < e.Args.Length && MainWindow is MainWindow emptySyncWindow)
            {
                var emptySyncDir = e.Args[emptySyncIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await emptySyncWindow.RunEmptySyncCheckAsync(emptySyncDir);
                    Shutdown();
                }));
            }

            // 关于 / 声明弹窗排版自检（仅 Debug）：<程序>.exe --about-check <目录>
            var aboutIndex = Array.IndexOf(e.Args, "--about-check");
            if (aboutIndex >= 0 && aboutIndex + 1 < e.Args.Length && MainWindow is MainWindow aboutWindow)
            {
                var aboutDir = e.Args[aboutIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await aboutWindow.RunAboutCheckAsync(aboutDir);
                    Shutdown();
                }));
            }

            // 设置页「关于」分组 + 更新弹窗自检（仅 Debug）：<程序>.exe --update-check <目录>
            var updateIndex = Array.IndexOf(e.Args, "--update-check");
            if (updateIndex >= 0 && updateIndex + 1 < e.Args.Length && MainWindow is MainWindow updateWindow)
            {
                var updateDir = e.Args[updateIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await updateWindow.RunUpdateCheckAsync(updateDir);
                    Shutdown();
                }));
            }

            // 设置页「快速搜索游戏名」+ 真点一次更新游戏名（仅 Debug）：<程序>.exe --quick-names-check <目录>
            var quickNamesIndex = Array.IndexOf(e.Args, "--quick-names-check");
            if (quickNamesIndex >= 0 && quickNamesIndex + 1 < e.Args.Length && MainWindow is MainWindow quickNamesWindow)
            {
                var quickNamesDir = e.Args[quickNamesIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await quickNamesWindow.RunQuickNamesCheckAsync(quickNamesDir);
                    Shutdown();
                }));
            }

            // 顶栏刷新按钮的悬停同步菜单自检（仅 Debug）：<程序>.exe --sync-menu-check <目录>
            var syncMenuIndex = Array.IndexOf(e.Args, "--sync-menu-check");
            if (syncMenuIndex >= 0 && syncMenuIndex + 1 < e.Args.Length && MainWindow is MainWindow syncMenuWindow)
            {
                var syncMenuDir = e.Args[syncMenuIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await syncMenuWindow.RunSyncMenuCheckAsync(syncMenuDir);
                    Shutdown();
                }));
            }

            // 拖动窗口时悬停菜单要跟着走（仅 Debug）：<程序>.exe --sync-menu-track-check <目录>
            var syncTrackIndex = Array.IndexOf(e.Args, "--sync-menu-track-check");
            if (syncTrackIndex >= 0 && syncTrackIndex + 1 < e.Args.Length && MainWindow is MainWindow syncTrackWindow)
            {
                var syncTrackDir = e.Args[syncTrackIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await syncTrackWindow.RunSyncMenuTrackCheckAsync(syncTrackDir);
                    Shutdown();
                }));
            }

            // DPI / 字体清晰度环境自检（仅 Debug）：<程序>.exe --dpi-check <目录>
            var dpiIndex = Array.IndexOf(e.Args, "--dpi-check");
            if (dpiIndex >= 0 && dpiIndex + 1 < e.Args.Length && MainWindow is MainWindow dpiWindow)
            {
                var dpiDir = e.Args[dpiIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await dpiWindow.RunDpiCheckAsync(dpiDir);
                    Shutdown();
                }));
            }

            // 同一块屏上 Display / Ideal 两种文本模式的对比拍照（仅 Debug）：<程序>.exe --text-mode-check <目录>
            var textModeIndex = Array.IndexOf(e.Args, "--text-mode-check");
            if (textModeIndex >= 0 && textModeIndex + 1 < e.Args.Length && MainWindow is MainWindow textModeWindow)
            {
                var textModeDir = e.Args[textModeIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await textModeWindow.RunTextModeCheckAsync(textModeDir);
                    Shutdown();
                }));
            }

            // 字重对比自检（仅 Debug）：<程序>.exe --weight-check <目录>
            var weightIndex = Array.IndexOf(e.Args, "--weight-check");
            if (weightIndex >= 0 && weightIndex + 1 < e.Args.Length && MainWindow is MainWindow weightWindow)
            {
                var weightDir = e.Args[weightIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await weightWindow.RunWeightCheckAsync(weightDir);
                    Shutdown();
                }));
            }

            // 搜索「本地优先」自检（仅 Debug）：<程序>.exe --local-search-check <目录>
            var localSearchIndex = Array.IndexOf(e.Args, "--local-search-check");
            if (localSearchIndex >= 0 && localSearchIndex + 1 < e.Args.Length && MainWindow is MainWindow localSearchWindow)
            {
                var localSearchDir = e.Args[localSearchIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await localSearchWindow.RunLocalSearchCheckAsync(localSearchDir);
                    Shutdown();
                }));
            }

            // 同步弹框「数据 + 中文名」两段交接自检（仅 Debug）：<程序>.exe --sync-combo-check <目录>
            var comboIndex = Array.IndexOf(e.Args, "--sync-combo-check");
            if (comboIndex >= 0 && comboIndex + 1 < e.Args.Length && MainWindow is MainWindow comboWindow)
            {
                var comboDir = e.Args[comboIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await comboWindow.RunSyncComboCheckAsync(comboDir);
                    Shutdown();
                }));
            }

            // 详情页开关动画 + 点遮罩关闭自检（仅 Debug）：<程序>.exe --detail-check <目录>
            var detailIndex = Array.IndexOf(e.Args, "--detail-check");
            if (detailIndex >= 0 && detailIndex + 1 < e.Args.Length && MainWindow is MainWindow detailWindow)
            {
                var detailDir = e.Args[detailIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await detailWindow.RunDetailCheckAsync(detailDir);
                    Shutdown();
                }));
            }

            // 字号自检（仅 Debug）：<程序>.exe --font-check <目录>
            var fontIndex = Array.IndexOf(e.Args, "--font-check");
            if (fontIndex >= 0 && fontIndex + 1 < e.Args.Length && MainWindow is MainWindow fontWindow)
            {
                var fontDir = e.Args[fontIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await fontWindow.RunFontCheckAsync(fontDir);
                    Shutdown();
                }));
            }

            // 「数据更新」设置卡片 + 同步弹框自检（仅 Debug）：<程序>.exe --sync-ui-check <目录>
            var syncUiIndex = Array.IndexOf(e.Args, "--sync-ui-check");
            if (syncUiIndex >= 0 && syncUiIndex + 1 < e.Args.Length && MainWindow is MainWindow syncUiWindow)
            {
                var syncUiDir = e.Args[syncUiIndex + 1];
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await syncUiWindow.RunSyncUiCheckAsync(syncUiDir);
                    Shutdown();
                }));
            }

            // 真跑一次「同步全部数据」再取消（仅 Debug）：<程序>.exe --sync-run-check <目录> [秒数]
            var syncRunIndex = Array.IndexOf(e.Args, "--sync-run-check");
            if (syncRunIndex >= 0 && syncRunIndex + 1 < e.Args.Length && MainWindow is MainWindow syncRunWindow)
            {
                var syncRunDir = e.Args[syncRunIndex + 1];
                var syncRunSeconds = syncRunIndex + 2 < e.Args.Length &&
                                     int.TryParse(e.Args[syncRunIndex + 2], out var parsed) ? parsed : 20;

                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await syncRunWindow.RunSyncRunCheckAsync(syncRunDir, syncRunSeconds);
                    Shutdown();
                }));
            }

            // 真跑一次「更新已有游戏数据」再取消（仅 Debug）：<程序>.exe --sync-existing-check <目录> [秒数]
            var syncExistingIndex = Array.IndexOf(e.Args, "--sync-existing-check");
            if (syncExistingIndex >= 0 && syncExistingIndex + 1 < e.Args.Length &&
                MainWindow is MainWindow syncExistingWindow)
            {
                var syncExistingDir = e.Args[syncExistingIndex + 1];
                var syncExistingSeconds = syncExistingIndex + 2 < e.Args.Length &&
                                          int.TryParse(e.Args[syncExistingIndex + 2], out var parsedExisting)
                    ? parsedExisting
                    : 12;

                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await syncExistingWindow.RunSyncExistingCheckAsync(syncExistingDir, syncExistingSeconds);
                    Shutdown();
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

        /// <summary>
        /// 抢单实例：抢到就是第一个（true）；抢不到说明已经有一个在跑——
        /// 通知它把窗口亮出来、弹个「已经启动」提示，然后返回 false 让本进程退出。
        /// </summary>
        private bool BecomeSingleInstance()
        {
            try
            {
                // 这里千万不能用 new Mutex(true, name, out isFirst) 来判断。
                // isFirst 只说明「内核对象是不是这次新建的」：只要还有任何一个进程开着这个名字的句柄
                // （包括被强杀后残留的幽灵进程），isFirst 就是 false —— 哪怕根本没有活着的实例，
                // 程序也会误判成「已经启动」，双击后闪一下就没了。
                // 正确做法是 WaitOne(0)：能拿到锁，才说明确实没有活着的实例。
                _instanceMutex = new Mutex(false, InstanceKey);

                bool acquired;
                try
                {
                    acquired = _instanceMutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    // 上一个实例是被强杀的，锁已经交到我们手上
                    acquired = true;
                }

                if (acquired)
                {
                    _ownsMutex = true;
                    ListenForSecondLaunch();
                    return true;
                }

                // 没拿到锁：确实有活着的实例，把锁句柄交出去，别误 Release 别人的锁
                _instanceMutex.Dispose();
                _instanceMutex = null;
                _ownsMutex = false;

                AskRunningInstanceToShow();
                try { new AlreadyRunningWindow().ShowDialog(); } catch { /* 提示框出问题也不能拦着退出 */ }
                Shutdown(0);
                return false;
            }
            catch (Exception ex)
            {
                // 单实例检测本身出问题（权限、名字被占等）时，绝不能把程序堵死：放行，允许正常启动
                LogCrash(ex);
                try { _instanceMutex?.Dispose(); } catch { }
                _instanceMutex = null;
                _ownsMutex = false;
                return true;
            }
        }

        /// <summary>第一个实例：挂个后台等待，第二个实例一 Set 事件就把主窗口叫回来。</summary>
        private void ListenForSecondLaunch()
        {
            try
            {
                _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
                _showWait = ThreadPool.RegisterWaitForSingleObject(
                    _showSignal,
                    (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (MainWindow is MainWindow main) main.ShowMainWindow();
                    })),
                    state: null,
                    millisecondsTimeOutInterval: Timeout.Infinite,
                    executeOnlyOnce: false);
            }
            catch
            {
                // 事件建不出来只是「不能再双击唤起」，不影响正常使用
            }
        }

        /// <summary>第二个实例：敲一下第一个实例的门（可能在托盘里藏着，让它自己出来）。</summary>
        private static void AskRunningInstanceToShow()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal))
                {
                    signal.Set();
                    signal.Dispose();
                }
            }
            catch
            {
                // 第一个实例还没来得及建事件：此时它窗口本来就是开着的，不用管
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _showWait?.Unregister(null);

            _showSignal?.Dispose();
            _showSignal = null;

            if (_instanceMutex is not null)
            {
                try
                {
                    if (_ownsMutex) _instanceMutex.ReleaseMutex();
                }
                catch
                {
                    // 不是本线程持有 / 已经释放过，都无所谓
                }

                _instanceMutex.Dispose();
                _instanceMutex = null;
                _ownsMutex = false;
            }

            base.OnExit(e);
        }
    }
}
