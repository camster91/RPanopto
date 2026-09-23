using System.Windows;
using PanoptoScheduler.Core.Configuration;

namespace PanoptoScheduler.App;

/// <summary>
/// First-run setup. Collects the tenant URL and OAuth client details so nobody
/// has to hand-place a file in a user profile.
/// </summary>
public partial class CredentialsDialog : Window
{
    /// <summary>The default every Rotman user needs, so most people change none of it.</summary>
    private const string DefaultTenant = "https://rotman.ca.panopto.com";

    /// <summary>
    /// The credentials as loaded, kept so Save can carry the fields this dialog
    /// does not edit rather than resetting them.
    /// </summary>
    private readonly PanoptoCredentials? _existing;

    public CredentialsDialog(PanoptoCredentials? existing = null, string? initialError = null)
    {
        InitializeComponent();

        _existing = existing;

        TenantBox.Text = existing?.TenantUrl ?? DefaultTenant;
        ClientIdBox.Text = existing?.ClientId ?? string.Empty;
        ClientSecretBox.Password = existing?.ClientSecret ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(initialError)) ShowError(initialError);

        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(ClientIdBox.Text)) ClientIdBox.Focus();
            else ClientSecretBox.Focus();
        };
    }

    /// <summary>Valid once the dialog closes with Save.</summary>
    public PanoptoCredentials? Credentials { get; private set; }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var tenant = TenantBox.Text.Trim().TrimEnd('/');
        var clientId = ClientIdBox.Text.Trim();
        var secret = ClientSecretBox.Password;

        if (tenant.Length == 0 || clientId.Length == 0 || secret.Length == 0)
        {
            ShowError("Fill in all three fields.");
            return;
        }

        if (!Uri.TryCreate(tenant, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            ShowError("The Panopto site must be a full URL, for example https://rotman.ca.panopto.com");
            return;
        }

        // TimeZone and RedirectUri are not editable here, so carry them over
        // from whatever loaded — including the partially-loaded values shown
        // above — rather than resetting a hand-edited credentials.json to the
        // compile-time defaults. A reset TimeZone is load-bearing: writes name
        // the instant, so the wrong zone books rooms hours off with no error.
        var credentials = new PanoptoCredentials
        {
            TenantUrl = tenant,
            ClientId = clientId,
            ClientSecret = secret,
            TimeZone = _existing?.TimeZone ?? Core.Scheduling.RoomClock.DefaultZoneId,
            RedirectUri = _existing?.RedirectUri ?? "http://localhost:51820/oauth/callback",
        };

        try
        {
            CredentialStore.Save(credentials);
        }
        catch (Exception ex)
        {
            // Saving is the whole point of the dialog, so a failure here is
            // shown rather than swallowed.
            ShowError($"Could not save to {CredentialStore.DefaultPath}: {ex.Message}");
            return;
        }

        Credentials = credentials;
        DialogResult = true;
    }

    private void ShowError(string message) => ErrorText.Text = message;
}
