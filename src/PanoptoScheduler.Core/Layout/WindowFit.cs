namespace PanoptoScheduler.Core.Layout;

/// <summary>
/// The size, and the smallest size, a window should open at on the desktop it
/// is opening on.
///
/// <para><b>Why this exists.</b> The main window asked for 1420 by 840 with a
/// minimum of 900 by 560, all in device-independent units and none of them aware
/// of the screen. Those sizes are in the XAML and stayed correct only on the
/// machine they were chosen on. Every size in WPF is scaled by the display's
/// DPI, so on a 1920x1080 laptop at 150% the desktop is about 1280 by 672 units
/// and the window opens larger than the screen it is on; at 200% the desktop is
/// about 960 by 520 and the <i>minimum</i> height alone is 560, so the window
/// cannot be made to fit no matter what the user does. That is the literal
/// complaint: "so can use it on any computer".</para>
///
/// <para><b>Both numbers move, not just the size.</b> Shrinking the requested
/// size is not sufficient, because WPF coerces a window up to its minimum: a
/// window set to 472 units tall with a minimum of 560 comes back at 560, which
/// is the original defect with extra steps. The minimum has to come down with
/// the screen.</para>
///
/// <para><b>In Core because it is arithmetic.</b> There is no reference from
/// the test project to the WPF app, so a fit that has to be proved rather than
/// squinted at belongs here — and "the window fits the screen" is a property
/// with a right answer, not a matter of taste.</para>
/// </summary>
/// <param name="Width">The width to open at.</param>
/// <param name="Height">The height to open at.</param>
/// <param name="MinWidth">The smallest width to allow.</param>
/// <param name="MinHeight">The smallest height to allow.</param>
public readonly record struct WindowFit(double Width, double Height, double MinWidth, double MinHeight)
{
    /// <summary>
    /// The gap left between the window and the edges of the desktop.
    ///
    /// <para>Not zero, because a window sized exactly to the work area sits
    /// flush against the taskbar and both screen edges, which reads as a window
    /// that has been maximized rather than one that was placed.</para>
    /// </summary>
    public const double DefaultMargin = 24;

    /// <summary>
    /// Fits a preferred size to a desktop.
    ///
    /// <para>Clamped, never scaled: a window is not a picture, and shrinking it
    /// in proportion to the screen would take a 1420-wide window down to 640 on
    /// a small screen and make it useless rather than merely small.</para>
    /// </summary>
    /// <param name="workWidth">
    /// The desktop's work area — the screen minus the taskbar and any docked
    /// appbars. <see cref="System.Windows.SystemParameters.WorkArea"/> already
    /// reports this in device-independent units, so no DPI arithmetic is done
    /// here and none should be added.
    /// </param>
    /// <param name="workHeight">The same, vertically.</param>
    /// <param name="preferredWidth">
    /// The size the window would like to be, which is what the XAML asks for.
    /// Passed in rather than repeated here so the two cannot drift apart.
    /// </param>
    /// <param name="preferredHeight">The same, vertically.</param>
    /// <param name="preferredMinWidth">The smallest width the window wants to allow.</param>
    /// <param name="preferredMinHeight">The smallest height the window wants to allow.</param>
    /// <param name="margin">See <see cref="DefaultMargin"/>.</param>
    public static WindowFit Fit(
        double workWidth,
        double workHeight,
        double preferredWidth,
        double preferredHeight,
        double preferredMinWidth,
        double preferredMinHeight,
        double margin = DefaultMargin)
    {
        var usableWidth = workWidth - (margin * 2);
        var usableHeight = workHeight - (margin * 2);

        // A desktop that cannot be measured, or one so small that the margin
        // leaves nothing, is answered with the preferred size unchanged. The
        // first layout pass really does report zero, so this is a case that
        // happens rather than a defensive branch — and a window the size it
        // always was is a better answer than one with a width of zero, which
        // renders as nothing at all and raises no error anywhere.
        if (!IsUsable(usableWidth) || !IsUsable(usableHeight))
        {
            return new WindowFit(preferredWidth, preferredHeight, preferredMinWidth, preferredMinHeight);
        }

        var minWidth = Smallest(preferredMinWidth, usableWidth);
        var minHeight = Smallest(preferredMinHeight, usableHeight);

        // The minimums are applied to the sizes as well, so the returned pair is
        // internally consistent. WPF coerces in one direction only — a window
        // narrower than its minimum is widened — so a caller that assigns these
        // in either order ends up with a window that fits.
        return new WindowFit(
            Math.Max(minWidth, Math.Min(MeasuredOr(preferredWidth, usableWidth), usableWidth)),
            Math.Max(minHeight, Math.Min(MeasuredOr(preferredHeight, usableHeight), usableHeight)),
            minWidth,
            minHeight);
    }

    /// <summary>The smaller of a preference and what the desktop will allow.</summary>
    private static double Smallest(double preferred, double limit) =>
        Math.Min(MeasuredOr(preferred, limit), limit);

    /// <summary>A preference that was never measured becomes the limit itself.</summary>
    private static double MeasuredOr(double preferred, double fallback) =>
        IsUsable(preferred) ? preferred : fallback;

    private static bool IsUsable(double value) =>
        value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
}
