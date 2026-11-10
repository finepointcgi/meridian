using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Meridian.Web.Integrations.Requirements;

/// <summary>A request body that can be sent again after a redirect or a login.</summary>
public sealed record ToolBody(string Text, string ContentType);

/// <summary>A buffered response.</summary>
public sealed record ToolResponse(HttpStatusCode Status, string Body, string? ETag, string FinalUrl, HttpResponseHeaders Headers);

/// <summary>
/// HTTP for one requirements-tool connection over the guarded integrations client (<see cref="OutboundGuard"/>):
/// a private cookie jar per instance (Jazz session cookies never leak to other connections), redirects followed here
/// so every Set-Cookie is seen, credentials sent only to the configured server's origin, an optional login step that
/// runs when the server asks for it, and error messages without secrets.
/// </summary>
public sealed class ToolHttp(HttpClient http, string baseUrl, string toolName)
{
    public const int MaxRedirects = 10;
    public const int MaxBodyChars = 32 * 1024 * 1024;

    private readonly Uri _origin = new(baseUrl);

    public CookieContainer Cookies { get; } = new();

    /// <summary>Adds credentials to a request that goes to the configured server's origin.</summary>
    public Action<HttpRequestMessage>? Authorize { get; set; }

    /// <summary>True when a response means "sign in first" (Jazz: <c>X-com-ibm-team-repository-web-auth-msg: authrequired</c>).</summary>
    public Func<ToolResponse, bool>? NeedsLogin { get; set; }

    /// <summary>Signs in (fills <see cref="Cookies"/>); called at most once per request.</summary>
    public Func<CancellationToken, Task>? LoginAsync { get; set; }

    public bool SameOrigin(Uri u) => u.Scheme == _origin.Scheme && string.Equals(u.Host, _origin.Host, StringComparison.OrdinalIgnoreCase) && u.Port == _origin.Port;

    /// <summary>Sends a request, following redirects and signing in when needed. Throws on non-success unless <paramref name="allowStatus"/> accepts it.</summary>
    public async Task<ToolResponse> SendAsync(HttpMethod method, string url, string accept, ToolBody? body = null, IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default, Func<HttpStatusCode, bool>? allowStatus = null, bool login = true)
    {
        var response = await SendRawAsync(method, url, accept, body, headers, ct);
        if (login && LoginAsync != null && NeedsLogin?.Invoke(response) == true)
        {
            await LoginAsync(ct);
            response = await SendRawAsync(method, url, accept, body, headers, ct);
            if (NeedsLogin(response)) throw new RequirementsToolException($"{toolName} did not accept the sign-in — check the username and password.", HttpStatusCode.Unauthorized);
        }
        if ((int)response.Status is >= 200 and < 300 || allowStatus?.Invoke(response.Status) == true) return response;
        var hint = response.Status switch
        {
            HttpStatusCode.Unauthorized => "the credentials were rejected",
            HttpStatusCode.Forbidden => "the account lacks permission",
            HttpStatusCode.NotFound => "not found — check the URL",
            HttpStatusCode.PreconditionFailed => "it was changed by someone else meanwhile",
            _ => Summarize(response.Body),
        };
        throw new RequirementsToolException($"{toolName}: {(int)response.Status} {response.Status}: {hint}", response.Status);
    }

    /// <summary>One request and its redirects, without the login step.</summary>
    public async Task<ToolResponse> SendRawAsync(HttpMethod method, string url, string accept, ToolBody? body = null, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new RequirementsToolException($"{toolName} returned an invalid URL.");
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Accept.ParseAdd(accept);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Meridian", "1.0"));
            if (headers != null) foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
            if (SameOrigin(uri)) Authorize?.Invoke(request);   // credentials never follow a redirect to another origin
            var cookie = Cookies.GetCookieHeader(uri);
            if (cookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookie);
            if (body != null)
            {
                request.Content = new StringContent(body.Text, Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(body.ContentType);
            }
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (HttpRequestException ex) { throw new RequirementsToolException($"Could not reach {uri.Host}: {ex.Message}"); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new RequirementsToolException($"{uri.Host} did not answer in time."); }
            using (response)
            {
                if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                    foreach (var c in setCookies)
                    {
                        try { Cookies.SetCookies(uri, c); }
                        catch (CookieException) { }   // a malformed cookie is ignored, as a browser would
                    }
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects) throw new RequirementsToolException($"{toolName} redirected too many times.");
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (next.Scheme != Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttp) throw new RequirementsToolException($"{toolName} redirected to an unsupported address.");
                    if (status is 301 or 302 or 303 && method != HttpMethod.Head) { method = HttpMethod.Get; body = null; }
                    uri = next;
                    continue;
                }
                var text = await ReadLimitedAsync(response, ct);
                return new ToolResponse(response.StatusCode, text, response.Headers.ETag?.ToString(), uri.ToString(), response.Headers);
            }
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var charset = response.Content.Headers.ContentType?.CharSet;
        Encoding enc;
        try { enc = charset is { Length: > 0 } ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8; }
        catch (ArgumentException) { enc = Encoding.UTF8; }
        using var reader = new StreamReader(stream, enc);
        var buffer = new char[81920];
        var sb = new StringBuilder();
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (sb.Length + n > MaxBodyChars) throw new RequirementsToolException("The response is too large.");
            sb.Append(buffer, 0, n);
        }
        return sb.ToString();
    }

    public static string Summarize(string text)
    {
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    public static string BasicAuth(string user, string secret) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}"));
}
