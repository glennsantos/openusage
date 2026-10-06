using System.Windows;
using System.Windows.Threading;
using OpenUsage.Core.Support;

namespace OpenUsage.Windows;

public partial class App : Application
{
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        Log.Info("OpenUsage for Windows starting");
        _tray = new TrayController();
        _tray.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        Log.Info("OpenUsage for Windows exiting");
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show("OpenUsage hit an unexpected error and needs to close.\n\nDetails were written to:\n" + Log.FilePath,
            "OpenUsage", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
