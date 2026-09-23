using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using PanoptoScheduler.Core.Auth;
using PanoptoScheduler.Core.Clients;

namespace PanoptoScheduler.Core.Diagnostics;

/// <summary>
/// Turns an exception into the one sentence a person can act on, and keeps the
/// rest of it for the log.
///
/// <para><b>Why this exists.</b> Six places in the app hand an exception's
/// <c>Message</c> straight to the status line, and for the failures that actually
/// happen that message is written for whoever wrote the client, not for whoever
/// is running a recording desk. A SOAP fault arrives as an XML body with a
/// namespace prefix; a dropped connection arrives as "An error occurred while
/// sending the request." Neither says whether to try again, sign in, or call
/// someone.</para>
///
/// <para><b>Nothing is swallowed.</b> Every caller still logs
/// <see cref="AppLog.Full"/>, which is the whole chain including stack traces,
/// and the app puts that on the tooltip of the line this returns. The sentence is
/// the summary, not the record — losing the detail is what makes an error
/// message a dead end, and that is the failure this type is written to
/// avoid.</para>
/// </summary>
public static class ProblemText
{
    /// <summary>
    /// One line, in plain terms, saying what went wrong and what to do about it.
    /// Never contains a stack trace, a type name, or an XML fragment.
    /// </summary>
    public static string Summarise(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var chain = Chain(error);

        // Ordered by how specific the answer is, not by where it sits in the
        // chain. A failed request usually arrives as two or three nested
        // exceptions, and the useful one is not always the outermost — that is
        // what "one or more errors occurred" is.
        if (First<PanoptoSoapFaultException>(chain) is { } fault)
        {
            // The fault's own sentence is Panopto's, and it is written for a
            // person — "The remote recorder is already in use at this time" —
            // so it is worth passing through, unlike most of what arrives
            // here. RawMessage, deliberately: fault.Message already begins
            // "Panopto rejected <operation>", and interpolating it here would
            // say "Panopto rejected the change: Panopto rejected
            // ScheduleRecording: …" — a doubled prefix plus an internal
            // operation name, which the contract above forbids.
            return $"Panopto rejected the change: {Clean(fault.RawMessage)}";
        }

        if (First<PanoptoRequestException>(chain) is { } request)
        {
            return request.Status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "Panopto refused the request. The sign-in has probably expired — "
                    + "sign in again and retry.",

                HttpStatusCode.NotFound =>
                    "Panopto no longer has that session or folder. It may have been "
                    + "deleted or moved by someone else — refresh to see the current state.",

                >= HttpStatusCode.InternalServerError =>
                    $"Panopto reported a problem at its end (HTTP {(int)request.Status}). "
                    + "This is usually temporary — trying again in a moment normally works.",

                _ =>
                    $"Panopto would not accept the request (HTTP {(int)request.Status} "
                    + $"from {Clean(request.Endpoint)}).",
            };
        }

        if (First<SignInWindowExpiredException>(chain) is { } expired)
        {
            // The one timeout that is not Panopto's: nothing had been sent
            // anywhere when this window ran out. The browser step was opened
            // and never finished, so the sentence has to name that step and
            // the window — a generic "try again" reads as the server being
            // busy, and the tab they forgot is still open.
            return "The sign-in was not finished: the browser step stayed open past the "
                 + $"{(int)expired.Window.TotalMinutes}-minute window without being completed. "
                 + "Start the sign-in again and finish it in the browser this time.";
        }

        if (First<TimeoutException>(chain) is not null
            || First<OperationCanceledException>(chain) is not null)
        {
            return "Panopto did not answer in time. The connection may be slow or the "
                 + "server busy — try again.";
        }

        if (First<SocketException>(chain) is not null
            || First<HttpRequestException>(chain) is not null)
        {
            return "Could not reach Panopto. Check this machine's network connection or VPN, "
                 + "then try again.";
        }

        if (First<TimeZoneNotFoundException>(chain) is not null)
        {
            return "The time zone configured for these rooms is not one Windows knows. "
                 + "The credentials file names it; it needs correcting before times can "
                 + "be shown or written.";
        }

        if (First<FileNotFoundException>(chain) is { } missing)
        {
            return $"A file this needed is not there: {Clean(missing.FileName ?? missing.Message)}";
        }

        if (First<PlatformNotSupportedException>(chain) is not null)
        {
            return "That part of Panopto's API only works on Windows, and this is not Windows.";
        }

        if (First<UnauthorizedAccessException>(chain) is not null)
        {
            return "Windows would not let the app read or write its own settings folder. "
                 + "Something on this machine is blocking access to the profile folder.";
        }

        // Last resort, and deliberately vague: if nothing above matched, the
        // message is one nobody anticipated, and guessing at it in front of a
        // user is worse than admitting the app does not know. The detail line's
        // tooltip carries the full chain.
        return "Something went wrong that the app did not expect. The full detail is in the "
             + "log, and the tooltip on this line has it too.";
    }

    /// <summary>
    /// Whether a cancellation was the transport giving up rather than someone
    /// asking it to stop.
    ///
    /// <para><b>The two arrive as the same exception and need opposite words.</b>
    /// A cancellation is an instruction that was obeyed — nothing happened, and
    /// nothing needs saying. A timeout is a write whose fate is unknown, and for
    /// a write that is the whole message: it may already have landed on the
    /// tenant, so "try again" is advice that can produce a second recording.</para>
    ///
    /// <para>The signal is the token, not the type. <c>HttpClient</c> reports its
    /// own timeout by throwing with the <i>uncancelled</i> token it was given,
    /// hanging a <see cref="TimeoutException"/> off the inner exception; a real
    /// cancellation carries the token that was cancelled. Checking the inner
    /// exception as well covers the case where a caller cancels its own token on
    /// the way out of a timeout it has already caught.</para>
    /// </summary>
    public static bool IsTimeout(OperationCanceledException error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error.InnerException is TimeoutException
               || !error.CancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// The exception and everything it wraps, outermost first.
    ///
    /// <para>No cycle guard, deliberately. <c>Exception.InnerException</c> is
    /// set once by the constructor and is not virtual, so a chain cannot loop
    /// back on itself — and a guard that cannot be reached is a guard that
    /// cannot be tested, sitting in the one code path where being wrong is
    /// worst.</para>
    /// </summary>
    private static List<Exception> Chain(Exception error)
    {
        var chain = new List<Exception>();

        for (var current = error; current is not null; current = current.InnerException)
        {
            chain.Add(current);
        }

        return chain;
    }

    private static T? First<T>(List<Exception> chain) where T : Exception
    {
        foreach (var error in chain)
        {
            if (error is T match) return match;
        }

        return null;
    }

    /// <summary>
    /// Collapses an exception message onto one line and caps its length.
    ///
    /// <para>A SOAP fault body is XML, and a failed HTTP response body can be a
    /// whole page; both arrive with newlines in them and neither belongs in a
    /// status line.</para>
    /// </summary>
    private static string Clean(string message)
    {
        var flattened = string.Join(' ', message
            .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        while (flattened.Contains("  ", StringComparison.Ordinal))
        {
            flattened = flattened.Replace("  ", " ", StringComparison.Ordinal);
        }

        const int limit = 200;
        return flattened.Length <= limit ? flattened : flattened[..limit] + "…";
    }
}
