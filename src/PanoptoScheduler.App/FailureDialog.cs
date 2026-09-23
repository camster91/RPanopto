using System.Windows;
using PanoptoScheduler.Core.Diagnostics;

namespace PanoptoScheduler.App;

/// <summary>
/// The one writer of the "a log was written — please send it on" box.
///
/// <para>Both ends of the failure spectrum show it — the unhandled-exception
/// handler that closes the app, and the last-line-of-defence catch inside
/// <see cref="ViewModels.AsyncRelayCommand"/> that does not — and they say the
/// same things about the log. Hand-building the text twice meant the two copies
/// could drift: when the log layout or the wording changes, one gets edited and
/// the operator sees two different stories about the same log.</para>
/// </summary>
internal static class FailureDialog
{
    /// <param name="leadIn">What happened, in one sentence — the only part that differs.</param>
    /// <param name="error">The exception; named in the box so the report can quote it.</param>
    /// <param name="title">The window title.</param>
    internal static void Show(string leadIn, Exception error, string title)
    {
        // The log's own fate decides which sentence is true. Promising a
        // written log while the writes are failing hands the operator a path
        // to a file that does not exist and asks them to send it — while the
        // one thing that would have said what went wrong is nowhere at all.
        // Saying so plainly is the honest alternative, and it is still
        // actionable: what they were doing is the other half of any report.
        var logLines = AppLog.LastWriteFailed
            ? "The log could not be written, so there is no file to send. The folder it\n"
              + $"would have gone to is:\n{AppLog.Directory}\n\n"
              + "Please describe what you were doing at the time instead."
            : "A log of what happened was written to:\n"
              + $"{AppLog.Directory}\n\n"
              + "Please send that file on, along with what you were doing at the time.";

        MessageBox.Show(
            $"{leadIn}\n\n{logLines}\n\n"
            + $"({error.GetType().Name}: {error.Message})",
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}