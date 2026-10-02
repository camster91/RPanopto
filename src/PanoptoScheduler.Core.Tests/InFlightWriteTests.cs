using System.Net;
using System.Text;
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.RateLimiting;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// A stop pressed while a write is on the wire must not abandon it: the server
/// can still act on the request, and a caller told only "cancelled" reports the
/// row as not sent — which, for ScheduleRecording, is a second recording on the
/// next attempt.
/// </summary>
public class InFlightWriteTests
{
    private const string Response =
        """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>"""
        + """<ScheduleRecordingResponse xmlns="http://tempuri.org/"><ScheduleRecordingResult/></ScheduleRecordingResponse>"""
        + """</s:Body></s:Envelope>""";

    /// <summary>
    /// Holds each request until released, and honours the cancellation token the
    /// way a real transport does — so a cancelled token aborts the wait.
    /// </summary>
    private sealed class HeldHandler : HttpMessageHandler
    {
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Received.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Response, Encoding.UTF8, "text/xml"),
            };
        }
    }

    private static (PanoptoSoapClient Soap, HeldHandler Handler) Build()
    {
        var handler = new HeldHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://rotman.ca.panopto.com") };
        return (new PanoptoSoapClient(http, new RateLimiterRegistry(), new StubAuthenticator()), handler);
    }

    [Fact]
    public async Task A_write_already_sent_runs_to_its_answer_when_stopped()
    {
        var (soap, handler) = Build();
        using var cts = new CancellationTokenSource();

        var call = soap.InvokeAsync(
            PanoptoSoapClient.RemoteRecorderManagementPath, "IRemoteRecorderManagement",
            SoapXml.Operation("ScheduleRecording"), cts.Token);

        await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        handler.Release.SetResult();

        // Returns the server's answer rather than throwing: the caller learns
        // what happened to the write, and the stop applies before the next one.
        await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(call.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_read_already_sent_is_still_abandoned_when_stopped()
    {
        var (soap, handler) = Build();
        using var cts = new CancellationTokenSource();

        var call = soap.InvokeAsync(
            PanoptoSoapClient.RemoteRecorderManagementPath, "IRemoteRecorderManagement",
            SoapXml.Operation("ScheduleRecording"), cts.Token, safeToRetry: true);

        await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task A_write_not_yet_sent_is_stopped()
    {
        var (soap, handler) = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => soap.InvokeAsync(
            PanoptoSoapClient.RemoteRecorderManagementPath, "IRemoteRecorderManagement",
            SoapXml.Operation("ScheduleRecording"), cts.Token));

        Assert.False(handler.Received.Task.IsCompleted);
    }
}
