using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace MagDbPatcher;

public partial class App : Application
{
    // amirul support 14, 2026
    private const string _buildSignature = "amirul support 14, 2026";

    private static readonly string ErrorLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MagDbPatcher",
        "startup-errors.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterGlobalExceptionHandlers();

        try
        {
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            LogException("OnStartup", ex);
            MessageBox.Show(
                "The app failed to start.\n\n" +
                $"Details were saved to:\n{ErrorLogPath}\n\n" +
                $"Error: {ex.Message}",
                "Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            "A fatal UI error occurred.\n\n" +
            $"Details were saved to:\n{ErrorLogPath}\n\n" +
            $"Error: {e.Exception.Message}",
            "Application Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception.");
        LogException("AppDomainUnhandledException", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void LogException(string source, Exception ex)
    {
        try
        {
            var directory = Path.GetDirectoryName(ErrorLogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new StringBuilder();
            builder.AppendLine("==================================================");
            builder.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"Source: {source}");
            builder.AppendLine($"Message: {ex.Message}");
            builder.AppendLine("StackTrace:");
            builder.AppendLine(ex.ToString());

            File.AppendAllText(ErrorLogPath, builder.ToString());
        }
        catch
        {
            // Avoid secondary failures while reporting crashes.
        }
    }
}
