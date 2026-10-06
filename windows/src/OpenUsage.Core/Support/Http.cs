using System.Net;
using OpenUsage.Core.Models;

namespace OpenUsage.Core.Support;

public sealed record HttpResult(HttpStatusCode Status, string Body, System.Net.Http.Headers.HttpResponseHeaders Headers)
{
    public int Code => (int)Status;
    public bool IsSuccess => Code is >= 200 and < 300;
    public bool IsAuthFailure => Code is 401 or 403;
}

public static class Http
{
    public const string ConnectionFailed = "Usage request failed. Check your connection.";
    public const string InvalidResponse = "Usage response invalid. Try again later.";
    public static string RequestFailed(int status) => $"Usage request failed (HTTP {status}). Try again later.";

    public static async Task<HttpResult> SendAsync(HttpClient client, HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return new HttpResult(response.StatusCode, body, response.Headers);
    }

    /// Runs `attempt`; on 401/403 refreshes once and retries once. Mirrors Swift `ProviderAuthRetry.fetch`.
    /// `refresh` returns the new access token, or null when no refresh is possible.
    public static async Task<HttpResult> WithAuthRetryAsync(
        string accessToken,
        Func<string, Task<HttpResult>> attempt,
        Func<Task<string?>> refresh,
        string tokenExpiredMessage)
    {
        HttpResult first;
        try { first = await attempt(accessToken).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new ProviderException(ConnectionFailed);
        }
        if (!first.IsAuthFailure) return first;

        var refreshed = await refresh().ConfigureAwait(false)
            ?? throw new ProviderException(tokenExpiredMessage, allowsAuthFallback: true);
        HttpResult second;
        try { second = await attempt(refreshed).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new ProviderException("Usage request failed after refresh. Try again.");
        }
        if (second.IsAuthFailure) throw new ProviderException(tokenExpiredMessage, allowsAuthFallback: true);
        return second;
    }

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }
}
