using PanoptoScheduler.Core.Diagnostics;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// What the log says about its own health.
///
/// <para>The crash dialog promises the operator a written log file, and that
/// promise is what makes them fetch it and send it on. When the write cannot
/// happen the promise has to be withdrawn — see
/// <see cref="AppLog.LastWriteFailed"/> — and these are the facts that keep
/// the flag honest.</para>
/// </summary>
public class AppLogTests
{
    /// <summary>
    /// A directory that is actually a file: every write against it throws,
    /// which is one of the shapes a dying log takes on a real machine — a
    /// folder made read-only, a path that is not a folder at all.
    /// </summary>
    [Fact]
    public void AWriteThatCannotHappenIsReportedAsOne()
    {
        var folder = Path.Combine(Path.GetTempPath(), "panopto-scheduler-tests");
        Directory.CreateDirectory(folder);

        var blocker = Path.Combine(folder, $"blocked-{Guid.NewGuid():N}.txt");
        File.WriteAllText(blocker, "a file, not a folder");

        var previous = AppLog.RedirectDirectory;
        try
        {
            AppLog.RedirectDirectory = blocker;

            AppLog.Info("a line that cannot be written");

            Assert.True(AppLog.LastWriteFailed);
        }
        finally
        {
            // The scope's directory has to win back no matter how the fact
            // ends; every other fact writes through this property.
            AppLog.RedirectDirectory = previous;
            File.Delete(blocker);
        }
    }

    /// <summary>
    /// The flag carries the most recent write's verdict, not a memory of any
    /// failure — a log that recovers stops announcing its death, so the crash
    /// dialog goes back to promising the file.
    /// </summary>
    [Fact]
    public void ARecoveredLogStopsAnnouncingItsDeath()
    {
        var previous = AppLog.RedirectDirectory;

        // A path whose parent is a file: unwritable, without leaving a blocker
        // of our own behind afterwards.
        var blocker = Path.Combine(Path.GetTempPath(),
            $"panopto-scheduler-tests-{Guid.NewGuid():N}.txt");
        File.WriteAllText(blocker, "a file, not a folder");

        try
        {
            AppLog.RedirectDirectory = Path.Combine(blocker, "logs");

            AppLog.Info("a line that cannot be written");
            Assert.True(AppLog.LastWriteFailed);

            AppLog.RedirectDirectory = previous;
            AppLog.Info("a line that lands");

            Assert.False(AppLog.LastWriteFailed);
        }
        finally
        {
            AppLog.RedirectDirectory = previous;
            File.Delete(blocker);
        }
    }
}