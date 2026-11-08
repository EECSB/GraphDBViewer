using Bunit;
using GraphDBViewerWeb.Components;

namespace GraphDBViewerWeb.Tests;

//Component-level cover for the Import panel's gate: it validates the pasted text and only then raises
//the callback. Unlike the rest of this project's tests, these render real Razor markup.
public class JsonInputPanelTests : BunitContext
{
    //Located by text rather than position, so adding or reordering buttons can't silently point these
    //tests at the wrong control.
    private static AngleSharp.Dom.IElement VisualizeButton(IRenderedComponent<JsonInputPanel> cut)
    {
        return cut.FindAll("button").First(b => b.TextContent.Trim() == "Visualize");
    }

    [Fact]
    public void Visualize_UnrecognizedText_ShowsErrorAndDoesNotRaise()
    {
        string raised = null;
        var cut = Render<JsonInputPanel>(p => p.Add(c => c.OnVisualize, s => raised = s));

        cut.Input("textarea", "this is not a graph in any format");
        cut.Click(VisualizeButton);

        Assert.Null(raised);
        Assert.Contains("Paste GraphSON/JSON", cut.Markup);
    }

    [Fact]
    public void Visualize_Dot_RaisesWithThePastedText()
    {
        string raised = null;
        var cut = Render<JsonInputPanel>(p => p.Add(c => c.OnVisualize, s => raised = s));

        cut.Input("textarea", "digraph { a -> b [label=knows] }");
        cut.Click(VisualizeButton);

        Assert.Equal("digraph { a -> b [label=knows] }", raised);
    }

    [Fact]
    public void Visualize_ExportedGremlin_RaisesSoADrawingCanRoundTrip()
    {
        string raised = null;
        var cut = Render<JsonInputPanel>(p => p.Add(c => c.OnVisualize, s => raised = s));

        cut.Input("textarea", "g.addV('person').property(T.id, 'alice')");
        cut.Click(VisualizeButton);

        Assert.NotNull(raised);
    }

    [Fact]
    public void Buttons_AreDisabledUntilSomethingIsPasted()
    {
        var cut = Render<JsonInputPanel>();

        Assert.True(VisualizeButton(cut).HasAttribute("disabled"));

        cut.Input("textarea", "digraph { a -> b }");

        Assert.False(VisualizeButton(cut).HasAttribute("disabled"));
    }

    //Visualize and Generate queries each have a picture before their word, which stays (2026-11-04, the user).
    [Fact]
    public void VisualizeAndGenerateQueries_HaveAnIconBeforeTheirWord()
    {
        var cut = Render<JsonInputPanel>();
        var generate = cut.FindAll("button").First(b => b.TextContent.Trim() == "Generate queries");

        Assert.Equal("I", VisualizeButton(cut).FirstElementChild.TagName);
        Assert.Contains("bi-eye", VisualizeButton(cut).FirstElementChild.ClassList);
        Assert.Equal("I", generate.FirstElementChild.TagName);
        Assert.Contains("bi-code-slash", generate.FirstElementChild.ClassList);
    }

    //Clear is the trash the query editor clears with (2026-11-03, the user), and empties the box.
    [Fact]
    public void Clear_IsTheTrash_AndEmptiesTheBox()
    {
        var cut = Render<JsonInputPanel>();

        cut.Input("textarea", "digraph { a -> b }");

        var clear = cut.Find("[data-testid=json-input-clear]");

        Assert.NotNull(clear.QuerySelector("i.bi-trash"));
        Assert.Contains("btn-outline-danger", clear.ClassName);
        Assert.Equal("", clear.TextContent.Trim());

        cut.Click("[data-testid=json-input-clear]");

        Assert.Equal("", cut.Find("textarea").GetAttribute("value") ?? "");
    }

}
