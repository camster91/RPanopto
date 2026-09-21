using System.Runtime.CompilerServices;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// The room's calendar must not read this machine's clock.
///
/// <para>The plan's verification for this work asked for the suite to pass with
/// the process zone forced to UTC. On Windows that check cannot be run: .NET
/// ignores the <c>TZ</c> environment variable, so <c>TZ=UTC</c> leaves
/// <see cref="TimeZoneInfo.Local"/> reading Eastern Standard Time and the suite
/// would go green without having proved anything. This is the substitute, and it
/// tests the invariant rather than one configuration of it — these files never
/// ask the machine what time it is, so no setting of the machine can change what
/// they answer.</para>
///
/// <para>The limit of it, honestly: it scans source text, so it proves these
/// files contain no call to the machine clock, not that no file anywhere does.
/// The list below is the display path — everything between a wire value and
/// something a person reads.</para>
/// </summary>
public class MachineClockGuardTests
{
    private static readonly string[] MustNotReadThisMachinesClock =
    [
        @"PanoptoScheduler.Core\Json\WcfDateTimeConverter.cs",
        @"PanoptoScheduler.Core\Layout\CalendarLayout.cs",
        @"PanoptoScheduler.Core\Models\PanoptoSession.cs",
        @"PanoptoScheduler.Core\Scheduling\BookingPatternGenerator.cs",
        @"PanoptoScheduler.Core\Scheduling\RoomClock.cs",
        @"PanoptoScheduler.App\ViewModels\BookingPatternViewModel.cs",
        @"PanoptoScheduler.App\ViewModels\CalendarViewModel.cs",
    ];

    private static readonly string[] Forbidden =
    [
        "TimeZoneInfo.Local",
        "DateTime.Now",
        "DateTime.Today",
        "DateTimeKind.Local",
        "ToLocalTime",
        "LocalDateTime",
    ];

    /// <summary>
    /// The <c>src</c> directory, from this file's own location — <c>CallerFilePath</c>
    /// bakes in the path at compile time, so the test walks up rather than depending
    /// on the runner's working directory.
    /// </summary>
    private static string SourceRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

    [Fact]
    public void The_display_path_never_asks_this_machine_what_time_it_is()
    {
        var root = SourceRoot();
        var offences = new List<string>();

        foreach (var relative in MustNotReadThisMachinesClock)
        {
            var path = Path.Combine(root, relative);
            Assert.True(File.Exists(path),
                $"{relative} is not where this guard expects it. If it moved, move it " +
                "in this list too — a guard that scans nothing passes silently.");

            var lineNumber = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                if (IsCommentLine(line)) continue;

                foreach (var forbidden in Forbidden)
                    if (line.Contains(forbidden, StringComparison.Ordinal))
                        offences.Add($"{relative}:{lineNumber}  {line.Trim()}");
            }
        }

        Assert.True(offences.Count == 0,
            "These read this machine's clock, so the calendar is wrong on a workstation " +
            "set to another zone:\n" + string.Join("\n", offences));
    }

    /// <summary>
    /// The documentation in these files names the very calls it forbids — that is
    /// what the explanation is about, so whole comment lines are dropped. Dropping
    /// only whole lines means a trailing comment on a line of code is still scanned;
    /// a false positive there is a person's to settle, and safer than a blind spot.
    /// </summary>
    private static bool IsCommentLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal);
    }
}
