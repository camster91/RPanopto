namespace PanoptoScheduler.Core.Configuration;

/// <summary>
/// Credentials that ship beside the app, so the first run asks nobody for
/// anything.
///
/// <para><b>Why a file and not a prompt.</b> Without this the first launch on
/// every machine stops at a dialog asking for a tenant URL, a client id and a
/// client secret — three things a person setting up their laptop has no way to
/// know, and which arrive by email if they arrive at all. With this, launching
/// the app goes straight to the calendar and the only question asked is the
/// one that matters: sign in as yourself.</para>
///
/// <para><b>The secret is in here, and that is unavoidable.</b> Panopto has no
/// public-client support — see <see cref="Auth.OAuthOptions"/> — so a desktop
/// app cannot hold a secret secretly, whatever we do. It is kept out of the
/// repository and out of the build, and copied in at packaging time from the
/// packager's own credentials file; the zip that carries it goes only to the
/// team. What it grants is the ability to <i>ask</i> for a sign-in, not access
/// to anything: every user still authenticates as themselves, and the token
/// that comes back is encrypted per-user with DPAPI.</para>
///
/// <para>A person's own <c>credentials.json</c> takes precedence over this, so
/// an admin can point one machine at a different tenant or roll the secret
/// without repackaging the app.</para>
/// </summary>
public static class ShippedDefaults
{
    /// <summary>The file's name, beside <c>PanoptoScheduler.App.exe</c>.</summary>
    public const string FileName = "defaults.json";

    /// <summary>
    /// Resolved against the executable's own folder, not the working directory:
    /// the app is launched by double-click, from a shortcut, and from a
    /// shortcut with a different "start in", and all three must find it.
    /// </summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>
    /// The shipped credentials, or <see langword="null"/> when the file is not
    /// there — which is the normal case for a development build.
    ///
    /// <para>A file that is present but unreadable throws, exactly as the user's
    /// own file does. Falling back to the dialog would be friendlier and worse:
    /// it would hide a packaging mistake, and the app would then behave
    /// differently on one machine for no visible reason.</para>
    /// </summary>
    public static PanoptoCredentials? TryLoad(string? path = null)
    {
        var file = path ?? DefaultPath;
        return File.Exists(file) ? CredentialStore.Load(file) : null;
    }
}
