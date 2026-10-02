using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Status;

/// <summary>
/// When to look at the rooms today — the only automatic reads the app makes
/// (spec §1: two per distinct start time, never a timer poll).
/// </summary>
public static class CheckpointPlanner
{
    public static readonly TimeSpan MergeWithin = TimeSpan.FromMinutes(1);

    public static IReadOnlyList<DateTime> ForDay(IEnumerable<PanoptoSession> sessions, DateOnly day)
    {
        var raw = sessions
            .Select(s => s.EffectiveStart)
            .Where(s => s is { } start && DateOnly.FromDateTime(start) == day)
            .Select(s => s!.Value)
            .Distinct()
            .SelectMany(start => new[] { start - RoomStatusEvaluator.OfflineWarnBefore, start + RoomStatusEvaluator.LateAfter })
            .Order()
            .ToList();

        // Two checks a minute apart are one check — but it must be the LATER of the two.
        // Both rules are "at or after" tests: a room is Late only once LateAfter has
        // passed (RoomStatusEvaluator: now - start >= LateAfter), so a late check pulled
        // even a minute earlier sees an Idle room that is not late yet and stays silent —
        // starts at 9:00 and 9:12 used to fold 9:00's 9:03 check into 9:12's 9:02 one, and
        // nothing ever looked at 9:00 after 9:02. Moving a check later is harmless for both
        // rules: a late check only gets later, and an offline check moves at most
        // MergeWithin (1 min) towards a start OfflineWarnBefore (10 min) away, so the
        // session is still inside the evaluator's "start - now <= 10 min" upcoming window.
        // That bound only holds if a cluster is measured from its FIRST time, not chained
        // time-to-time: sessions starting every minute would otherwise make one long chain
        // whose last time is far past the earliest start's offline window.
        var merged = new List<DateTime>(raw.Count);
        var clusterStart = DateTime.MinValue;
        foreach (var t in raw)
        {
            if (merged.Count > 0 && t - clusterStart <= MergeWithin) merged[^1] = t;
            else { merged.Add(t); clusterStart = t; }
        }
        return merged;
    }
}
