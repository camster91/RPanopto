using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Status;

public static class RoomStatusEvaluator
{
    public static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan OfflineWarnBefore = TimeSpan.FromMinutes(10);

    /// <param name="nowRoom">The room's wall clock — the same clock the sessions carry.</param>
    public static (IReadOnlyList<RoomStatus> Rooms, IReadOnlyList<StatusAlert> Alerts) Evaluate(
        IReadOnlyList<RemoteRecorder> recorders,
        IReadOnlyList<PanoptoSession> sessions,
        DateTime nowRoom)
    {
        var rooms = new List<RoomStatus>(recorders.Count);
        var alerts = new List<StatusAlert>();

        foreach (var recorder in recorders)
        {
            var mine = sessions.Where(s => BelongsTo(s, recorder) && s.EffectiveStart is not null).ToList();
            var current = mine.FirstOrDefault(s => s.EffectiveStart <= nowRoom && nowRoom < (s.EffectiveEnd ?? s.EffectiveStart));
            var upcoming = mine.Where(s => s.EffectiveStart > nowRoom && s.EffectiveStart - nowRoom <= OfflineWarnBefore)
                               .OrderBy(s => s.EffectiveStart).FirstOrDefault();

            var state = RecorderStateMap.From(recorder.State);

            if (state == RoomState.Idle && current is not null && nowRoom - current.EffectiveStart >= LateAfter)
            {
                state = RoomState.Late;
                alerts.Add(new StatusAlert($"late|{current.SessionID}", recorder.Name,
                    $"{recorder.Name}: \"{current.SessionName}\" has not started recording ({current.EffectiveStart:h:mm tt})."));
            }
            else if (state == RoomState.Offline && (upcoming ?? current) is { } due)
            {
                alerts.Add(new StatusAlert($"offline|{due.SessionID}", recorder.Name,
                    $"{recorder.Name} is offline; \"{due.SessionName}\" is booked at {due.EffectiveStart:h:mm tt}."));
            }

            rooms.Add(new RoomStatus(recorder.Id, recorder.Name, state, (current ?? upcoming)?.SessionName));
        }

        return (rooms, alerts);
    }

    private static bool BelongsTo(PanoptoSession s, RemoteRecorder r)
        => Guid.TryParse(s.RemoteRecorderID, out var id)
            ? id == r.Id
            : string.Equals(s.RemoteRecorderName?.Trim(), r.Name.Trim(), StringComparison.OrdinalIgnoreCase);
}
