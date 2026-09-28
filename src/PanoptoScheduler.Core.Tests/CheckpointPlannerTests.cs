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
}
