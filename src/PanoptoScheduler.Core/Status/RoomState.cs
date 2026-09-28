namespace PanoptoScheduler.Core.Status;

public enum RoomState { Unknown, Idle, Recording, Offline, Late }

public sealed record RoomStatus(Guid RecorderId, string RecorderName, RoomState State, string? SessionName);

/// <param name="Failed">The read behind this snapshot did not succeed; every room is Unknown.</param>
public sealed record StatusSnapshot(IReadOnlyList<RoomStatus> Rooms, DateTime CheckedAtRoom, bool Failed);

/// <param name="Key">Stable per session and kind, so one problem alerts once.</param>
public sealed record StatusAlert(string Key, string RecorderName, string Message);

/// <summary>SOAP <c>RemoteRecorder.State</c> strings → what the strip shows (spec Amendment 1).</summary>
public static class RecorderStateMap
{
    public static RoomState From(string? state) => state?.Trim() switch
    {
        "Recording" or "Paused" => RoomState.Recording,
        "Stopped" or "Previewing" or "RecorderRunning" => RoomState.Idle,
        "Disconnected" or "Faulted" => RoomState.Offline,
        _ => RoomState.Unknown,
    };
}
