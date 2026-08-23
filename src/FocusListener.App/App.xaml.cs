using System.Windows;
using System.Windows.Threading;

namespace FocusListener.App;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstance;
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.TryAcquire())
        {
            SingleInstanceCoordinator.ActivateExistingWindow();
            Shutdown();
            return;
        }

        ProductRuntime.Initialize();
        var settings = new FocusInteractionSettingsStore(ProductRuntime.SettingsPath).Load();
        ProductText.Use(settings.AppLanguage);
        if (SystemParameters.HighContrast)
        {
            Resources["InkBrush"] = SystemColors.WindowTextBrush;
            Resources["MutedBrush"] = SystemColors.GrayTextBrush;
            Resources["AccentBrush"] = SystemColors.HighlightBrush;
            Resources["AccentSoftBrush"] = SystemColors.ControlBrush;
        }
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is MainWindow window)
            {
                _tray = new TrayController(window);
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ProductRuntime.Log("ApplicationExited");
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ProductRuntime.RecordCrash("DispatcherUnhandledException", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            ProductText.Choose(
                "Focus Listener 遇到意外错误并将安全退出。下次启动可在“帮助与关于”中导出不含课堂内容的诊断包。",
                "Focus Listener encountered an unexpected error and will close safely. On the next launch, use Help & About to export a diagnostic bundle without classroom content."),
            "Focus Listener",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(1);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ProductRuntime.RecordCrash("AppDomainUnhandledException", exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ProductRuntime.Log("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }
}
