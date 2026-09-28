using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.Status;

namespace PanoptoScheduler.Core.Tests;

public class RoomStatusEvaluatorTests
{
    private static readonly Guid Rec = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Sess = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly DateTime Nine = new(2026, 9, 28, 9, 0, 0);

    private static RemoteRecorder Recorder(string? state) => new() { Id = Rec, Name = "Room 101", State = state };

    private static PanoptoSession At(DateTime start) => new()
    {
        SessionID = Sess.ToString("D"), SessionName = "RSM1234 L3",
        RemoteRecorderID = Rec.ToString("D"), RemoteRecorderName = "Room 101",
        StartTime = start, Duration = 5400,
    };

    [Theory]
    [InlineData("Recording", RoomState.Recording)]
    [InlineData("Paused", RoomState.Recording)]
    [InlineData("Stopped", RoomState.Idle)]
    [InlineData("Previewing", RoomState.Idle)]
    [InlineData("RecorderRunning", RoomState.Idle)]
    [InlineData("Disconnected", RoomState.Offline)]
    [InlineData("Faulted", RoomState.Offline)]
    [InlineData("SomethingNew", RoomState.Unknown)]
    [InlineData(null, RoomState.Unknown)]
    public void Maps_recorder_state(string? raw, RoomState expected)
        => Assert.Equal(expected, RecorderStateMap.From(raw));

    [Fact]
    public void Idle_three_minutes_after_start_is_late_and_alerts()
    {
        var (rooms, alerts) = RoomStatusEvaluator.Evaluate([Recorder("Stopped")], [At(Nine)], Nine.AddMinutes(3));
        Assert.Equal(RoomState.Late, rooms[0].State);
        Assert.Equal($"late|{Sess:D}", Assert.Single(alerts).Key);
    }

    [Fact]
    public void Idle_two_minutes_after_start_is_not_late_yet()
    {
        var (rooms, alerts) = RoomStatusEvaluator.Evaluate([Recorder("Stopped")], [At(Nine)], Nine.AddMinutes(2));
        Assert.Equal(RoomState.Idle, rooms[0].State);
        Assert.Empty(alerts);
    }

    [Fact]
    public void Recording_during_session_is_fine()
    {
        var (rooms, alerts) = RoomStatusEvaluator.Evaluate([Recorder("Recording")], [At(Nine)], Nine.AddMinutes(10));
        Assert.Equal(RoomState.Recording, rooms[0].State);
        Assert.Empty(alerts);
    }

    [Fact]
    public void Offline_ten_minutes_before_a_session_alerts_once_keyed_by_session()
    {
        var (rooms, alerts) = RoomStatusEvaluator.Evaluate([Recorder("Disconnected")], [At(Nine)], Nine.AddMinutes(-10));
        Assert.Equal(RoomState.Offline, rooms[0].State);
        Assert.Equal($"offline|{Sess:D}", Assert.Single(alerts).Key);
    }

    [Fact]
    public void Offline_with_nothing_booked_soon_does_not_alert()
    {
        var (_, alerts) = RoomStatusEvaluator.Evaluate([Recorder("Disconnected")], [At(Nine)], Nine.AddHours(-3));
        Assert.Empty(alerts);
    }

    [Fact]
    public void Unknown_state_during_a_session_stays_unknown_not_late()
    {
        var (rooms, alerts) = RoomStatusEvaluator.Evaluate([Recorder(null)], [At(Nine)], Nine.AddMinutes(5));
        Assert.Equal(RoomState.Unknown, rooms[0].State);
        Assert.Empty(alerts);
    }

    [Fact]
    public void Session_matched_by_recorder_name_when_id_missing()
    {
        var s = At(Nine); s.RemoteRecorderID = null;
        var (rooms, _) = RoomStatusEvaluator.Evaluate([Recorder("Stopped")], [s], Nine.AddMinutes(4));
        Assert.Equal(RoomState.Late, rooms[0].State);
    }
}
