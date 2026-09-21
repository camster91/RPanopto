using System.ComponentModel;
using System.Windows;
using PanoptoScheduler.App.ViewModels;

namespace PanoptoScheduler.App;

/// <summary>
/// Asks the operator to sign in, and stays open until the tenant accepts it.
///
/// <para>Exists because a saved session can be absent or expire, and both leave
/// an empty calendar. Being told why — and given the button — is the difference
/// between that reading as "nothing to show" and "something is wrong".</para>
/// </summary>
public partial class SignInDialog : Window
{
    private readonly CalendarViewModel _viewModel;

    public SignInDialog(CalendarViewModel viewModel, string reason)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // Set here rather than bound: the reason belongs to this prompt, not to
        // the calendar behind it.
        ReasonText.Text = reason;

        _viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => _viewModel.PropertyChanged -= OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.IsBusy))
            SignInButton.IsEnabled = !_viewModel.IsBusy;
    }

    private async void OnSignInClick(object sender, RoutedEventArgs e)
    {
        // Disabled up front: a second click would start a second browser
        // sign-in against the same loopback port and the first would lose its
        // redirect.
        SignInButton.IsEnabled = false;
        try
        {
            if (await _viewModel.SignInAsync())
            {
                DialogResult = true;
                return;
            }
        }
        finally
        {
            // Not re-enabled on success — the window is on its way out, and the
            // status line and reason above already say what went wrong if it
            // did not.
            if (DialogResult is null) SignInButton.IsEnabled = true;
        }
    }
}
