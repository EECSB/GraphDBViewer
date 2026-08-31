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
    public void Groups_AreNotEmpty()
    {
        Assert.NotEmpty(GremlinExamples.Groups);
    }

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
        {
            foreach (var example in group.Examples)
            {
                Assert.False(string.IsNullOrWhiteSpace(example.Name));
                Assert.False(string.IsNullOrWhiteSpace(example.Query));
            }
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

    [Theory]
    [MemberData(nameof(SampleLoaders))]
    public void SampleLoader_AddsVerticesAndEdges_AndReportsCount(string loader)
    {
        Assert.Contains("addV(", loader);
        Assert.Contains("addE(", loader);
        Assert.EndsWith("g.V().count()", loader);
    }
}
