using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PanoptoScheduler.Core.Auth;

/// <summary>
/// Single-use loopback HTTP listener that catches the OAuth redirect.
///
/// <para>Built on <see cref="TcpListener"/> rather than
/// <see cref="HttpListener"/> deliberately. <c>HttpListener</c> requires a URL
/// ACL registered with <c>netsh</c>, which needs administrator rights — so on a
/// standard locked-down workstation it fails with "Access is denied". A raw
/// socket listener binds 127.0.0.1 without elevation, which is what lets the
/// app work for every member of the team rather than only admins.</para>
///
/// Bound to <see cref="IPAddress.Loopback"/> so the callback is never reachable
/// from the network.
/// </summary>
public sealed class LoopbackListener(int port, string expectedPath) : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, port);
    private bool _started;

    /// <summary>
    /// Binds the callback port.
    ///
    /// <para>A bind failure is translated rather than passed through. The raw
    /// <see cref="SocketException"/> reads as "Only one usage of each socket
    /// address is normally permitted" in eleven-point grey text, which tells the
    /// person holding the laptop nothing about what to do — and the overwhelmingly
    /// likely cause is the one they can fix in five seconds: they already have the
    /// app open, and are looking at the first copy's window.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The port could not be bound, with the reason stated in terms a person can
    /// act on.
    /// </exception>
    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Port {port} is already in use, so the browser has nowhere to hand the " +
                "sign-in back to. Another copy of Panopto Scheduler is most likely already " +
                "open — switch to it and use that one. If it is not, something else on this " +
                $"machine is holding port {port} and will need to be closed.",
                ex);
        }

        _started = true;
    }

    /// <summary>
    /// Waits for the redirect and returns its query parameters.
    ///
    /// Browsers often request <c>/favicon.ico</c> alongside the callback, so
    /// requests are looped over until one arrives on the expected path.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (!_started) throw new InvalidOperationException("Start() must be called first.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(timeoutCts.Token)
                .ConfigureAwait(false);

            var requestLine = await ReadRequestLineAsync(client, timeoutCts.Token)
                .ConfigureAwait(false);

            var query = ParseQuery(requestLine);

            if (query is not null)
            {
                await WriteResponseAsync(client, timeoutCts.Token).ConfigureAwait(false);
                return query;
            }

            // Not our path (favicon, probe) — answer cheaply and keep waiting.
            await WriteResponseAsync(client, timeoutCts.Token, "Waiting for authorization…", 404)
                .ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadRequestLineAsync(TcpClient client, CancellationToken ct)
    {
        // The stream must outlive this method: the caller still has to write the
        // response back over it. Disposing it here closes the underlying socket,
        // and the later write then fails with "The operation is not allowed on
        // non-connected sockets". Only the reader is disposed.
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

        // The request line is the first line; headers are not needed.
        return await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? string.Empty;
    }

    private IReadOnlyDictionary<string, string>? ParseQuery(string requestLine)
    {
        // "GET /oauth/callback?code=...&state=... HTTP/1.1"
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return null;

        var target = parts[1];
        var separator = target.IndexOf('?');
        var path = separator >= 0 ? target[..separator] : target;

        if (!string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase))
            return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (separator < 0 || separator == target.Length - 1) return result;

        foreach (var pair in target[(separator + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return result;
    }

    private static async Task WriteResponseAsync(
        TcpClient client,
        CancellationToken ct,
        string message = "Authorization complete. You can close this tab and return to Panopto Scheduler.",
        int status = 200)
    {
        var body = $"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>Panopto Scheduler</title></head>
            <body style="font-family:system-ui;padding:3rem;text-align:center">
            <h2>{WebUtility.HtmlEncode(message)}</h2>
            </body></html>
            """;

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var reason = status == 200 ? "OK" : "Not Found";

        var header = $"HTTP/1.1 {status} {reason}\r\n"
                   + "Content-Type: text/html; charset=utf-8\r\n"
                   + $"Content-Length: {bodyBytes.Length}\r\n"
                   + "Connection: close\r\n\r\n";

        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (_started) _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
