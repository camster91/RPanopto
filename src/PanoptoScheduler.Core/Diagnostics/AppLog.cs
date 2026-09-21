using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PanoptoScheduler.Core.Diagnostics;

/// <summary>
/// A plain append-only log file, so a problem on someone else's machine can be
/// read rather than described over the phone.
///
/// <para>Written to <c>%USERPROFILE%\.panopto-scheduler\logs\</c>, one file per
/// day, beside the credentials. There is deliberately no logging framework here:
/// the failure this exists for is the one that takes the process down, and a
/// logger that can itself throw is worse than none. Every write therefore sits
/// inside a <c>catch</c> that swallows, and lines are appended rather than
/// rewritten so a crash mid-write cannot damage earlier entries.</para>
///
/// <para><b>Nothing secret passes through here.</b> Credentials and tokens have
/// their own stores — see <see cref="Configuration.CredentialStore"/> and
/// <see cref="Auth.DpapiTokenStore"/> — and this log names files and types, not
/// their contents.</para>
/// </summary>
public static class AppLog
{
    /// <summary>Days a log file is kept before startup deletes it.</summary>
    private const int RetentionDays = 30;

    private static readonly object Gate = new();

    /// <summary>
    /// Where the log goes instead of the real directory. Null in the app, which
    /// is the only case an operator ever sees.
    /// </summary>
    /// <remarks>
    /// This exists for the test suite, and the reason is specific enough to
    /// write down. Hitting the paging ceiling is a tested behaviour — a tenant
    /// that ignores <c>PageNumber</c> must stop at <c>MaxPages</c> rather than
    /// loop — and <see cref="Warn"/> is how <c>SoapPaging</c> reports it. Run
    /// against the default directory, a <i>passing</i> test therefore writes
    /// "stopped at the 60-page ceiling after 15000 item(s)" into the operator's
    /// own log, where it reads exactly like the tenant truncating a listing and
    /// quietly dropping bookings past the cut. That is the one file they are
    /// asked to send when something looks wrong, so a line a passing test can
    /// produce has to land somewhere else.
    /// </remarks>
    public static string? RedirectDirectory { get; set; }

    /// <summary><c>~/.panopto-scheduler/logs</c>, or wherever a test pointed it.</summary>
    public static string Directory
        => RedirectDirectory ?? Path.Combine(Configuration.CredentialStore.Directory, "logs");

    /// <summary>Today's file, so a support call asks for one predictable name.</summary>
    public static string CurrentFile => Path.Combine(Directory,
        $"app-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? error = null)
        => Write("ERROR", error is null ? message : $"{message}{Environment.NewLine}{Full(error)}");

    private static void Write(string level, string message)
    {
        var line = string.Format(CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}", DateTime.Now, level, message);

        // Also to the debugger: with one attached the output is immediate, and
        // the file is for the case where nobody can attach.
        Debug.WriteLine(line);

        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(CurrentFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // A log that cannot be written must not be what fails the app.
        }
    }

    /// <summary>
    /// The whole exception chain, deepest included. The outermost exception is
    /// usually a wrapper — "one or more errors occurred" — and the line worth
    /// reading is underneath it.
    ///
    /// <para>Public because it is what <see cref="ProblemText.Summarise"/> is the
    /// summary *of*. A UI that shows the one-line version has to be able to hand
    /// the whole thing over as well — a tooltip, or a "copy the detail" — or the
    /// friendly sentence becomes a dead end, which is a worse failure than the
    /// raw message it replaced.</para>
    ///
    /// <para>Named <c>Full</c> rather than <c>Describe</c> because it is now read
    /// from outside: the call site reads "the full text of this exception",
    /// which is the thing being asked for.</para>
    /// </summary>
    public static string Full(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var text = new StringBuilder();

        for (var current = error; current is not null; current = current.InnerException)
        {
            text.AppendLine($"{current.GetType().FullName}: {current.Message}");
            if (current.StackTrace is { } stack) text.AppendLine(stack);
            if (current.InnerException is not null) text.AppendLine("  --- inner ---");
        }

        return text.ToString();
    }

    /// <summary>
    /// Deletes logs past the retention window. Called once at startup rather
    /// than on every write, so a long session does not pay for it repeatedly.
    /// </summary>
    public static void Prune()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;

            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch
        {
            // Housekeeping only; never worth surfacing.
        }
    }
}
