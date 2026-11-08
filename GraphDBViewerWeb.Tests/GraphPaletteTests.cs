using System.Linq;
using System.Text.RegularExpressions;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class GraphPaletteTests
{
    //A pinned value, since two calls in one run agree for any hash: string.GetHashCode too, which differs from one run
    //to the next.
    [Fact]
    public void ColorForLabel_IsTheSameInEveryRun()
    {
        Assert.Equal("#6610f2", GraphPalette.ColorForLabel("Component"));
    }

    [Fact]
    public void ColorForLabel_ReturnsAPaletteColor()
    {
        Assert.Contains(GraphPalette.ColorForLabel("anything"), GraphPalette.Colors);
        Assert.Contains(GraphPalette.ColorForLabel("Assembly"), GraphPalette.Colors);
    }

    [Fact]
    public void ColorForLabel_NullOrEmpty_ReturnsFirstColor()
    {
        Assert.Equal(GraphPalette.Colors[0], GraphPalette.ColorForLabel(null));
        Assert.Equal(GraphPalette.Colors[0], GraphPalette.ColorForLabel(""));
    }

    [Fact]
    public void Colors_AreAllValidSixDigitHex()
    {
        Assert.All(GraphPalette.Colors, c => Assert.Matches(new Regex("^#[0-9a-fA-F]{6}$"), c));
    }

    [Fact]
    public void ColorForLabel_DistinguishesCommonSchemaLabels()
    {
        //Not guaranteed collision-free in general, but the sample schema's labels should spread across the palette.
        var labels = new[] { "Assembly", "Component", "Material", "Product" };
        var distinct = labels.Select(GraphPalette.ColorForLabel).Distinct().Count();

        Assert.True(distinct >= 3, $"expected the 4 sample labels to use at least 3 colors, got {distinct}");
    }
}
