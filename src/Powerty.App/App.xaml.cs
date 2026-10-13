using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Powerty.App;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Last-resort safety net: WPF tears the process down on any unhandled UI
        // exception. Log it and keep running (the tray and hotkeys stay alive); also
        // make sure an escape in a background task cannot kill the app silently.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        DiagnosticsLog.Enabled = true;

        // A second instance would silently fail to register the global hotkeys
        // (they are exclusive), so refuse to start twice.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\Powerty.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "程序已在运行中。\n\n请在系统托盘（任务栏右下角，可能收在“^”里）找到图标：\n· 双击图标打开主窗口\n· 右键图标可开关模块或退出",
                "Powerty",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticsLog.Write("CRASH", $"unhandled ui exception: {e.Exception}");
        e.Handled = true;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        DiagnosticsLog.Write("CRASH", $"unobserved task exception: {e.Exception}");
        e.SetObserved();
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        DiagnosticsLog.Write("CRASH", $"unhandled exception: {e.ExceptionObject}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}
