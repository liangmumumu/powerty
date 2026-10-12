using System.Threading;
using System.Windows;

namespace Powerty.App;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
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

        DiagnosticsLog.Enabled = true;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}
