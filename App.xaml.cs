using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ForgeFlow
{
    public partial class App : Application
    {
        private Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            _singleInstanceMutex = new Mutex(true, @"Local\ForgeFlowLiteSingleInstance", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "ForgeFlow يعمل بالفعل. أغلق النافذة المفتوحة أولاً.",
                    "ForgeFlow",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
                _singleInstanceMutex?.Dispose();
            }
            catch
            {
            }

            base.OnExit(e);
        }

        private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash(e.Exception);
            MessageBox.Show(
                "حدث خطأ غير متوقع وتم حفظ التفاصيل في سجل الأعطال.\n\n" + e.Exception.Message,
                "ForgeFlow Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                LogCrash(ex);
        }

        private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogCrash(e.Exception);
            e.SetObserved();
        }

        private static void LogCrash(Exception exception)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ForgeFlow",
                    "Logs");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "crash.log");
                File.AppendAllText(
                    path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n" +
                    $"Version: {Assembly.GetExecutingAssembly().GetName().Version}\n" +
                    $"OS: {Environment.OSVersion}\n" +
                    $"Machine: {Environment.MachineName}\n" +
                    $"User: {Environment.UserName}\n\n" +
                    $"{exception}\n\n");
            }
            catch
            {
            }
        }
    }
}
