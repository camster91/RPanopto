using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using PanoptoScheduler.Core;
using PanoptoScheduler.Core.Configuration;
using PanoptoScheduler.Core.Diagnostics;
using PanoptoScheduler.Core.Scheduling;
using PanoptoScheduler.Core.Updates;

namespace PanoptoScheduler.App;

public partial class App : Application
{
    private PanoptoConnection? _panopto;

    /// <summary>
    /// Held for the life of the process so a second copy cannot start. Never
    /// released by hand — see <see cref="SingleInstance.TryClaim"/>.
    /// </summary>
    private Mutex? _instanceLock;

    /// <summary>
    /// The build, as stamped by the csproj.
    ///
    /// <para>Two forms, because they answer different questions. This one is for
    /// the title bar — short enough to read over the phone. The SDK appends the
    /// commit sha to the informational version, so the title would otherwise
    /// carry forty hex characters nobody reads aloud; <see cref="FullVersion"/>
    /// keeps them, and that is what the log records.</para>
    /// </summary>
    public static string Version => FullVersion.Split('+')[0];

    /// <summary>
    /// The build including the commit sha, which is what identifies the exact
    /// source a log file came from.
    /// </summary>
    public static string FullVersion =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "unknown";

    /// <summary>
    /// Resolves credentials before any window opens. Startup is driven from here
    /// rather than <c>StartupUri</c> because the main window cannot be built
    /// until we know which tenant to talk to.
    ///
    /// <para>On a packaged install nothing is asked: <see cref="ShippedDefaults"/>
    /// supplies the tenant and client, and the only thing the user does is sign
    /// in as themselves. The dialog is the fallback for a development checkout
    /// and for a machine whose configuration is broken.</para>
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLog.Prune();

        // The second trail, pruned beside the first because it is the same
        // housekeeping on the same schedule — one file per day, deleted after a
        // month. It is not the app log and is not read by the same people: this
        // one records what the app changed in the tenant, row by row, so a
        // booking that goes wrong can be reconstructed afterwards.
        var auditLog = new FileBulkAuditLog();
        auditLog.Prune();

        AppLog.Info($"Panopto Scheduler {FullVersion} starting.");

        HookCrashHandlers();

        // Before anything is read or any window is built, so a second copy costs
        // nothing and asks for nothing. See SingleInstance for what two copies
        // would actually collide over.
        if (!SingleInstance.TryClaim(out _instanceLock))
        {
            AppLog.Info("Another copy is already running; this one is exiting.");

            // A self-test is run from a script and has nobody to click OK, so it
            // says so on the console and exits instead of waiting on a dialog.
            // That distinction matters here: the self-test windows are
            // interactive and never close themselves, so a fixture left open
            // from an earlier run is the ordinary way to reach this branch.
            var selfTest = e.Args.Any(a => a.StartsWith("--self-test", StringComparison.OrdinalIgnoreCase));
            if (selfTest)
            {
                Console.WriteLine("=== ALREADY RUNNING: close the open copy and try again ===");
            }
            else
            {
                MessageBox.Show(
                    "Panopto Scheduler is already open.\n\n"
                    + "It may be behind this window, or minimised on the taskbar. Only one "
                    + "copy can run at a time: two would share the same saved sign-in and "
                    + "the same log file, and would split the request budget Panopto allows "
                    + "between them.",
                    "Panopto Scheduler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

#if DEBUG
        // A binding that does not resolve is this app's quietest failure: WPF
        // reports it to the debug trace and to nobody else, so the control draws
        // blank and every log stays clean. That is exactly the shape of fault
        // that never gets found, so in a development build it goes to the log.
        //
        // Debug only. Some bindings fail benignly and often — a tooltip bound to
        // its own Text on a cell whose template has not been applied yet — and a
        // user's log file is not the place for those.
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingLogListener());
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
#endif

        PanoptoCredentials? credentials = null;
        string? problem = null;

        try
        {
            credentials = CredentialStore.Resolve();
        }
        catch (Exception ex)
        {
            // A malformed file should be fixable from the dialog, not fatal
            // before a window ever appears.
            problem = $"Could not read {CredentialStore.DefaultPath}: {ex.Message}";
            AppLog.Error("Credentials could not be read.", ex);
        }

        if (credentials is null)
        {
            AppLog.Info("No credentials on this machine and none shipped — asking.");
            var dialog = new CredentialsDialog(initialError: problem);
            if (dialog.ShowDialog() != true)
            {
                AppLog.Info("Setup was cancelled; exiting.");
                Shutdown();
                return;
            }

            credentials = dialog.Credentials!;
        }

        AppLog.Info($"Using tenant {credentials.TenantUrl}, rooms in {credentials.TimeZone}.");

        // One connection for the whole app. Sign-in opens a browser, and Panopto
        // meters requests per client id, so a second connection would both ask
        // the user to sign in again and quietly double the request budget.
        // The trail is passed in here, at the one place a connection is built, so
        // every write path under it records without having to remember to. A
        // writer constructed without it is silent by design — the connection is
        // still usable, it just leaves no trace — which is why this line is the
        // one that matters.
        _panopto = new PanoptoConnection(credentials, auditLog: auditLog);

        var window = new MainWindow(_panopto) { Title = $"Panopto Scheduler {Version}" };
        MainWindow = window;

#if DEBUG
        // Draws a fixture week and touches no network. The only way to look at
        // the calendar on a machine that has no tenant configured — and, as it
        // happens, the only way to see whether a layout change worked without
        // asking a person to read a screenshot.
        if (e.Args.Contains("--self-test-week", StringComparer.OrdinalIgnoreCase))
        {
            AppLog.Info("Self-test week requested; no request will be made to Panopto.");
            window.UseDebugFixture();
        }

        // The fixture week with the details panel opened, so the panel's own
        // markup is loaded and its bindings resolved on a machine with no tenant.
        // Nothing else draws it: the panel opens on a click, and a self-test has
        // no pointer.
        if (e.Args.Contains("--self-test-panel", StringComparer.OrdinalIgnoreCase))
        {
            AppLog.Info("Self-test panel requested; no request will be made to Panopto.");
            window.UseDebugFixtureWithPanel();
        }

        // The fixture week with the room listing reported as truncated, which is
        // the only way to draw the banner that reports it. That banner is the
        // on-screen half of the "i dont see all the recorders" fix; the other half
        // is a log line.
        if (e.Args.Contains("--self-test-rooms", StringComparer.OrdinalIgnoreCase))
        {
            AppLog.Info("Self-test rooms requested; the room listing will be reported as truncated.");
            window.UseDebugFixtureWithIncompleteRooms();
        }

        // The fixture week with the update banner drawn, which is the only way
        // to render it on a machine whose feed is current: the banner waits
        // on a published version being newer than this one, and on a machine
        // that is up to date it correctly shows nothing. The fixture names a
        // made-up version so the banner and its link can be asserted.
        if (e.Args.Contains("--self-test-update", StringComparer.OrdinalIgnoreCase))
        {
            AppLog.Info("Self-test update requested; no feed will be read.");
            window.UseDebugFixtureWithUpdate();
        }

        // Said in the title, not only in the status line, because a fixture window
        // is indistinguishable from a real one at a glance and can never write —
        // so an operator who finds one open works in it, watches every change fail,
        // and concludes the app is broken. Which is exactly what happened. Asking
        // the window whether it is a fixture rather than listing the flags here
        // means a fixture flag added later cannot be forgotten in this line.
        if (window.IsFixture)
            window.Title = $"Panopto Scheduler {Version} — self-test, nothing here is real";

        // Builds every window but the main one, and throws them away.
        //
        // A {StaticResource} that names a key nobody declared does not fail the
        // build. It throws when the window is loaded, and these three load only
        // when something is already wrong — no credentials, an expired session,
        // or an operator reaching for the bulk tools. That is the worst possible
        // time to discover a typo in a style name, and it is why this exists
        // rather than trusting the build.
        //
        // Constructing is the whole test: InitializeComponent resolves every
        // resource reference in the file. Nothing is shown, so nothing flashes.
        if (e.Args.Contains("--self-test-dialogs", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _ = new CredentialsDialog();
                _ = new SignInDialog(new ViewModels.CalendarViewModel(_panopto), "self-test");

                var bulk = new ViewModels.BulkWindowViewModel(_panopto, () => [], () => { });
                _ = new BulkWindow(bulk);

                AppLog.Info("Self-test dialogs: every window loaded all of its resources.");
                Console.WriteLine("=== DIALOGS OK ===");
            }
            catch (Exception ex)
            {
                AppLog.Error("Self-test dialogs: a window could not be loaded.", ex);
                Console.WriteLine($"=== DIALOGS FAILED: {ex.Message} ===");
            }

            Shutdown();
            return;
        }

        // Opens the bulk window on a fixture and reads back what its cells resolved
        // to, which is the only way to catch a mis-scoped binding inside a grid cell.
        // Constructing a window resolves its resource references but realizes no
        // templates: a cell that binds to nothing looks exactly like a cell with
        // nothing in it, and neither raises an error on its own.
        if (e.Args.Contains("--self-test-bulk", StringComparer.OrdinalIgnoreCase))
        {
            AppLog.Info("Self-test bulk requested; no request will be made to Panopto.");

            var bulk = new ViewModels.BulkWindowViewModel(_panopto, () => [], () => { });
            var bulkWindow = new BulkWindow(bulk)
            {
                Title = "Bulk actions (self-test)",

                // Sized here rather than left to the window's own default,
                // because a grid row only realizes where there is height for it:
                // crushed to a few pixels the DataGrid would draw no cells at
                // all, and "no cell realized" is the same reading as "the cell
                // binding is broken". The test needs room to be able to tell
                // those two apart.
                Width = 1400,
                Height = 900,
            };

            bulkWindow.UseDebugFixture();
            bulkWindow.Show();

            // Layout has to happen before there are any cells to inspect, so the
            // verdict is queued behind it rather than read straight after Show.
            bulkWindow.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                bulkWindow.CheckGrid();
                Shutdown();
            }));

            return;
        }
#endif

        Exit += async (_, _) =>
        {
            AppLog.Info("Exiting.");
            if (_panopto is not null) await _panopto.DisposeAsync();
        };

        window.Show();

        // The update check runs only on a real window. A fixture touches no
        // network by design, and this check is a network call; the guard is
        // here rather than inside the check because "is this a fixture" is a
        // question about the window, and it compiles away in Release builds,
        // where fixtures do not exist.
#if DEBUG
        if (!window.IsFixture)
#endif
        {
            _ = CheckForUpdateAsync(window);
        }

        // A way to find out whether logging and crash reporting work on a
        // machine that is not this one. Diagnostics that have never been
        // exercised are the ones that turn out to be wrong exactly when someone
        // needs them, and there is no other way to make a WPF app fail on
        // purpose. Posted through the dispatcher rather than thrown here, so it
        // travels the same path a real UI fault does.
        if (e.Args.Contains("--self-test-crash", StringComparer.OrdinalIgnoreCase))
        {
            AppLog.Info("Self-test crash requested.");
            Dispatcher.BeginInvoke(new Action(() =>
                throw new InvalidOperationException(
                    "Deliberate fault, raised by --self-test-crash to prove the crash "
                    + "handler writes a log. Nothing is wrong with the app.")));
        }
    }

    /// <summary>
    /// Asks the version feed whether a newer build has been published, and
    /// puts the update banner up if there is one.
    ///
    /// <para><b>Nothing about this may interrupt an operator.</b> The check is
    /// a courtesy about a version they already hold a working copy of:
    /// <see cref="VersionFeed.TryReadAsync"/> is written to swallow its own
    /// failures quietly, so reaching the catch here means something outside
    /// it did — the dispatcher call, say — and even that must not take the
    /// app down. That is the lesson of the review's async-void escapes,
    /// applied to every fire-and-forget path since: the catch is not
    /// decoration, it is the reason this method is safe to not await.</para>
    /// </summary>
    private static async Task CheckForUpdateAsync(MainWindow window)
    {
        try
        {
            var info = await VersionFeed.TryReadAsync().ConfigureAwait(false);

            if (info is not null && VersionFeed.IsNewer(info.LatestVersion, Version))
                window.Dispatcher.Invoke(() =>
                {
                    window.AnnounceUpdate(info.LatestVersion, Version);

                    // The banner is a UI state, and this is the line that makes
                    // it visible from outside the machine: an operator asking
                    // "why does my copy say there is an update" is answered from
                    // the log without a screen share. Logged after the banner
                    // is up, so the line means "shown", not just "noticed".
                    AppLog.Info($"An update is available: version {info.LatestVersion} (this copy is {Version}).");
                });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"The update check could not finish: {ex.Message}");
        }
    }

    /// <summary>
    /// Catches what would otherwise take the process down silently.
    ///
    /// <para>Without this an unhandled exception on the UI thread kills the app
    /// with a Windows crash dialog and leaves nothing behind — the user sees it
    /// vanish, and there is no artifact to send anyone. That handler ends the
    /// way the user can see: a line in the log file, and a message that names
    /// where the log is.</para>
    ///
    /// <para>The handler for exceptions on every other thread cannot promise the
    /// message. It runs on whatever thread faulted, and once it returns the
    /// process dies no matter what it does — the Windows crash dialog cannot be
    /// suppressed from there — so it writes the log line and nothing else, and
    /// a user who sees the app vanish under a foreign-thread fault has the log
    /// to send rather than a sentence to read. That is a difference in what the
    /// two handlers can do, not one to be papered over in the docs: this
    /// comment used to claim a message for every fatal case, and the handler
    /// below has never shown one.</para>
    /// </summary>
    private void HookCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            // Handled, so the message below is what the user sees rather than
            // the WER dialog underneath it. The app still closes: there is no
            // way to know how much state the exception left consistent.
            args.Handled = true;
            ReportAndExit(args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Error("Fatal exception outside the UI thread.",
                args.ExceptionObject as Exception);
        };

        // A faulted task nobody awaited: worth recording, not worth closing the
        // app over. Marked observed so it cannot escalate into a process kill.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }

#if DEBUG
    /// <summary>
    /// Routes WPF's data-binding complaints into <see cref="AppLog"/>, so a
    /// binding that cannot resolve leaves a trace instead of a blank control.
    /// </summary>
    private sealed class BindingLogListener : TraceListener
    {
        private readonly System.Text.StringBuilder _pending = new();

        public override void Write(string? message) => _pending.Append(message);

        public override void WriteLine(string? message)
        {
            _pending.Append(message);
            AppLog.Warn($"Binding: {_pending}");
            _pending.Clear();
        }
    }
#endif

    private static void ReportAndExit(Exception error)
    {
        AppLog.Error("Unhandled exception on the UI thread; closing.", error);

        try
        {
            FailureDialog.Show(
                "Panopto Scheduler hit an unexpected problem and has to close.",
                error,
                $"Panopto Scheduler {Version}");
        }
        catch
        {
            // If even the message box cannot be shown, the log is already
            // written and that is the part that matters.
        }

        Current.Shutdown(1);
    }
}
