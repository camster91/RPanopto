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
/// <param name="connectionDeadline">
/// How long one connection gets to say something at all. A connection that
/// sends no request line is closed and the wait continues, so one silent
/// local connection cannot park the whole authorization window.
/// </param>
public sealed class LoopbackListener(int port, string expectedPath, TimeSpan? connectionDeadline = null)
    : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, port);
    private readonly TimeSpan _connectionDeadline = connectionDeadline ?? DefaultConnectionDeadline;
    private bool _started;

    /// <summary>How long one connection gets to say something at all.</summary>
    private static readonly TimeSpan DefaultConnectionDeadline = TimeSpan.FromSeconds(10);

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
    /// <para>Browsers often request <c>/favicon.ico</c> alongside the callback,
    /// and any page open on this machine can issue a GET to a loopback port, so
    /// requests are looped over until the one that is genuinely this sign-in's
    /// redirect arrives: right path, and a <paramref name="expectedState"/>
    /// matching the state this sign-in issued.</para>
    /// </summary>
    /// <param name="timeout">
    /// How long the whole wait may take — the user's authorization window.
    /// </param>
    /// <param name="expectedState">
    /// The state this sign-in put in the authorize URL. A request to the
    /// callback path carrying a different state — a stray GET from another
    /// page — is answered like any other stray request and the wait
    /// continues, instead of ending the attempt with an error that reads as
    /// a forged callback.
    /// </param>
    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(
        TimeSpan timeout,
        string expectedState,
        CancellationToken ct = default)
    {
        if (!_started) throw new InvalidOperationException("Start() must be called first.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(timeoutCts.Token)
                .ConfigureAwait(false);

            // A per-connection deadline, not the caller's whole authorization
            // window. Any local process can connect to a loopback port — a
            // browser preconnect, a port scanner — and one that connects and
            // then sends no request line would otherwise park this loop until
            // the window expired: the browser's real callback would never be
            // accepted, and the sign-in would fail as a timeout with the tab
            // still spinning. The deadline closes the silent connection and
            // goes back to waiting.
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
            connectionCts.CancelAfter(_connectionDeadline);

            IReadOnlyDictionary<string, string>? query;
            try
            {
                var requestLine = await ReadRequestLineAsync(client, connectionCts.Token)
                    .ConfigureAwait(false);

                query = ParseQuery(requestLine, expectedState);

                if (query is null)
                {
                    // Not our redirect — favicon, probe, or a stray request to the
                    // callback path. Answer cheaply and keep waiting: ending the
                    // wait here would abort a sign-in that was about to succeed.
                    await WriteResponseAsync(client, connectionCts.Token, "Waiting for authorization…", 404)
                        .ConfigureAwait(false);
                    continue;
                }
            }
            catch (Exception ex) when (IsOneConnectionsFault(ex, ct, timeoutCts.Token))
            {
                // One connection went wrong — it was silent past its deadline, or
                // it reset before its request line was read or its 404 written.
                // The 404 used to sit outside this guard, and the read caught
                // only the deadline, so a browser preconnect that hung up early
                // or a scanner that resets every socket it opens ended the whole
                // sign-in with a socket error; and a deadline that fired during
                // the write escaped as a bare cancellation, which the
                // authenticator reports as the sign-in window expiring when it
                // had minutes left. The `using` closes this one; the next
                // connection may be the redirect.
                continue;
            }

            try
            {
                await WriteResponseAsync(client, connectionCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsOneConnectionsFault(ex, ct, timeoutCts.Token))
            {
                // The code is already in hand. A browser that closed the tab the
                // instant it had sent the redirect misses the "you can close this
                // tab" page, which costs nothing; throwing here would throw away a
                // sign-in that has, in every way that matters, succeeded.
            }

            return query;
        }
    }

    /// <summary>
    /// Whether <paramref name="error"/> belongs to the one connection being served,
    /// rather than to the wait as a whole.
    ///
    /// <para>A socket error is always the connection's own. A cancellation is the
    /// connection's only when neither the caller nor the authorization window has
    /// been cancelled — those two must still end the wait, and do: they propagate
    /// from here, or from the next accept if a socket error raced them.</para>
    /// </summary>
    private static bool IsOneConnectionsFault(Exception error, CancellationToken ct, CancellationToken window)
        => error switch
        {
            IOException or SocketException => true,
            OperationCanceledException => !ct.IsCancellationRequested && !window.IsCancellationRequested,
            _ => false,
        };

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

    /// <summary>
    /// Parses this sign-in's redirect out of a request line, or returns null
    /// when the request is not the redirect.
    ///
    /// <para>Null, never an empty dictionary, is what "not ours" means. The
    /// earlier version returned an empty dictionary for a request to the
    /// callback path with no query string, and the accept loop treated any
    /// non-null result as the redirect — so any page open on this machine
    /// could end a pending sign-in with one query-less GET to a port that is
    /// documented in AV-ALLOWLIST.md, leaving the user with an "OAuth state
    /// mismatch" that described an attack rather than a stray request.</para>
    ///
    /// <para>What counts as the redirect: the right path, and a
    /// <c>state</c> matching the one this sign-in issued. A redirect always
    /// carries its state back, and the match is the forgery guard — so a
    /// request to the right path without one, or with a state that is not
    /// ours, is not the redirect.</para>
    /// </summary>
    private IReadOnlyDictionary<string, string>? ParseQuery(string requestLine, string expectedState)
    {
        // "GET /oauth/callback?code=...&state=... HTTP/1.1"
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return null;

        var target = parts[1];
        var separator = target.IndexOf('?');
        var path = separator >= 0 ? target[..separator] : target;

        if (!string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase))
            return null;

        // No query string at all: not the redirect, and answering it as one
        // is the failure described above.
        if (separator < 0 || separator == target.Length - 1) return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in target[(separator + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        if (!result.TryGetValue("state", out var state) ||
            !string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            return null;
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
