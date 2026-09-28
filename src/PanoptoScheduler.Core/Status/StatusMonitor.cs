using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Diagnostics;
using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Status;

/// <summary>
/// Looks at the rooms at today's checkpoints and nowhere else (spec §1).
///
/// <para><b>Waits in slices of at most a minute and re-reads the clock.</b> A
/// single long delay does not advance while the machine sleeps, so a laptop
/// closed at 8:40 and opened at 9:05 would otherwise either miss the 9:03
/// check entirely or fire every missed one at once. Slicing is free — no call
/// is made on wake unless a checkpoint is due — and a missed checkpoint older
/// than <see cref="MissedGrace"/> is dropped, because its answer is history.</para>
/// </summary>
public sealed class StatusMonitor(
    Func<CancellationToken, Task<IReadOnlyList<RemoteRecorder>>> readRecorders,
    Func<IReadOnlyList<PanoptoSession>> sessions,
    Func<DateTime> nowRoom,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    public static readonly TimeSpan MaxSleepSlice = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MissedGrace = TimeSpan.FromMinutes(5);

    private readonly HashSet<string> _alerted = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _alertDay;

    public event Action<StatusSnapshot>? Updated;
    public event Action<StatusAlert>? Alert;

    public StatusSnapshot? Last { get; private set; }

    public async Task CheckNowAsync(CancellationToken ct = default)
    {
        var now = nowRoom();
        try
        {
            var recorders = await readRecorders(ct).ConfigureAwait(false);
            var (rooms, alerts) = RoomStatusEvaluator.Evaluate(recorders, sessions(), now);
            Publish(new StatusSnapshot(rooms, now, Failed: false));

            var today = DateOnly.FromDateTime(now);
            if (today != _alertDay) { _alerted.Clear(); _alertDay = today; }
            foreach (var alert in alerts)
                if (_alerted.Add(alert.Key)) Alert?.Invoke(alert);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Not retried: the next checkpoint or a Refresh tries again (spec §1).
            AppLog.Warn($"Room status check failed: {ex.Message}");
            var unknown = (Last?.Rooms ?? []).Select(r => r with { State = RoomState.Unknown }).ToList();
            Publish(new StatusSnapshot(unknown, now, Failed: true));
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var lastChecked = DateTime.MinValue;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var now = nowRoom();
            var today = DateOnly.FromDateTime(now);
            var plan = CheckpointPlanner.ForDay(sessions(), today);

            var due = plan.Where(t => t <= now && t > lastChecked).ToList();
            if (due.Count > 0)
            {
                lastChecked = due[^1];
                if (now - due[^1] <= MissedGrace) await CheckNowAsync(ct).ConfigureAwait(false);
                continue;
            }

            var next = plan.FirstOrDefault(t => t > now);
            var until = next == default ? today.AddDays(1).ToDateTime(TimeOnly.MinValue) - now : next - now;
            await delay(until < MaxSleepSlice ? until : MaxSleepSlice, ct).ConfigureAwait(false);
        }
    }

    private void Publish(StatusSnapshot snapshot)
    {
        Last = snapshot;
        Updated?.Invoke(snapshot);
    }
}
