using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace DynamicIsland
{
    /// <summary>
    /// Interaction logic for App.xaml
    ///
    /// 全局崩溃日志：把三类未处理异常写到 %AppData%\DynamicIsland\error.log。
    /// 平时不存在，崩了才有；用户报"软件崩了"时直接读这个日志定位，不用复现。
    /// </summary>
    public partial class App : Application
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DynamicIsland");
        private static readonly string LogPath = Path.Combine(LogDir, "error.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // P6 持久化：先加载，再让 MainWindow 构造期间读到的就是文件中的值
            DisplaySettings.Load();

            // UI 线程未处理异常：默认会崩 WPF，标记 Handled=true 让软件继续跑（尽可能存活）
            DispatcherUnhandledException += (_, args) =>
            {
                LogException("Dispatcher", args.Exception);
                args.Handled = true;
            };

            // 非 UI 线程（含 finalizer）：标记不了 Handled，进程通常会终止——先把日志落盘再说
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                LogException("AppDomain", args.ExceptionObject as Exception
                    ?? new Exception(args.ExceptionObject?.ToString() ?? "<null>"));
            };

            // Task 未 await 抛的异常：标记 Observed 让 GC 不二次抛
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                LogException("Task", args.Exception);
                args.SetObserved();
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // P6 兜尾：debounce 没来得及触发时这里同步写一次（关进程 / 重启 / 资源管理器收尾都会走到）
            DisplaySettings.SaveNow();
            base.OnExit(e);
        }

        private static void LogException(string source, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                // 滚动一下：超过 1MB 就清空（避免无限增长）
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024)
                    File.WriteAllText(LogPath, $"[truncated at {DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n");

                var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex.GetType().FullName}: {ex.Message}\n"
                          + ex.StackTrace + "\n\n";
                File.AppendAllText(LogPath, entry);
            }
            catch
            {
                // 日志写失败就算了，别让日志本身把进程拖死
            }
        }
    }
}
