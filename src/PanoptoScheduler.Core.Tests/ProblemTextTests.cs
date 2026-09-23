using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using PanoptoScheduler.Core.Auth;
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Diagnostics;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// What a person is shown when something fails.
///
/// <para>The point of the summary is that it says what to do, and the point of
/// keeping the full text is that the summary is allowed to be vague. Both halves
/// are asserted here, because a summary that quietly dropped the detail would
/// pass a test that only looked at the summary.</para>
/// </summary>
public class ProblemTextTests
{
    [Fact]
    public void ASoapFaultIsSummarisedWithPanoptosOwnWords()
    {
        var fault = new PanoptoSoapFaultException(
            "ScheduleRecording",
            "Server.Error",
            "The remote recorder is already in use at this time.");

        var summary = ProblemText.Summarise(fault);

        Assert.Contains("already in use", summary, StringComparison.Ordinal);
        AssertNoLeak(summary);

        // The fault's own Message is "Panopto rejected ScheduleRecording:
        // …", so interpolating it into the summary would double the prefix and
        // leak the internal operation name — both forbidden by the contract on
        // Summarise. Before RawMessage existed, this test stayed green through
        // exactly that, because it only checked the Panopto sentence.
        Assert.Equal(1, summary.Split("Panopto rejected", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("ScheduleRecording", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AFaultsXmlBodyIsFlattenedOntoOneLine()
    {
        var fault = new PanoptoSoapFaultException(
            "ScheduleRecording",
            "Server.Error",
            "<s:Envelope>\n  <s:Body>\n    <s:Fault>Recorder busy</s:Fault>\n  </s:Body>\n</s:Envelope>");

        var summary = ProblemText.Summarise(fault);

        Assert.DoesNotContain('\n', summary);
        Assert.DoesNotContain('\r', summary);
        Assert.Contains("Recorder busy", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExpiredSignInSaysToSignInAgain()
    {
        var expired = new PanoptoRequestException(
            "SessionManagement/GetSessions", HttpStatusCode.Unauthorized, "");

        var summary = ProblemText.Summarise(expired);

        Assert.Contains("sign in again", summary, StringComparison.OrdinalIgnoreCase);
        AssertNoLeak(summary);
    }

    [Fact]
    public void AProblemAtPanoptosEndSaysItIsProbablyTemporary()
    {
        var outage = new PanoptoRequestException(
            "SessionManagement/ScheduleRecording",
            HttpStatusCode.BadGateway,
            "<html>502 Bad Gateway</html>");

        var summary = ProblemText.Summarise(outage);

        Assert.Contains("Panopto reported a problem at its end", summary, StringComparison.Ordinal);
        Assert.Contains("502", summary, StringComparison.Ordinal);
        AssertNoLeak(summary);
    }

    [Fact]
    public void ADroppedConnectionSaysToCheckTheNetwork()
    {
        // The shape a real one arrives in: HttpClient wraps the socket error, so
        // the useful exception is not the outermost.
        var dropped = new HttpRequestException(
            "An error occurred while sending the request.",
            new SocketException((int)SocketError.HostUnreachable));

        var summary = ProblemText.Summarise(dropped);

        Assert.Contains("Could not reach Panopto", summary, StringComparison.Ordinal);
        AssertNoLeak(summary);
    }

    [Fact]
    public void ASlowServerIsNotReportedAsADroppedConnection()
    {
        // A timeout usually arrives wrapped in an OperationCanceledException,
        // which is also what a socket failure can look like. The timeout has to
        // win, because the advice differs: one says try again, the other says
        // check the network.
        var slow = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout",
            new TimeoutException("The operation has timed out."));

        var summary = ProblemText.Summarise(slow);

        Assert.Contains("did not answer in time", summary, StringComparison.Ordinal);
        AssertNoLeak(summary);
    }

    /// <summary>
    /// The distinction the app keys its wording off, and the one it used to get
    /// wrong in the direction that costs a duplicate recording.
    ///
    /// <para><b>Both arrive as <c>OperationCanceledException</c>, and they need
    /// opposite advice.</b> A cancellation was an instruction that was obeyed:
    /// nothing happened, so there is nothing to act on. A timeout is a write whose
    /// fate is unknown — it may already have landed on the tenant — so "try again"
    /// is the one thing an operator must not be told, because on a move or a
    /// booking it produces a second one.</para>
    ///
    /// <para>The shape below is the one <c>HttpClient</c> actually produces when
    /// its own <c>Timeout</c> elapses: the exception carries the <i>uncancelled</i>
    /// token it was handed, with a <see cref="TimeoutException"/> hung off the
    /// inner exception. Neither half is decoration — the token is what a genuine
    /// cancellation would have cancelled, and the inner exception is what survives
    /// a caller that cancels its own token on the way out.</para>
    /// </summary>
    [Fact]
    public void An_http_timeout_is_a_timeout_not_a_cancellation()
    {
        var timedOut = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout",
            new TimeoutException("The operation has timed out."));

        Assert.True(ProblemText.IsTimeout(timedOut));
    }

    /// <summary>
    /// And the other half, which is the one that must never be read as a timeout:
    /// someone asked for the work to stop. The token is the whole signal — it is
    /// cancelled, and nothing is wrapped, so the call was obeyed and no write
    /// went out.
    /// </summary>
    [Fact]
    public void A_cancelled_token_is_a_cancellation_not_a_timeout()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var cancelled = new OperationCanceledException("Stopped.", source.Token);

        Assert.False(ProblemText.IsTimeout(cancelled));
    }

    /// <summary>
    /// The tie-break, spelled out because it is a decision rather than an
    /// accident: when a token was cancelled <i>and</i> a timeout is wrapped
    /// inside, the timeout wins.
    ///
    /// <para>A caller that catches its own timeout and then cancels the token on
    /// the way out produces exactly this, and reading it as a calm cancellation
    /// would tell the operator nothing happened when the write may well have
    /// landed. Wrong in that direction loses a recording; wrong the other way
    /// costs a sentence that says "check before retrying".</para>
    /// </summary>
    [Fact]
    public void A_wrapped_timeout_outranks_a_cancelled_token()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var both = new OperationCanceledException(
            "Cancelled after timing out.", new TimeoutException("The operation has timed out."), source.Token);

        Assert.True(ProblemText.IsTimeout(both));
    }

    /// <summary>
    /// The other signal, on its own: no inner exception at all, and a token
    /// nobody cancelled.
    ///
    /// <para>This is the case the token half of the check exists for. If the
    /// answer cannot have come from a cancellation request — there was no request;
    /// the token is still live — then it did not come from one, and the only thing
    /// left that cancels a request is the transport giving up. Reading this as a
    /// calm cancellation is how a write that timed out gets reported as "nothing
    /// happened".</para>
    /// </summary>
    [Fact]
    public void An_uncancelled_token_alone_is_enough()
    {
        var unexplained = new OperationCanceledException(
            "The operation was canceled.", CancellationToken.None);

        Assert.True(ProblemText.IsTimeout(unexplained));
    }

    [Fact]
    public void Nothing_is_not_a_timeout()
    {
        Assert.Throws<ArgumentNullException>(() => ProblemText.IsTimeout(null!));
    }

    [Fact]
    public void AnUnknownFailureAdmitsItAndPointsAtTheLog()
    {
        var summary = ProblemText.Summarise(new InvalidOperationException(
            "Object reference not set to an instance of an object."));

        Assert.Contains("did not expect", summary, StringComparison.Ordinal);
        Assert.Contains("log", summary, StringComparison.OrdinalIgnoreCase);

        // Not "Object reference not set..." — that is a sentence for whoever
        // wrote the code, and the person reading this cannot act on it.
        Assert.DoesNotContain("Object reference", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ASummaryNeverCarriesAStackTraceOrATypeName()
    {
        Exception error;

        try
        {
            throw new InvalidOperationException("thrown from a helper");
        }
        catch (Exception caught)
        {
            error = caught;
        }

        var summary = ProblemText.Summarise(error);

        AssertNoLeak(summary);
    }

    [Fact]
    public void TheFullTextKeepsTheInnermostMessage()
    {
        // This is the half of the contract that makes the summary allowed to be
        // vague. If the detail did not survive, "the full detail is in the log"
        // would be a lie.
        var buried = new InvalidOperationException(
            "one or more errors occurred",
            new HttpRequestException("the innermost reason"));

        var full = AppLog.Full(buried);

        Assert.Contains("the innermost reason", full, StringComparison.Ordinal);
        Assert.Contains("HttpRequestException", full, StringComparison.Ordinal);
        Assert.Contains("--- inner ---", full, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFullTextKeepsEveryLevelNotJustTheInnermost()
    {
        var buried = new InvalidOperationException(
            "outermost",
            new ApplicationException(
                "middle",
                new HttpRequestException("innermost")));

        var full = AppLog.Full(buried);

        Assert.Contains("outermost", full, StringComparison.Ordinal);
        Assert.Contains("middle", full, StringComparison.Ordinal);
        Assert.Contains("innermost", full, StringComparison.Ordinal);
    }

    /// <summary>
    /// The body Panopto sent back with a failed request. Both clients
    /// truncate it to 400 characters so it can be written down, and the
    /// Message names only the endpoint and the status — so if the full text
    /// does not carry the body, the one line in which the tenant says what it
    /// objected to never reaches anyone. The summary stays without it: the
    /// body is a fragment for whoever reads the log, not a sentence for the
    /// status line.
    /// </summary>
    [Fact]
    public void TheFullTextKeepsTheBodyPanoptoSentBack()
    {
        var refused = new PanoptoRequestException(
            "SessionManagement/GetSessions",
            HttpStatusCode.BadRequest,
            """{"error":"The folder does not exist or you lack access"}""");

        var full = AppLog.Full(refused);

        Assert.Contains("The folder does not exist", full, StringComparison.Ordinal);

        // And it stays in the record only: the summary of the same failure is
        // the one-liner about the request being refused, with no body in it.
        Assert.DoesNotContain("The folder does not exist",
            ProblemText.Summarise(refused), StringComparison.Ordinal);
    }

    /// <summary>
    /// The sign-in window running out is the one timeout that is not Panopto's:
    /// nothing had been asked of the server when it expired. Left as the bare
    /// cancellation it used to propagate as, the summary said "Panopto did not
    /// answer in time… the server busy" — advice to check a network that was
    /// never consulted, while the browser tab they forgot stayed open.
    /// </summary>
    [Fact]
    public void AnUnfinishedSignInNamesTheBrowserStepNotTheServer()
    {
        var expired = new SignInWindowExpiredException(TimeSpan.FromMinutes(5));

        var summary = ProblemText.Summarise(expired);

        Assert.Contains("browser", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5-minute", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("did not answer in time", summary, StringComparison.Ordinal);
        AssertNoLeak(summary);
    }

    [Fact]
    public void AVeryLongMessageIsCapped()
    {
        var enormous = new PanoptoSoapFaultException(
            "ScheduleRecording",
            "Server.Error",
            new string('x', 5000));

        var summary = ProblemText.Summarise(enormous);

        Assert.True(summary.Length < 400, $"summary was {summary.Length} characters");
    }

    /// <summary>
    /// Asserts the summary is one plain sentence: no stack trace frames, no
    /// exception type names, no XML.
    /// </summary>
    private static void AssertNoLeak(string summary)
    {
        Assert.DoesNotContain("   at ", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception:", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("<", summary, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', summary);
    }
}
