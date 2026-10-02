using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.Status;

namespace PanoptoScheduler.Core.Tests;

public class CheckpointPlannerTests
{
    private static readonly DateOnly Day = new(2026, 9, 28);
    private static PanoptoSession At(int h, int m, int dayOffset = 0)
        => new() { StartTime = Day.ToDateTime(new TimeOnly(h, m)).AddDays(dayOffset), Duration = 3600 };

    [Fact]
    public void Two_checkpoints_per_distinct_start()
    {
        var times = CheckpointPlanner.ForDay([At(9, 0), At(9, 0), At(14, 0)], Day);
        Assert.Equal(
            new[] { Day.ToDateTime(new TimeOnly(8, 50)), Day.ToDateTime(new TimeOnly(9, 3)), Day.ToDateTime(new TimeOnly(13, 50)), Day.ToDateTime(new TimeOnly(14, 3)) },
            times);
    }

    [Fact]
    public void Other_days_are_ignored()
        => Assert.Empty(CheckpointPlanner.ForDay([At(9, 0, dayOffset: 1)], Day));

    [Fact]
    public void Sessions_without_a_time_are_ignored()
        => Assert.Empty(CheckpointPlanner.ForDay([new PanoptoSession()], Day));

    [Fact]
    public void Checkpoints_within_a_minute_merge()
    {
        // 9:00's post-start (9:03) and 9:13's pre-start (9:03) coincide.
        var times = CheckpointPlanner.ForDay([At(9, 0), At(9, 13)], Day);
        Assert.Equal(3, times.Count);
    }

    private static DateTime T(int h, int m) => Day.ToDateTime(new TimeOnly(h, m));

    [Fact]
    public void A_merge_keeps_the_later_checkpoint_so_the_late_check_is_not_early()
    {
        // 9:00's late check (9:03) and 9:12's offline check (9:02) merge. Kept at 9:02 the
        // 9:00 room would be only two minutes in — not Late yet — and never looked at again.
        var times = CheckpointPlanner.ForDay([At(9, 0), At(9, 12)], Day);
        Assert.Equal(new[] { T(8, 50), T(9, 3), T(9, 15) }, times);
    }

    [Fact]
    public void Starts_a_minute_apart_both_get_their_checks()
    {
        // 8:50/8:51 and 9:03/9:04 pair up; the later of each pair is still inside 9:01's
        // offline window (9 min ahead) and at or after 9:01's late moment.
        var times = CheckpointPlanner.ForDay([At(9, 0), At(9, 1)], Day);
        Assert.Equal(new[] { T(8, 51), T(9, 4) }, times);
    }

    [Fact]
    public void Every_session_is_late_and_offline_checked_after_merging()
    {
        // Starts every minute: a chained merge would drift far past the first offline
        // window; clusters measured from their first time move a check at most a minute.
        var sessions = Enumerable.Range(0, 15).Select(i => At(9, i)).ToList();
        var times = CheckpointPlanner.ForDay(sessions, Day);

        foreach (var s in sessions)
        {
            var start = s.EffectiveStart!.Value;
            Assert.Contains(times, t => t < start && start - t <= RoomStatusEvaluator.OfflineWarnBefore);
            Assert.Contains(times, t => t - start >= RoomStatusEvaluator.LateAfter
                                        && t - start <= RoomStatusEvaluator.LateAfter + CheckpointPlanner.MergeWithin);
        }
    }
}
