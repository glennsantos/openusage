using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using OpenUsage.Core.Support;

namespace OpenUsage.Windows;

/// Borderless panel that opens above the tray icon, like a taskbar flyout. Hides when it loses focus.
public partial class FlyoutWindow : Window
{
    private const double ScreenMargin = 12;
    private readonly DashboardViewModel _model;
    private readonly Action _quit;

    public FlyoutWindow(DashboardViewModel model, Action quit)
    {
        InitializeComponent();
        _model = model;
        _quit = quit;
        DataContext = model;
        SizeChanged += (_, _) => PlaceNearTray();
    }

    /// Time the panel last hid, so a tray click that caused the hide doesn't immediately reopen it.
    public DateTime LastHidden { get; private set; }

    public void ShowFlyout()
    {
        Show();
        PlaceNearTray();
        Activate();
    }

    /// Bottom-right of the work area, which sits just above the taskbar in its default position.
    private void PlaceNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - ScreenMargin;
        Top = area.Bottom - ActualHeight - ScreenMargin;
    }

    private void HideFlyout()
    {
        Hide();
        LastHidden = DateTime.UtcNow;
    }

    private void OnDeactivated(object? sender, EventArgs e) => HideFlyout();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) HideFlyout();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await _model.RefreshAsync();

    private void OnLogClick(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(Log.FilePath)) Log.Info("Log opened from panel");
        Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    private void OnQuitClick(object sender, RoutedEventArgs e) => _quit();
}
