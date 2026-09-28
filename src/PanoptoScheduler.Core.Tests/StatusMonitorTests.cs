using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.Status;

namespace PanoptoScheduler.Core.Tests;

public class StatusMonitorTests
{
    private sealed class Clock(DateTime start)
    {
        public DateTime Now = start;
        public Task Delay(TimeSpan by, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Now += by;
            return Task.CompletedTask;
        }
    }

    private static readonly Guid Rec = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTime Nine = new(2026, 9, 28, 9, 0, 0);

    private static PanoptoSession Session(DateTime start) => new()
    {
        SessionID = Guid.NewGuid().ToString("D"), SessionName = "L", RemoteRecorderID = Rec.ToString("D"),
        StartTime = start, Duration = 3600,
    };

    [Fact]
    public async Task One_read_per_checkpoint_and_none_between()
    {
        var s = Session(Nine);
        var clock = new Clock(Nine.AddHours(-1));
        var reads = 0;
        using var cts = new CancellationTokenSource();
        var monitor = new StatusMonitor(
            _ => { reads++; if (clock.Now >= Nine.AddMinutes(3)) cts.Cancel();
                   return Task.FromResult<IReadOnlyList<RemoteRecorder>>([new() { Id = Rec, Name = "R", State = "Recording" }]); },
            () => [s], () => clock.Now, clock.Delay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync(cts.Token));

        Assert.Equal(2, reads); // 8:50 and 9:03
    }

    [Fact]
    public async Task Wake_after_sleep_runs_at_most_one_check()
    {
        var s = Session(Nine);
        var clock = new Clock(Nine.AddHours(-1));
        var reads = 0;
        using var cts = new CancellationTokenSource();
        Func<TimeSpan, CancellationToken, Task> sleepyDelay = (by, ct) =>
        {
            // First wait: the laptop sleeps until 9:05 — both checkpoints passed.
            if (clock.Now < Nine) clock.Now = Nine.AddMinutes(5); else cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var monitor = new StatusMonitor(
            _ => { reads++; return Task.FromResult<IReadOnlyList<RemoteRecorder>>([]); },
            () => [s], () => clock.Now, sleepyDelay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync(cts.Token));

        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Missed_checkpoint_older_than_grace_is_skipped()
    {
        var s = Session(Nine);
        var clock = new Clock(Nine.AddHours(-1));
        var reads = 0;
        using var cts = new CancellationTokenSource();
        Func<TimeSpan, CancellationToken, Task> sleepyDelay = (by, ct) =>
        {
            if (clock.Now < Nine) clock.Now = Nine.AddMinutes(30); else cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var monitor = new StatusMonitor(
            _ => { reads++; return Task.FromResult<IReadOnlyList<RemoteRecorder>>([]); },
            () => [s], () => clock.Now, sleepyDelay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync(cts.Token));

        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task Failed_read_publishes_an_unknown_snapshot_and_does_not_retry()
    {
        var s = Session(Nine);
        var clock = new Clock(Nine.AddMinutes(3));
        var reads = 0;
        var monitor = new StatusMonitor(
            _ => { reads++; throw new HttpRequestException("down"); },
            () => [s], () => clock.Now, clock.Delay);
        StatusSnapshot? seen = null;
        monitor.Updated += snapshot => seen = snapshot;

        await monitor.CheckNowAsync();

        Assert.Equal(1, reads);
        Assert.True(seen!.Failed);
    }

    [Fact]
    public async Task Same_alert_is_raised_once_per_day()
    {
        var s = Session(Nine);
        var clock = new Clock(Nine.AddMinutes(4));
        var monitor = new StatusMonitor(
            _ => Task.FromResult<IReadOnlyList<RemoteRecorder>>([new() { Id = Rec, Name = "R", State = "Stopped" }]),
            () => [s], () => clock.Now, clock.Delay);
        var alerts = 0;
        monitor.Alert += _ => alerts++;

        await monitor.CheckNowAsync();
        await monitor.CheckNowAsync();

        Assert.Equal(1, alerts);
    }
}
