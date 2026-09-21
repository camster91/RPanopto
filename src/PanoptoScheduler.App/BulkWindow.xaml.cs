using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PanoptoScheduler.App.ViewModels;
using PanoptoScheduler.Core.Diagnostics;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.App;

public partial class BulkWindow : Window
{
    private readonly BulkWindowViewModel _viewModel;

    public BulkWindow(BulkWindowViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // Reading the tenant's rooms is a network call, so it happens when the
        // window is actually opened rather than when the view model is built — the
        // self-test constructs this view model with nobody signed in, and a
        // constructor that reached for the network would make that test depend on
        // the tenant.
        //
        // The fixture marks the rooms loaded, so in a self-test this is a no-op.
        Loaded += (_, _) => _viewModel.Booking.Load();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

#if DEBUG
    /// <summary>The room list the self-test uses, so the picker has something to find.</summary>
    private static readonly string[] FixtureRooms =
    [
        "Event Space 214", "LM 1610", "RSM 1210", "RSM 1212", "RSM 1214",
    ];

    /// <summary>
    /// Fills the booking tab from a fixture rather than the tenant, for
    /// <c>--self-test-bulk</c>.
    ///
    /// <para>The dates are whatever the form already defaulted to, which comes from
    /// the room clock. Nothing here is booked, so a pattern that generated nothing
    /// would be a silent pass — which is why <see cref="CheckGrid"/> asserts on the
    /// rows it actually produced.</para>
    /// </summary>
    public void UseDebugFixture()
    {
        var pattern = _viewModel.Booking.Pattern;

        pattern.UseFixtureRooms(FixtureRooms);

        // Before the fixture's own pattern is built, so what it leaves behind —
        // a template in the picker and a form it filled — is overwritten by the
        // values below rather than fighting them.
        ExerciseTemplates(pattern);

        // Installed is not ticked. Without this the pattern has no rooms, so it
        // generates nothing — and an empty grid is exactly what a broken grid
        // binding also looks like, which would make this test lie in the
        // direction of "the app is fine". Ticking through the command rather
        // than the field also puts the room commands under the test.
        if (pattern.TickShownCommand.CanExecute(null)) pattern.TickShownCommand.Execute(null);

        pattern.TitleFormat = "{room} {date} {n}";

        foreach (var day in pattern.Weekdays)
            day.IsTicked = day.Day is DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday;

        pattern.StartText = "10:00 AM";
        pattern.EndText = "11:00 AM";

        // Not the command: this runs before the window is shown, and the command is
        // async void. Generate is the same code the command calls.
        var added = pattern.Generate(replace: true);

        // Said out loud, because a fixture that quietly generates nothing is
        // indistinguishable from a grid whose rows never reached a cell.
        if (added == 0)
        {
            AppLog.Warn(
                $"Self-test bulk: the fixture pattern generated no rows. {pattern.Status} "
                + $"{pattern.Detail} {string.Join(" | ", pattern.Problems)}");
        }

        _viewModel.Booking.Grid.Announce(
            added == 0
                ? "Self-test bulk: the fixture pattern generated no rows."
                : $"{added} row(s) added — {_viewModel.Booking.Grid.RowCount} in the grid now.",
            "Preview before booking.");
    }

    /// <summary>
    /// Drives the saved-templates strip without reading or writing the templates
    /// file.
    ///
    /// <para><b>Deliberately no disk.</b> <c>templates.json</c> belongs to whoever
    /// is running the app, and a self-test that saved over or deleted an entry in it
    /// would be the test destroying the thing it is testing — on the machine of the
    /// person who ran it to check the app was healthy. So the template is built in
    /// memory and only the path from the picker to the form is exercised, which is
    /// the part no Core fact can reach: ticking the weekdays, ticking the rooms by
    /// name through the real room list, and writing the two time boxes.</para>
    ///
    /// <para>Save and Delete are thin wrappers over <see cref="BookingTemplateStore"/>,
    /// which has its own facts; re-testing them here would mean writing the
    /// operator's file to prove something already proved.</para>
    /// </summary>
    private static void ExerciseTemplates(BookingPatternViewModel pattern)
    {
        var template = new BookingTemplate
        {
            Name = "Self-test pattern",
            Recorders = [.. FixtureRooms],
            Weekdays = [DayOfWeek.Tuesday, DayOfWeek.Thursday],
            Start = new TimeSpan(14, 30, 0),
            End = new TimeSpan(15, 45, 0),
            TitleFormat = "Self-test {room} {date}",
            SpanDays = 30,
        };

        pattern.Templates.Add(template);
        pattern.SelectedTemplate = template;
        pattern.LoadTemplate();

        var wrong = new List<string>();

        var weekdays = pattern.Weekdays.Where(d => d.IsTicked).Select(d => d.Day).ToList();

        if (!weekdays.SequenceEqual([DayOfWeek.Tuesday, DayOfWeek.Thursday]))
            wrong.Add($"the days came back as {string.Join(", ", weekdays)}");

        // Formatted through the form's own clock format, because that is what the
        // boxes show and what the reader has to be able to read back — a load that
        // wrote 14:30 into a box that says "2:30 PM" would fill the form in with
        // something the form itself would refuse.
        if (pattern.StartText != "2:30 PM") wrong.Add($"the start came back as \"{pattern.StartText}\"");
        if (pattern.EndText != "3:45 PM") wrong.Add($"the end came back as \"{pattern.EndText}\"");
        if (pattern.TitleFormat != "Self-test {room} {date}")
            wrong.Add("the recording name did not come across");

        // The rooms are the half that fails quietly: a name that does not match
        // leaves nothing ticked, and nothing ticked generates nothing — which is
        // the same picture a broken grid draws.
        var expectedRooms = $"{FixtureRooms.Length} of {FixtureRooms.Length} room(s) ticked";

        if (pattern.TickedRoomsSummary != expectedRooms)
            wrong.Add($"the rooms read \"{pattern.TickedRoomsSummary}\" rather than \"{expectedRooms}\"");

        if (pattern.Templates.Count == 0)
            wrong.Add("the picker was left empty, so nothing could be picked");

        if (wrong.Count == 0) return;

        // Said out loud rather than asserted: this runs before the window is shown,
        // where there is nobody to catch a throw, and a fixture that stopped here
        // would take the rest of the self-test with it.
        AppLog.Warn($"Self-test bulk: loading a template did not take — {string.Join("; ", wrong)}.");
    }

    /// <summary>
    /// Reports whether the grid's cells actually resolved, and warns when they did
    /// not.
    ///
    /// <para><b>This exists because a mis-scoped binding is invisible.</b> The Room
    /// picker reaches past the row's own data context up to the window for the room
    /// list. Written as a plain <c>{Binding RoomNames}</c> that compiles, resolves to
    /// nothing, and gives an empty dropdown that looks like a tenant with no rooms.
    /// It can only be caught once the cell template is realized, and the debug
    /// binding listener that would catch it as a warning only fires then — so this
    /// draws the grid on purpose and reads back what the ComboBox actually got.</para>
    ///
    /// <para>The grid's own column bindings are checked the same way, by looking for
    /// a cell carrying the first row's title: a title in the model that never
    /// reaches a cell is the same class of silent wrongness.</para>
    /// </summary>
    public void CheckGrid()
    {
        BookingRows.UpdateLayout();

        CheckTemplatePicker();

        var rows = _viewModel.Booking.Grid.Rows;

        // Two different faults, said differently. An empty model is this fixture's
        // problem and reporting it as a binding fault would send the next person
        // looking through XAML for a bug that is in the fixture.
        if (rows.Count == 0)
        {
            AppLog.Warn(
                "Self-test bulk: the model holds no rows, so there is nothing to draw. "
                + "That is the fixture, not the grid — see the warning the fixture wrote.");
            return;
        }

        if (BookingRows.Items.Count != rows.Count)
        {
            AppLog.Warn(
                $"Self-test bulk: the grid drew {BookingRows.Items.Count} row(s) for "
                + $"{rows.Count} in the model, so the rows binding did not resolve.");
            return;
        }

        var pickers = Descendants<ComboBox>(BookingRows).ToList();

        var wired = pickers.FirstOrDefault(c =>
            c.ItemsSource is System.Collections.IEnumerable source
            && source.Cast<object>().Count() == FixtureRooms.Length);

        var titles = Descendants<TextBlock>(BookingRows).Select(t => t.Text).ToHashSet();

        var titleShown = titles.Contains(rows[0].Title);

        var summary =
            $"Self-test bulk: grid drew {BookingRows.Items.Count} row(s); "
            + $"{pickers.Count} room picker(s) realized, "
            + (wired is not null
                ? $"one holding {FixtureRooms.Length} room(s)"
                : "none holding the room list")
            + $"; a cell {(titleShown ? "carries" : "does not carry")} \"{rows[0].Title}\".";

        if (wired is not null && titleShown)
        {
            AppLog.Info(summary);
            return;
        }

        var why = pickers.Count == 0
            ? " Not one room picker was realized, so the grid drew no cells at all — "
              + "which is the grid having no room to draw in, not a binding that failed."
            : " The Room picker reaches its list by walking up to the window's data "
              + "context, and the Title column binds straight through — a binding that "
              + "resolves to nothing renders as an empty cell with no error of its own.";

        AppLog.Warn(summary + why);
    }

    /// <summary>
    /// Reports whether the template picker resolved, which is the one binding in
    /// the templates strip a live layout is needed to see.
    ///
    /// <para>It is checked for the same reason the grid's cells are: a binding that
    /// resolves to nothing draws an empty dropdown, and an empty dropdown is
    /// exactly what a machine with no saved templates looks like. The fixture puts
    /// one template in, so on a machine with none the difference is visible.</para>
    /// </summary>
    private void CheckTemplatePicker()
    {
        var pattern = _viewModel.Booking.Pattern;

        var items = TemplatePicker.Items.Count;
        var selected = TemplatePicker.SelectedItem;

        var summary =
            $"Self-test bulk: the template picker holds {items} item(s) for "
            + $"{pattern.Templates.Count} in the model, and "
            + (selected is null ? "nothing is picked" : $"\"{selected}\" is picked") + ".";

        if (items == pattern.Templates.Count && selected is not null)
        {
            AppLog.Info(summary);
            return;
        }

        AppLog.Warn(
            summary + " The picker binds straight through to the view model, and the "
            + "fixture puts a template in before the window is shown — so an empty "
            + "picker here is the ItemsSource or SelectedItem binding not resolving.");
    }

    /// <summary>Every descendant of the given type, depth first.</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match) yield return match;

            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
#endif

    /// <summary>
    /// Escape closes, which the window had no way to do except the title bar.
    ///
    /// <para>This is done here rather than with <c>IsCancel="True"</c> because the
    /// window is opened with <see cref="Window.Show"/> and not
    /// <see cref="Window.ShowDialog"/>: a cancel button sets <c>DialogResult</c>,
    /// and setting that on a non-modal window throws. The throw would reach the
    /// dispatcher handler, which closes the application — so the shortcut for
    /// "never mind" would have quit the program.</para>
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnPreviewKeyDown(e);
    }
}
