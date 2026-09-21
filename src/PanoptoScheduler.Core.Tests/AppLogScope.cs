using System.Runtime.CompilerServices;
using PanoptoScheduler.Core.Diagnostics;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// Points <see cref="AppLog"/> at a temp directory for the whole test run, so
/// nothing these facts report can be mistaken for something the tenant did.
///
/// <para>Several facts here deliberately drive a listing to the paging ceiling —
/// a tenant that ignores <c>PageNumber</c> must stop at <c>MaxPages</c> rather
/// than loop, and that wall has to be proved. The ceiling is reported through
/// <see cref="AppLog.Warn"/>, so with the default directory a <i>passing</i>
/// test writes "stopped at the 60-page ceiling after 15000 item(s)" into the
/// real <c>~/.panopto-scheduler/logs/app-*.log</c>.</para>
///
/// <para>That line is not noise anyone can recognise as noise. It reads exactly
/// like the tenant truncating a listing and quietly dropping every booking past
/// the cut, in the one file an operator is asked to send when something looks
/// wrong — and it sent us chasing a tenant that has 19 recorders for a fault
/// that was never there. A line a green test can produce belongs somewhere
/// disposable.</para>
///
/// <para>Redirecting costs nothing: no fact reads the log file. The ones that
/// care about a message take it from the exception or the result, and
/// <see cref="AppLog.Full"/> is a pure formatter over an exception.</para>
/// </summary>
internal static class AppLogScope
{
    /// <summary>
    /// Runs before any fact, in every test host. A module initializer rather
    /// than a fixture because the ordering guarantee is the whole point: a
    /// fixture that had not run yet would leave a window where a test writes
    /// the real file.
    /// </summary>
    [ModuleInitializer]
    internal static void Redirect()
    {
        AppLog.RedirectDirectory = Path.Combine(
            Path.GetTempPath(), "panopto-scheduler-tests", "logs");
    }
}
