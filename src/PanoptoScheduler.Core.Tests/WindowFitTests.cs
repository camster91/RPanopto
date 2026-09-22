using PanoptoScheduler.Core.Layout;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// The window's fit to the desktop, which is the arithmetic behind "so can use
/// it on any computer".
///
/// <para>The reported defect was that the app opens larger than the screen on a
/// scaled display and cannot be shrunk to fit. Both halves of that are
/// properties with a right answer, so both are asserted here rather than
/// checked by eye on the one machine this was written on.</para>
/// </summary>
public class WindowFitTests
{
    // What MainWindow.xaml asks for. Repeated as the test's input rather than
    // read from the XAML, because the point of these cases is the arithmetic on
    // a known input; if the XAML changes, these numbers should be revisited
    // deliberately rather than tracked automatically.
    private const double PreferredWidth = 1420;
    private const double PreferredHeight = 840;
    private const double PreferredMinWidth = 900;
    private const double PreferredMinHeight = 560;

    private static WindowFit Fit(double workWidth, double workHeight) =>
        WindowFit.Fit(workWidth, workHeight, PreferredWidth, PreferredHeight, PreferredMinWidth, PreferredMinHeight);

    /// <summary>
    /// A desktop in device-independent units, which is what WPF reports.
    ///
    /// <para>The scale factor matters more than the panel resolution: every size
    /// in WPF is divided by it, so a 1920x1080 screen at 200% offers about
    /// 960x520 units and is the tightest case in the field. The work area is
    /// taken as the screen less a 40-unit taskbar.</para>
    /// </summary>
    private static (double Width, double Height) Desktop(double pixelsWide, double pixelsHigh, double scale)
    {
        const double Taskbar = 40;
        return (pixelsWide / scale, (pixelsHigh / scale) - Taskbar);
    }

    /// <summary>
    /// The reported symptom, as a fact: on a 1920x1080 laptop at 150% the
    /// window used to be requested at 1420x840 in a desktop of about 1280x680,
    /// so it opened hanging off the screen.
    /// </summary>
    [Fact]
    public void A_scaled_laptop_gets_a_window_that_fits_it()
    {
        var (width, height) = Desktop(1920, 1080, 1.5);
        var fit = Fit(width, height);

        Assert.True(fit.Width <= width, $"asked for {fit.Width} in a desktop {width} wide");
        Assert.True(fit.Height <= height, $"asked for {fit.Height} in a desktop {height} tall");
        Assert.True(fit.Width < PreferredWidth, "the requested width should have been reduced");
    }

    /// <summary>
    /// The half that is easy to miss. At 200% the desktop is about 960x500
    /// units, which is <i>shorter</i> than the 560 minimum the window used to
    /// declare — and WPF coerces a window up to its minimum, so reducing the
    /// requested size alone leaves it exactly as unfittable as before.
    /// </summary>
    [Fact]
    public void At_two_hundred_percent_the_minimum_comes_down_too()
    {
        var (width, height) = Desktop(1920, 1080, 2.0);
        var fit = Fit(width, height);

        Assert.True(height < PreferredMinHeight, "this case is only interesting while the desktop is shorter than the old minimum");
        Assert.Equal(height - (WindowFit.DefaultMargin * 2), fit.MinHeight, 3);
        Assert.True(fit.MinHeight <= fit.Height);
    }

    /// <summary>
    /// Every desktop a work machine plausibly has, with the two properties that
    /// have to hold on all of them. Cheap to state, and it is the version of
    /// this test that survives someone changing the constants.
    /// </summary>
    [Theory]
    [InlineData(3840, 2160, 1.0)]  // 4K, native
    [InlineData(3840, 2160, 1.5)]  // 4K, Windows' recommended scaling
    [InlineData(2560, 1440, 1.0)]
    [InlineData(2560, 1440, 1.25)]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(1920, 1080, 1.25)]
    [InlineData(1920, 1080, 1.5)]  // the common laptop, and the reported case
    [InlineData(1920, 1080, 2.0)]  // the tightest case in the field
    [InlineData(1600, 900, 1.0)]
    [InlineData(1366, 768, 1.0)]   // older laptops, and RDP at native
    [InlineData(1280, 720, 1.0)]
    public void It_fits_the_desktop_and_can_be_shrunk_to_fit(double pixelsWide, double pixelsHigh, double scale)
    {
        var (width, height) = Desktop(pixelsWide, pixelsHigh, scale);
        var fit = Fit(width, height);

        // 1. It opens on screen.
        Assert.True(fit.Width <= width, $"{pixelsWide}x{pixelsHigh} @{scale}: width {fit.Width} exceeds {width}");
        Assert.True(fit.Height <= height, $"{pixelsWide}x{pixelsHigh} @{scale}: height {fit.Height} exceeds {height}");

        // 2. It can still be resized smaller. A minimum larger than the size it
        //    opened at is a window with no room left to give, and a minimum
        //    larger than the desktop is one that cannot be made to fit at all.
        Assert.True(fit.MinWidth <= fit.Width, $"{pixelsWide}x{pixelsHigh} @{scale}: minimum width {fit.MinWidth} exceeds width {fit.Width}");
        Assert.True(fit.MinHeight <= fit.Height, $"{pixelsWide}x{pixelsHigh} @{scale}: minimum height {fit.MinHeight} exceeds height {fit.Height}");
        Assert.True(fit.MinWidth <= width, $"{pixelsWide}x{pixelsHigh} @{scale}: minimum width {fit.MinWidth} exceeds the desktop");
        Assert.True(fit.MinHeight <= height, $"{pixelsWide}x{pixelsHigh} @{scale}: minimum height {fit.MinHeight} exceeds the desktop");

        // 3. And it is never asked to exceed what the desktop has.
        Assert.True(fit.Width > 0 && fit.Height > 0, $"{pixelsWide}x{pixelsHigh} @{scale}: a window of {fit.Width}x{fit.Height} renders as nothing");
    }

    /// <summary>
    /// A desktop with room to spare is left exactly as the XAML asks for it.
    /// The clamp is not a resize.
    /// </summary>
    [Fact]
    public void A_desktop_with_room_to_spare_gets_the_preferred_size_unchanged()
    {
        var fit = Fit(3800, 2100);

        Assert.Equal(PreferredWidth, fit.Width, 3);
        Assert.Equal(PreferredHeight, fit.Height, 3);
        Assert.Equal(PreferredMinWidth, fit.MinWidth, 3);
        Assert.Equal(PreferredMinHeight, fit.MinHeight, 3);
    }

    /// <summary>
    /// The first layout pass reports a work area of zero, and an unmeasured
    /// desktop arrives as NaN. Both are answered with the preferred size rather
    /// than with a window of no size, which draws nothing and reports no error.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(double.NaN, double.NaN)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity)]
    public void An_unmeasurable_desktop_leaves_the_preferred_size_alone(double workWidth, double workHeight)
    {
        var fit = Fit(workWidth, workHeight);

        Assert.Equal(PreferredWidth, fit.Width, 3);
        Assert.Equal(PreferredHeight, fit.Height, 3);
        Assert.Equal(PreferredMinWidth, fit.MinWidth, 3);
        Assert.Equal(PreferredMinHeight, fit.MinHeight, 3);
    }

    /// <summary>
    /// The margin is a gap, so it comes off both sides — and it is what stops a
    /// fitted window from sitting flush against the taskbar, which reads as a
    /// window that was maximized rather than placed.
    /// </summary>
    [Fact]
    public void The_margin_is_taken_from_both_edges()
    {
        var fit = Fit(1000, 800);

        Assert.Equal(1000 - (WindowFit.DefaultMargin * 2), fit.Width, 3);
        Assert.Equal(800 - (WindowFit.DefaultMargin * 2), fit.Height, 3);
    }
}
