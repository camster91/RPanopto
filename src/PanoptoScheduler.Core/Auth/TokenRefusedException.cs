using System.Net;

namespace PanoptoScheduler.Core.Auth;

/// <summary>
/// The token endpoint answered and refused the grant itself — a 400 or 401,
/// the two statuses RFC 6749 §5.2 reserves for "this grant or this client is
/// not valid" (<c>invalid_grant</c>, <c>invalid_client</c> and their kin).
///
/// <para><b>Why a type of its own.</b> It is the one token failure that
/// licenses throwing a saved session away. A refresh token the server has
/// refused will be refused again, so it is dropped rather than re-sent once per
/// row. Every other failure — a 5xx while Panopto is in maintenance, a 429, a
/// dropped connection, a body that would not parse — says nothing about the
/// token, and clearing the DPAPI cache on one of those turns an hour of
/// Panopto downtime into a browser sign-in for everyone who was working
/// through it. The refresh path keys its sign-out off this type and nothing
/// broader, so a new kind of failure cannot acquire that behaviour by
/// accident.</para>
///
/// <para>Derives from <see cref="InvalidOperationException"/> because that is
/// what a refused token request has always thrown, and the message is
/// unchanged.</para>
/// </summary>
public sealed class TokenRefusedException(HttpStatusCode status, string body)
    : InvalidOperationException(
        $"Panopto rejected the token request ({(int)status}). " +
        $"Verify the client secret and that the redirect URL is registered exactly. {body}")
{
    public HttpStatusCode Status { get; } = status;
}
