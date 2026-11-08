using System.Reflection;
using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

///<summary>
///Unit tests for the non-network models in GremlinDB. The result type moved out to GraphDbResult when
///the client was put behind IGraphDb — see GraphDbResultTests.
///</summary>
public class GremlinDbModelsTests
{
    //── GremlinConnection ───────────────────────────────────────────────

    [Fact]
    public void GremlinConnection_CopyConstructor_CopiesAllProperties()
    {
        //Every property, found by reflection and set away from what a new connection holds, so one added later and
        //left out of the copy constructor fails here instead of quietly taking its default in every copy.
        var properties = typeof(GremlinDB.GremlinConnection).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).ToList();
        var original = new GremlinDB.GremlinConnection();

        foreach (var property in properties)
            property.SetValue(original, ValueOtherThan(property, property.GetValue(original)));

        var copy = new GremlinDB.GremlinConnection(original);

        Assert.Empty(properties.Where(p => !Equals(p.GetValue(original), p.GetValue(copy))).Select(p => p.Name));
    }

    //A value the property does not hold: a string its own name, so no two are alike, a number one more, a flag the
    //other way.
    private static object ValueOtherThan(PropertyInfo property, object current)
    {
        if (property.PropertyType == typeof(string))
            return property.Name;
        else if (property.PropertyType == typeof(int))
            return (int)current + 1;
        else if (property.PropertyType == typeof(bool))
            return !(bool)current;

        throw new NotSupportedException($"{property.Name} is a {property.PropertyType.Name}, which this test has no value for yet: add one here.");
    }
}
