using Bunit;
using GraphDBViewerWeb.Code;
using Microsoft.Extensions.DependencyInjection;
using GraphDBViewerWeb.Components;

namespace GraphDBViewerWeb.Tests;

public class ShowcaseModalTests : BunitContext
{
    public ShowcaseModalTests()
    {
        Services.AddSingleton(WebOnlyHost.Options());
    }

    [Fact]
    public void NotVisible_RendersNothing()
    {
        var cut = Render<ShowcaseModal>(p => p.Add(c => c.Visible, false));

        Assert.Empty(cut.FindAll("iframe"));
    }

    //The overlay shows the landing page bundled into wwwroot at /showcase.
    [Fact]
    public void Visible_ShowsTheBundledShowcaseInAnIframe()
    {
        var cut = Render<ShowcaseModal>(p => p.Add(c => c.Visible, true));

        //Resolved against the app base URI, so it's an absolute URL ending in the bundled path.
        Assert.EndsWith("showcase/index.html", cut.Find("iframe").GetAttribute("src"));
    }

    [Fact]
    public void CloseButton_RaisesOnClose()
    {
        var closed = false;
        var cut = Render<ShowcaseModal>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.OnClose, () => closed = true));

        cut.Find(".btn-close").Click();

        Assert.True(closed);
    }
}

public class AboutModalTests : BunitContext
{
    public AboutModalTests()
    {
        Services.AddSingleton(WebOnlyHost.Options());
    }

    [Fact]
    public void ShowcaseButton_RaisesOnOpenShowcase()
    {
        var opened = false;
        var cut = Render<AboutModal>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.OnOpenShowcase, () => opened = true));

        cut.FindAll("button").First(b => b.TextContent.Contains("feature showcase")).Click();

        Assert.True(opened);
    }
}
