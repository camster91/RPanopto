namespace PanoptoScheduler.Core.Auth;

/// <summary>
/// Raised when the interactive sign-in's authorization window ran out before
/// the browser step handed the redirect back.
///
/// <para><b>Why a type, rather than letting the cancellation propagate.</b> The
/// window expiring arrives as a bare <see cref="OperationCanceledException"/>,
/// which a summariser can only read as a timeout — "Panopto did not answer in
/// time… the server busy — try again." That sends someone to check a network
/// that was never consulted: nothing had been asked of Panopto yet, and the
/// browser tab they forgot about is still open. The failure is the sign-in
/// never being finished, and the person who can fix that is the one holding
/// the mouse.</para>
/// </summary>
/// <param name="window">How long the sign-in waited before giving up.</param>
public sealed class SignInWindowExpiredException(TimeSpan window)
    : Exception($"Sign-in was not completed within the {window} window.")
{
    /// <summary>How long the sign-in waited before giving up.</summary>
    public TimeSpan Window { get; } = window;
}