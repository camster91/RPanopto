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

    [Fact]
    public void RedAndGreenAreNotNeighbours()
    {
        // The one pair red-green colour blindness cannot separate. They were
        // adjacent in the old six-colour list.
        var red = RecorderPalette.Colours[6];
        var green = RecorderPalette.Colours[11];

        Assert.True(red.R > red.G && red.R > red.B);
        Assert.True(green.G > green.R && green.G > green.B);
        Assert.True(
            Math.Abs(6 - 11) > 1,
            "red and green must not be handed out one after the other");
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
