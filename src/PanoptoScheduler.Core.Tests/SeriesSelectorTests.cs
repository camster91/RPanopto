using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core.Tests;

public class SeriesSelectorTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0); // Monday noon

    private static PanoptoSession S(string folder, DateTime start, string room = "Room 101") => new()
    {
        SessionID = Guid.NewGuid().ToString("D"), SessionName = "L", FolderID = folder, FolderName = folder.ToUpperInvariant(),
        RemoteRecorderName = room, StartTime = start, Duration = 5400,
    };

    [Fact]
    public void Folders_count_only_future_sessions()
    {
        var folders = SeriesSelector.Folders([S("a", Now.AddDays(-7)), S("a", Now.AddDays(1)), S("b", Now.AddDays(2))], Now);
        Assert.Equal(new[] { ("a", 1), ("b", 1) }, folders.Select(f => (f.FolderId, f.Upcoming)));
    }

    [Fact]
    public void Select_filters_folder_room_weekday_and_range()
    {
        var tue = Now.AddDays(1); var wed = Now.AddDays(2);
        var sessions = new[] { S("a", tue), S("a", wed), S("a", tue.AddDays(7), "Room 202"), S("b", tue) };
        var picked = SeriesSelector.Select(sessions,
            new SeriesFilter("a", RecorderName: "Room 101", Weekdays: new HashSet<DayOfWeek> { DayOfWeek.Tuesday }), Now);
        Assert.Single(picked);
    }

    [Fact]
    public void Select_excludes_sessions_already_started()
        => Assert.Empty(SeriesSelector.Select([S("a", Now.AddMinutes(-5))], new SeriesFilter("a"), Now));

    [Fact]
    public void OnDates_picks_by_room_day()
    {
        var reading = DateOnly.FromDateTime(Now.AddDays(8));
        var picked = SeriesSelector.OnDates([S("a", Now.AddDays(1)), S("a", Now.AddDays(8))], new HashSet<DateOnly> { reading });
        Assert.Single(picked);
    }
}
