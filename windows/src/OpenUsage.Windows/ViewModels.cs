using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenUsage.Core.Models;
using OpenUsage.Core.Presentation;
using OpenUsage.Core.Providers;

namespace OpenUsage.Windows;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed record MetricRow(string Label, string Value, string? Subtitle, double? Fraction, string? AccentHex)
{
    public bool HasBar => Fraction != null;
    public bool HasSubtitle => Subtitle != null;

    public static MetricRow From(MetricLine line, DateTimeOffset now) => line switch
    {
        MetricLine.Progress p => new MetricRow(p.Label, MetricFormatter.ProgressValue(p),
            MetricFormatter.ResetsIn(p.ResetsAt, now), MetricFormatter.Fraction(p), null),
        MetricLine.Values v => new MetricRow(v.Label, MetricFormatter.Values(v),
            v.ExpiriesAt is { Count: > 0 } e ? $"Next expires {e[0].ToLocalTime():MMM d}" : null, null, null),
        MetricLine.Badge b => new MetricRow(b.Label, b.BadgeText, null, null, b.ColorHex),
        MetricLine.Text t => new MetricRow(t.Label, t.Value, null, null, t.ColorHex),
        _ => throw new ArgumentOutOfRangeException(nameof(line)),
    };
}

public sealed class ProviderCard : Observable
{
    private string? _plan;
    private string? _error;
    private string? _warning;
    private string _status = "Loading…";
    private IReadOnlyList<MetricRow> _rows = Array.Empty<MetricRow>();

    public ProviderCard(IProviderRuntime runtime) => Runtime = runtime;

    public IProviderRuntime Runtime { get; }
    public string Name => Runtime.Provider.DisplayName;
    public string? Plan { get => _plan; private set => Set(ref _plan, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public string? Warning { get => _warning; private set => Set(ref _warning, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public IReadOnlyList<MetricRow> Rows { get => _rows; private set => Set(ref _rows, value); }

    /// Lines for pinned widgets, used by the tray tooltip.
    public IReadOnlyList<MetricLine> PinnedLines { get; private set; } = Array.Empty<MetricLine>();

    public void Apply(ProviderSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;
        var widgets = Runtime.Widgets.ToDictionary(w => w.LineLabel);
        // Lines without a widget (status badges, notes) always show; widget lines follow the default layout.
        var visible = snapshot.Lines.Where(l => !widgets.TryGetValue(l.Label, out var w) || w.DefaultEnabled).ToList();

        Plan = snapshot.Plan;
        Error = snapshot.Error;
        Warning = snapshot.Warning;
        Rows = visible.Select(l => MetricRow.From(l, now)).ToList();
        PinnedLines = visible.Where(l => widgets.TryGetValue(l.Label, out var w) && w.DefaultPinned).ToList();
        Status = $"Updated {snapshot.RefreshedAt.ToLocalTime():t}";
    }
}

public sealed class DashboardViewModel : Observable
{
    private bool _isRefreshing;

    public DashboardViewModel(IEnumerable<IProviderRuntime> providers)
    {
        Cards = new ObservableCollection<ProviderCard>(providers.Select(p => new ProviderCard(p)));
    }

    public ObservableCollection<ProviderCard> Cards { get; }
    public bool IsRefreshing { get => _isRefreshing; private set => Set(ref _isRefreshing, value); }

    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            var snapshots = await Task.WhenAll(Cards.Select(c => c.Runtime.RefreshAsync()));
            for (var i = 0; i < Cards.Count; i++) Cards[i].Apply(snapshots[i]);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// e.g. "Claude 42% · 18%  |  Codex 7% · 30%". NotifyIcon caps tooltips at 127 characters.
    public string TrayTooltip()
    {
        var parts = Cards
            .Where(c => c.PinnedLines.Count > 0)
            .Select(c => $"{c.Name} {string.Join(" · ", c.PinnedLines.Select(MetricFormatter.Compact))}");
        var text = string.Join("\n", parts);
        if (text.Length == 0) text = "OpenUsage";
        return text.Length > 127 ? text[..126] + "…" : text;
    }
}
