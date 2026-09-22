using System.Threading;

namespace PanoptoScheduler.App;

/// <summary>
/// Keeps one copy of the app running per signed-in user.
///
/// <para><b>Why this exists.</b> Nothing stopped a second copy before, and the
/// installer makes it more reachable rather than less: there is now a shortcut
/// on the Start Menu <i>and</i> on the desktop, so launching twice is one
/// double-click away. Two copies share the saved sign-in file (last writer
/// wins, so a token refreshed by one is thrown away by the other) and the daily
/// log file, whose lock is in-process only — the two processes interleave their
/// appends and produce a log with two runs spliced together. They also both
/// hold a request budget that Panopto meters per client id, so the second copy
/// halves the number of bookings the first one can make before it is refused.
/// The only accidental guard today is the OAuth loopback port, which collides
/// late — at sign-in, after the window is already up and the user has already
/// started working.</para>
///
/// <para><b>It reports rather than activates.</b> Raising the window that is
/// already open would be nicer, and cross-process window activation is fiddly
/// enough — a stale process id, a window on another virtual desktop, an
/// elevated instance that cannot be driven from an unelevated one — that a
/// wrong guess is worse than a sentence telling the user where to look.</para>
/// </summary>
internal static class SingleInstance
{
    /// <summary>
    /// <c>Local\</c>, not <c>Global\</c>, on purpose: the namespace is per
    /// logon session, so two people signed in to the same machine — fast user
    /// switching, or a shared AV workstation — each get their own instance.
    /// That is the correct behaviour, because everything the app writes is
    /// per-user. A <c>Global\</c> name would also need a privilege this app
    /// should never ask for.
    /// </summary>
    private const string MutexName = @"Local\PanoptoScheduler.SingleInstance";

    /// <summary>
    /// Takes the lock, or reports that someone else has it.
    /// </summary>
    /// <param name="held">
    /// The mutex, once claimed, so it lives as long as the app does. It is not
    /// released explicitly: the operating system closes the handle when the
    /// process ends, which releases it, and that is the one path that also
    /// covers a crash. A mutex released early would let a second copy in while
    /// the first is still shutting down and still holding its log file open.
    /// </param>
    public static bool TryClaim(out Mutex? held)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);

        if (!first)
        {
            // Not the owner, so disposing is all this handle is good for.
            // Disposing without releasing is deliberate: the process that owns
            // it is still running, and releasing it here would hand the lock to
            // this copy on its way out.
            mutex.Dispose();
            held = null;
            return false;
        }

        held = mutex;
        return true;
    }
}
