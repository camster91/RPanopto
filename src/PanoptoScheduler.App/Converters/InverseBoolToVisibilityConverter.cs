using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PanoptoScheduler.App.Converters;

/// <summary>
/// Visible when the bound <see cref="bool"/> is <b>false</b>.
///
/// <para><b>WPF ships no such converter.</b> There is a
/// <see cref="BooleanToVisibilityConverter"/> and no inverse of it, which is a
/// gap that gets filled the same wrong way every time: a second view-model
/// property whose only job is to be the negation of the first. Two properties
/// that must agree, written by hand, in a class that already has enough of those —
/// and the day they disagree is the day one panel shows two contradictory
/// sentences at once.</para>
///
/// <para>This is the negation expressed once, in the binding, where it cannot
/// drift. It is deliberately not a general-purpose inverter: it renders
/// <see cref="Visibility"/>, and nothing in this app wants the inverse of a bool
/// for any other purpose.</para>
///
/// <para>Unset is collapsed, not visible. A binding still resolving, or one that
/// resolved to nothing, has not established falseness — and the elements this is
/// used on all say something like "no link was reported", which would be a
/// confident claim about a session while the panel was still reading it.</para>
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is false ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException(
            "One-way by design: nothing in this app sets a bool from whether " +
            "an element is on screen.");
}
