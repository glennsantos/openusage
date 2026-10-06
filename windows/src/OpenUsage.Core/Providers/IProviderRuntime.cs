using OpenUsage.Core.Models;

namespace OpenUsage.Core.Providers;

/// Windows counterpart of the Swift `ProviderRuntime` protocol.
public interface IProviderRuntime
{
    ProviderInfo Provider { get; }
    IReadOnlyList<WidgetDescriptor> Widgets { get; }

    /// Loads credentials, calls the provider API and maps the response. Never throws; failures become an error snapshot.
    Task<ProviderSnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    /// Local-only credential probe: no network, no prompts.
    bool HasLocalCredentials();
}
