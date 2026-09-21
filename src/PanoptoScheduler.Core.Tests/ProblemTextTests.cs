using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
