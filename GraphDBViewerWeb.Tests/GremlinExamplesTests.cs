using System.Text.RegularExpressions;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class GremlinExamplesTests
{
    public static IEnumerable<object[]> SampleLoaders => new[]
    {
        new object[] { GremlinExamples.TableGraphLoader },
        new object[] { GremlinExamples.ModernGraphLoader },
        new object[] { GremlinExamples.FlightRoutesLoader },
        new object[] { GremlinExamples.ThreeDObjectsLoader },
    };

    [Fact]
    public void EveryGroup_HasCategoryAndAtLeastOneExample()
    {
        foreach (var group in GremlinExamples.Groups)
        {
            Assert.False(string.IsNullOrWhiteSpace(group.Category));
            Assert.NotEmpty(group.Examples);
        }
    }

    [Fact]
    public void EveryExample_HasNameAndQuery()
    {
        foreach (var group in GremlinExamples.Groups)
            foreach (var example in group.Examples)
            {
                Assert.False(string.IsNullOrWhiteSpace(example.Name));
                Assert.False(string.IsNullOrWhiteSpace(example.Query));
            }
    }

    [Theory]
    [InlineData("Inspect")]
    [InlineData("Visualize")]
    [InlineData("Mutate")]
    [InlineData("Sample graphs")]
    public void Groups_ContainExpectedCategory(string category)
    {
        Assert.Contains(GremlinExamples.Groups, g => g.Category == category);
    }

    [Fact]
    public void Example_DefaultsToNonDestructive()
    {
        var example = new QueryExample("Count", "g.V().count()");
        Assert.False(example.Destructive);
    }

    [Fact]
    public void DropAllData_IsMarkedDestructive()
    {
        var dropAll = GremlinExamples.Groups
            .SelectMany(g => g.Examples)
            .First(e => e.Query == "g.V().drop()");

        Assert.True(dropAll.Destructive);
    }

    [Theory]
    [MemberData(nameof(SampleLoaders))]
    public void SampleLoader_IsAdditive_DoesNotDrop(string loader)
    {
        Assert.DoesNotContain("drop(", loader);
    }

    //JanusGraph takes no Groovy decimal: to it 48.2082 is a BigDecimal, "not supported" (2026-11-07, at the showcase's
    //refresh), where 48.2082d is a double, which TinkerGraph takes as well. Every coordinate carries the suffix.
    [Fact]
    public void GeoMapLoader_WritesEveryCoordinateAsADouble_WhichJanusGraphTakes()
    {
        var coordinates = Regex.Matches(GremlinExamples.GeoMapLoader, @"\.property\('(lat|lon)',(-?\d+\.\d+)(d?)\)");

        Assert.Equal(36, coordinates.Count);
        Assert.All(coordinates, one => Assert.Equal("d", one.Groups[3].Value));
    }

    //The geospatial sample exists to be put on the Map and the Globe: every city carries the plain
    //lat/lon pair the views read by convention, the routes are what the arcs draw, and — unlike the
    //other loaders, which end in a count — it returns the graph, so the map is one click away.
    [Fact]
    public void GeoMapLoader_LocatesEveryCity_AndReturnsTheRoutes()
    {
        var loader = GremlinExamples.GeoMapLoader;

        var cities = loader.Split("addV('City')").Length - 1;
        var latitudes = loader.Split(".property('lat',").Length - 1;
        var longitudes = loader.Split(".property('lon',").Length - 1;

        Assert.Equal(18, cities);
        Assert.Equal(cities, latitudes);
        Assert.Equal(cities, longitudes);
        Assert.Contains("addE('route')", loader);
        Assert.EndsWith("select('v','e','o')", loader);
        Assert.DoesNotContain("drop(", loader);

        //The user's list (2026-09-02): Dallas and Seoul in; São Paulo, Johannesburg, Nairobi, Dubai and
        //Singapore out; Europe and the United States filled in.
        Assert.Contains("'Dallas'", loader);
        Assert.Contains("'Seoul'", loader);
        Assert.DoesNotContain("Singapore", loader);
        Assert.DoesNotContain("Dubai", loader);
        Assert.DoesNotContain("Nairobi", loader);

        //The Cypher twin carries the same cities.
        var cypherCities = CypherExamples.GeoMapLoader.Split(":City {").Length - 1;

        Assert.Equal(cities, cypherCities);
        Assert.Contains("name:'Dallas'", CypherExamples.GeoMapLoader);
        Assert.DoesNotContain("Singapore", CypherExamples.GeoMapLoader);

        //Tagged like the 3D sample, so it can be found and removed as one thing.
        Assert.Contains("'exampleType','geo_example_1'", loader);
        Assert.Contains(GremlinExamples.Groups.First(g => g.Category == "Sample graphs").Examples, e => e.Name == "Geospatial map");
        Assert.Contains(CypherExamples.Groups.First(g => g.Category == "Sample graphs").Examples, e => e.Name == "Geospatial map");
    }

    [Theory]
    [MemberData(nameof(SampleLoaders))]
    public void SampleLoader_AddsVerticesAndEdges_AndReportsCount(string loader)
    {
        Assert.Contains("addV(", loader);
        Assert.Contains("addE(", loader);
        Assert.EndsWith("g.V().count()", loader);
    }
}
