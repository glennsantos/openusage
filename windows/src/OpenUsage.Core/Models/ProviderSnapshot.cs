namespace OpenUsage.Core.Models;

public sealed record ProviderInfo(string Id, string DisplayName, string StatusUrl, string DashboardUrl);

/// One widget a provider can show. `Id` is "<providerId>.<suffix>" and matches the metric line it maps.
public sealed record WidgetDescriptor(string Id, string LineLabel, bool DefaultEnabled, bool DefaultPinned);

public sealed record ProviderSnapshot(
    ProviderInfo Provider,
    string? Plan,
    IReadOnlyList<MetricLine> Lines,
    DateTimeOffset RefreshedAt,
    string? Warning = null,
    string? Error = null)
{
    public static ProviderSnapshot Failed(ProviderInfo provider, string message) =>
        new(provider, null, Array.Empty<MetricLine>(), DateTimeOffset.Now, Error: message);
}

/// A user-facing provider failure. The message is shown verbatim in the panel.
public sealed class ProviderException : Exception
{
    public ProviderException(string message, bool allowsAuthFallback = false) : base(message)
    {
        AllowsAuthFallback = allowsAuthFallback;
    }

    /// True for auth-expiry errors where the next credential candidate should be tried.
    public bool AllowsAuthFallback { get; }
}
