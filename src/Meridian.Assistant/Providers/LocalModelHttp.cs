using System.Collections.Concurrent;

namespace Meridian.Assistant.Providers;

/// <summary>
/// Refuses every request whose scheme, host or port differs from the configured model server, so nothing (a redirect,
/// a crafted model id, a bug) can make the assistant reach another address. The configured host itself may be loopback
/// or private: it was chosen explicitly by the administrator (server) or the user on their own computer (desktop).
/// </summary>
public sealed class AllowedHostHandler : DelegatingHandler
{
    private readonly Uri _allowed;

    public AllowedHostHandler(Uri allowed, HttpMessageHandler inner) : base(inner) => _allowed = allowed;

    public static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        a.Port == b.Port;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } uri || !SameOrigin(uri, _allowed))
            throw new ChatProviderException("Blocked a request to an address other than the configured model server.");
        return base.SendAsync(request, ct);
    }
}

/// <summary>Creates the HTTP clients that talk to a local model server (tests pass a fake inner handler).</summary>
public interface ILocalModelHttpFactory
{
    HttpClient Create(Uri baseUrl);
}

public sealed class LocalModelHttpFactory(HttpMessageHandler? inner = null) : ILocalModelHttpFactory
{
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    public HttpClient Create(Uri baseUrl)
    {
        var origin = baseUrl.GetLeftPart(UriPartial.Authority);
        if (inner != null)   // tests: a fresh client around the shared fake handler
            return new HttpClient(new AllowedHostHandler(baseUrl, inner), disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        return _clients.GetOrAdd(origin, _ => new HttpClient(new AllowedHostHandler(baseUrl, new SocketsHttpHandler
        {
            AllowAutoRedirect = false,               // a redirect could point anywhere
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseProxy = false,                        // local servers are reached directly
        }))
        { Timeout = Timeout.InfiniteTimeSpan });     // per-request deadlines are applied by the provider (local models are slow)
    }
}
