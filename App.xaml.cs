using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ForgeFlow
{
    public partial class App : Application
    {
        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        private Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            Resources[typeof(System.Windows.Controls.ComboBox)] = DarkComboBoxStyles.CreateComboBoxStyle();
            Resources[typeof(System.Windows.Controls.ComboBoxItem)] = DarkComboBoxStyles.CreateComboBoxItemStyle();
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            _singleInstanceMutex = new Mutex(true, @"Local\ForgeFlowLiteSingleInstance", out bool createdNew);
            if (!createdNew)
            {
                if (!TryActivateExistingInstance())
                {
                    MessageBox.Show(
                        "ForgeFlow يعمل بالفعل. تم تشغيل النسخة المفتوحة حالياً.",
                        "ForgeFlow",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

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

        private static bool TryActivateExistingInstance()
        {
            try
            {
                int currentId = Environment.ProcessId;
                string? currentPath = Environment.ProcessPath;

                foreach (Process process in Process.GetProcessesByName("ForgeFlow"))
                {
                    using (process)
                    {
                        if (process.Id == currentId)
                            continue;

                        string? processPath = null;
                        try { processPath = process.MainModule?.FileName; } catch { }

                        if (!string.IsNullOrWhiteSpace(currentPath) &&
                            !string.IsNullOrWhiteSpace(processPath) &&
                            !string.Equals(currentPath, processPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        nint handle = WaitForMainWindow(process);
                        if (handle == nint.Zero)
                            continue;

                        if (IsIconic(handle))
                            ShowWindowAsync(handle, SW_RESTORE);
                        else
                            ShowWindowAsync(handle, SW_SHOW);

                        SetForegroundWindow(handle);
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static nint WaitForMainWindow(Process process)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    process.Refresh();
                    if (process.MainWindowHandle != nint.Zero)
                        return process.MainWindowHandle;
                }
                catch
                {
                    return nint.Zero;
                }

                Thread.Sleep(100);
            }

            return nint.Zero;
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

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(nint hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(nint hWnd);
    }
}
