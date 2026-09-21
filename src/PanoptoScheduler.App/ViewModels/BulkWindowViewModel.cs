using System.ComponentModel;
using PanoptoScheduler.Core;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.App.ViewModels;

/// <summary>
/// Hosts the three bulk tools and gives them one status line.
///
/// <para>Each tool reports into its own status, and the wrapper forwards
/// whichever tab is on top — so a failure on the hidden tab cannot quietly
/// replace the message describing what the operator is actually looking
/// at.</para>
///
/// <para><b>Tab order is the order of the work.</b> Booking first, because that is
/// what the window is for; the file reader second, because it feeds the booking
/// grid rather than standing beside it; editing the selection last.</para>
/// </summary>
public sealed class BulkWindowViewModel : ObservableObject
{
    // 0 = book, 1 = import a file, 2 = edit the calendar's selection.
    public const int BookTab = 0;
    public const int ImportTab = 1;
    public const int EditTab = 2;

    public BulkWindowViewModel(
        PanoptoConnection panopto,
        Func<IReadOnlyList<SessionTarget>> selection,
        Action selectionConsumed)
    {
        Booking = new BookingViewModel(panopto);

        // The file reader's only way out is into the grid, which is what keeps
        // one preview discipline in the app rather than two.
        Import = new ImportViewModel(rows =>
        {
            var added = Booking.Grid.Add(rows);

            SelectedTab = BookTab;

            Booking.Grid.Announce(
                added == 1
                    ? "1 row read from the file, added to the grid."
                    : $"{added} rows read from the file, added to the grid.",
                "Preview before booking.");
        });

        Edit = new BulkEditViewModel(panopto, selection, selectionConsumed);

        // The grid, not the container: the container holds no status of its own,
        // so subscribing to it would forward nothing.
        Booking.Grid.PropertyChanged += ForwardFrom(BookTab);
        Import.PropertyChanged += ForwardFrom(ImportTab);
        Edit.PropertyChanged += ForwardFrom(EditTab);
    }

    public BookingViewModel Booking { get; }
    public ImportViewModel Import { get; }
    public BulkEditViewModel Edit { get; }

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value)) return;

            Raise(nameof(Status));
            Raise(nameof(Detail));
            Raise(nameof(DetailTooltip));

            // The calendar's ticks are not observable from here, so the edit tab
            // has to re-check them every time it comes to the front. The rooms are
            // read the first time the booking tab is, for the same reason: nothing
            // tells either of them that the thing they depend on has changed.
            if (value == EditTab) Edit.Refresh();
            if (value == BookTab) Booking.Load();
        }
    }
    private int _selectedTab;

    public string Status => SelectedTab switch
    {
        ImportTab => Import.Status,
        EditTab => Edit.Status,
        _ => Booking.Grid.Status,
    };

    public string Detail => SelectedTab switch
    {
        ImportTab => Import.Detail,
        EditTab => Edit.Detail,
        _ => Booking.Grid.Detail,
    };

    /// <summary>
    /// The whole exception chain behind <see cref="Detail"/>, forwarded the same
    /// way — so the tooltip belongs to the tab that is on top, matching the line
    /// it hangs off.
    /// </summary>
    public string DetailTooltip => SelectedTab switch
    {
        ImportTab => Import.DetailTooltip,
        EditTab => Edit.DetailTooltip,
        _ => Booking.Grid.DetailTooltip,
    };

    /// <summary>
    /// Called when the calendar's ticks change. The selection lives on the other
    /// view model, so the edit tab's commands have to be re-checked by hand —
    /// otherwise ticking a session leaves every button greyed out until the
    /// window is reopened.
    /// </summary>
    public void Refresh()
    {
        Edit.Refresh();
        Booking.Load();
    }

    /// <summary>
    /// Which of the three the property came from, or -1 for anything that is not
    /// one of the three status lines — of which there are many, because the booking
    /// half has a form on it.
    /// </summary>
    private PropertyChangedEventHandler ForwardFrom(int index)
        => (sender, e) =>
        {
            if (SelectedTab != index) return;

            var relevant = sender switch
            {
                ImportViewModel => e.PropertyName is nameof(ImportViewModel.Status)
                    or nameof(ImportViewModel.Detail)
                    or nameof(ImportViewModel.DetailTooltip),
                BulkEditViewModel => e.PropertyName is nameof(BulkEditViewModel.Status)
                    or nameof(BulkEditViewModel.Detail)
                    or nameof(BulkEditViewModel.DetailTooltip),
                _ => e.PropertyName is nameof(BookingGridViewModel.Status)
                    or nameof(BookingGridViewModel.Detail)
                    or nameof(BookingGridViewModel.DetailTooltip),
            };

            if (!relevant) return;

            Raise(nameof(Status));
            Raise(nameof(Detail));
            Raise(nameof(DetailTooltip));
        };
}
