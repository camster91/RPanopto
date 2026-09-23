using PanoptoScheduler.Core.Layout;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// The palette's whole job is that two rooms on screen do not look the same, and
/// that a room does not change colour when the view refreshes. Both are
/// properties of the assignment rather than of the drawing, so both are testable
/// without a window.
/// </summary>
public class RecorderPaletteTests
{
    [Fact]
    public void DistinctRecordersGetDistinctColours()
    {
        var rooms = Enumerable.Range(1, RecorderPalette.Size)
            .Select(i => $"Rotman {1000 + i}")
            .ToList();

        var assigned = RecorderPalette.Assign(rooms);

        Assert.Equal(RecorderPalette.Size, assigned.Count);
        Assert.Equal(RecorderPalette.Size, assigned.Values.Distinct().Count());
    }

    [Fact]
    public void PastThePaletteSizeItWrapsRatherThanRunningOut()
    {
        var rooms = Enumerable.Range(1, RecorderPalette.Size + 3)
            .Select(i => $"Rotman {1000 + i}")
            .ToList();

        var assigned = RecorderPalette.Assign(rooms);

        // Every room has a colour; the thirteenth shares one. Sharing is the
        // failure this guards against being a *crash*, not a duplicate.
        Assert.Equal(RecorderPalette.Size + 3, assigned.Count);
        Assert.All(assigned.Values, colour => Assert.Contains(colour, RecorderPalette.Colours));
    }

    [Fact]
    public void ASpreadOfARecordersRoomsAllDiffer()
    {
        // The count that used to wrap: six is the old palette, and a building
        // with eight rooms had two pairs sharing stripes.
        var rooms = new[]
        {
            "Rotman 1050", "Rotman 1060", "Rotman 1070", "Rotman 1080",
            "Rotman 1090", "Rotman 2000", "Rotman 2010", "Rotman 2020",
        };

        var assigned = RecorderPalette.Assign(rooms);

        Assert.Equal(rooms.Length, assigned.Values.Distinct().Count());
    }

    [Fact]
    public void TheSameRoomsGetTheSameColoursWhateverOrderTheyArriveIn()
    {
        // Panopto returns a week's sessions in whatever order it likes, so this
        // is the difference between a stable calendar and one where every room
        // changes colour on refresh.
        var rooms = new[] { "Rotman 1050", "Rotman 3010", "Rotman 2010", "Rotman 1180" };

        var first = RecorderPalette.Assign(rooms);
        var second = RecorderPalette.Assign(rooms.Reverse());
        var third = RecorderPalette.Assign(["Rotman 2010", "Rotman 1180", "Rotman 1050", "Rotman 3010"]);

        foreach (var room in rooms)
        {
            Assert.Equal(first[room], second[room]);
            Assert.Equal(first[room], third[room]);
        }
    }

    [Fact]
    public void RecorderNamesAreMatchedIgnoringCase()
    {
        // The rest of the app compares recorder names case-insensitively, and a
        // week that returned the same room cased two ways would otherwise draw
        // it in two colours.
        var assigned = RecorderPalette.Assign(["Rotman 1050", "rotman 1050"]);

        Assert.Single(assigned);
    }

    [Fact]
    public void BlankNamesAreSkipped()
    {
        // A session with no recorder has no stripe to colour.
        var assigned = RecorderPalette.Assign(["Rotman 1050", "", "   "]);

        Assert.Single(assigned);
        Assert.True(assigned.ContainsKey("Rotman 1050"));
    }

    [Fact]
    public void NoColourIsRepeatedWithinAList()
    {
        // A palette with a duplicate entry in it would be invisible until two
        // rooms drew in the same stripe, so it is checked directly.
        Assert.Equal(RecorderPalette.Colours.Count, RecorderPalette.Colours.Distinct().Count());
    }

    /// <summary>
    /// The standard RGB-to-hue conversion, test-local on purpose: Core has no
    /// colour type to borrow it from, and the palette's own doc once claimed an
    /// interleave its list did not deliver, so the arithmetic is kept beside
    /// the assertion that uses it.
    /// </summary>
    private static double Hue(PaletteColour colour)
    {
        var r = colour.R / 255.0;
        var g = colour.G / 255.0;
        var b = colour.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        if (delta == 0) return 0;

        if (max == r) return 60 * (((g - b) / delta) % 6);
        if (max == g) return 60 * (((b - r) / delta) + 2);
        return 60 * (((r - g) / delta) + 4);
    }

    /// <summary>
    /// The shorter angular distance between two hues, so 350° and 10° count
    /// as neighbours the way they look.
    /// </summary>
    private static double WheelDistance(double a, double b)
    {
        var gap = Math.Abs(a - b);
        return gap > 180 ? 360 - gap : gap;
    }

    [Fact]
    public void ConsecutiveColoursAreFarApartOnTheWheelIncludingTheWrap()
    {
        // The interleave is the palette's documented design: two rooms drawn
        // side by side must never be two neighbouring hues, and the pair that
        // matters as much as any is the last entry back to the first, because
        // that is the pair the thirteenth room collides with. The doc on this
        // list once claimed "roughly 180°" while the real smallest gap was
        // 59°, so this reads the list and measures.
        var colours = RecorderPalette.Colours;
        var minimum = double.MaxValue;

        for (var i = 0; i < colours.Count; i++)
        {
            var gap = WheelDistance(Hue(colours[i]), Hue(colours[(i + 1) % colours.Count]));
            minimum = Math.Min(minimum, gap);
        }

        Assert.True(minimum >= 80,
            $"the closest consecutive hues are {minimum:0.#}° apart — a quarter turn is the promise");
    }

    [Fact]
    public void NoGreenIsHandedOutNextToTheRed()
    {
        // The one pair red-green colour blindness cannot separate at all.
        // "Red" and "green" are found by what the channels look like, not by
        // index — the earlier version of this test hardcoded two indices and
        // asserted the distance between the literals, which stayed green
        // through any order the list shipped.
        //
        // The red test demands red clearly dominant over green (by a margin,
        // not a hair) for two reasons: magenta is red-dominant but carries a
        // strong blue channel, and blue is the one axis red-green colour
        // blindness preserves — a magenta stripe next to a green one stays
        // separable. And olive is red-dominant by a single count, which no
        // eye reads as red; it is a dark yellow-green.
        var colours = RecorderPalette.Colours;

        var reds = Enumerable.Range(0, colours.Count)
            .Where(i => colours[i].R > colours[i].G
                        && colours[i].R > colours[i].B
                        && colours[i].R - colours[i].G > 30)
            .ToList();
        var greens = Enumerable.Range(0, colours.Count)
            .Where(i => colours[i].G > colours[i].R && colours[i].G > colours[i].B)
            .ToList();

        Assert.NotEmpty(reds);
        Assert.NotEmpty(greens);

        foreach (var red in reds)
        {
            foreach (var green in greens)
            {
                Assert.True(Math.Abs(red - green) > 1,
                    $"a green (index {green}) sits next to the red (index {red})");
            }
        }
    }

    [Fact]
    public void TheNamesComeBackInTheOrderTheColoursWereHandedOut()
    {
        // The legend reads from Names and the stripes come from Assign. If those
        // two ever disagreed, the legend would list rooms against the wrong
        // swatches — which is worse than having no legend.
        var rooms = new[] { "Rotman 3010", "Rotman 1050", "Rotman 2010" };

        Assert.Equal(
            RecorderPalette.Colours.Take(rooms.Length),
            RecorderPalette.Names(rooms).Select(name => RecorderPalette.Assign(rooms)[name]));
    }

    [Fact]
    public void TheOrderIsStableAcrossRepeatedCalls()
    {
        var rooms = new[] { "Rotman 1050", "Rotman 3010", "Rotman 2010", "Rotman 1180" };

        Assert.Equal(RecorderPalette.Names(rooms), RecorderPalette.Names(rooms.Reverse()));
    }

    [Fact]
    public void AssigningNothingIsNotAFailure()
    {
        // A week with nothing scheduled has no recorders at all.
        Assert.Empty(RecorderPalette.Assign([]));
    }

    [Fact]
    public void HexIsTheFormTheAppConvertsFrom()
    {
        Assert.Equal("#2F6FED", RecorderPalette.Colours[0].Hex);
    }
}
