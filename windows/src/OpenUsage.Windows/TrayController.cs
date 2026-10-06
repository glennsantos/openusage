using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Providers.Claude;
using OpenUsage.Core.Providers.Codex;
using OpenUsage.Core.Providers.Cursor;
using OpenUsage.Core.Support;
using Forms = System.Windows.Forms;

namespace OpenUsage.Windows;

/// Owns the notification-area icon, the flyout panel, and the auto-refresh timer.
public sealed class TrayController : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http = new();
    private readonly Forms.NotifyIcon _icon;
    private readonly DispatcherTimer _timer;
    private readonly DashboardViewModel _model;
    private readonly FlyoutWindow _flyout;

    public TrayController(ThemeManager theme)
    {
        _model = new DashboardViewModel(DetectProviders(_http));
        _flyout = new FlyoutWindow(_model, theme, Quit);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => _flyout.ShowFlyout());
        menu.Items.Add("Refresh", null, async (_, _) => await RefreshAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize),
            Text = "OpenUsage",
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += OnIconClick;

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void Start()
    {
        _icon.Visible = true;
        _timer.Start();
        _ = RefreshAsync();
    }

    /// Claude, Codex, Cursor in the default order. Shows only providers with a local login; if none has one,
    /// shows all three so the panel explains how to sign in.
    private static IReadOnlyList<IProviderRuntime> DetectProviders(HttpClient http)
    {
        var all = new IProviderRuntime[] { new ClaudeProvider(http), new CodexProvider(http), new CursorProvider(http) };
        var detected = all.Where(p => p.HasLocalCredentials()).ToList();
        Log.Info($"Detected providers: {(detected.Count == 0 ? "none" : string.Join(", ", detected.Select(p => p.Provider.Id)))}");
        return detected.Count > 0 ? detected : all;
    }

    private async Task RefreshAsync()
    {
        await _model.RefreshAsync();
        _icon.Text = _model.TrayTooltip();
    }

    private void OnIconClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left) return;
        // Clicking the icon while the panel is open deactivates (hides) it first; don't reopen on that same click.
        if (_flyout.IsVisible || DateTime.UtcNow - _flyout.LastHidden < TimeSpan.FromMilliseconds(300)) return;
        _flyout.ShowFlyout();
    }

    private void Quit() => Application.Current.Shutdown();

    public void Dispose()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _http.Dispose();
    }
}
