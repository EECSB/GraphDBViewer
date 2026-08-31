using AngleSharp.Dom;
using Bunit;
using GraphDBViewerWeb.Code;
using GraphDBViewerWeb.Components;
using Microsoft.Extensions.DependencyInjection;

namespace GraphDBViewerWeb.Tests;

//Markup cover for the AI-model form. Its provider buttons write the literals LlmProviderFactory.Create
//and OpenAiProvider.DefaultBaseUrlFor read back, and its per-provider fields (Max tokens, placeholders)
//encode decisions that are pinned in C# tests but were invisible at the markup layer until now.
//
//Rendered through the picker itself rather than through a panel that happens to contain one: the form
//belongs to the picker, and the Ask AI panel now shows the dropdown alone.
public class NlQueryModalTests : BunitContext
{
    private IRenderedComponent<LlmConnectionPicker> RenderModalWithOpenForm()
    {
        Services.AddSingleton<IAppStorage>(new NullStorage());
        Services.AddSingleton(new HttpClient());
        Services.AddScoped<LlmConnectionStore>();
        Services.AddSingleton(WebOnlyHost.Options());

        var cut = Render<LlmConnectionPicker>();

        //"+" opens the add-model form; a fresh LlmConnection defaults to Anthropic.
        cut.FindAll("button").First(b => b.TextContent.Trim() == "+").Click();

        return cut;
    }

    //And the panel that only picks does not show them, which is the reason the above had to move.
    [Fact]
    public void ThePickOnlyFormIsADropdownAndNothingElse()
    {
        Services.AddSingleton<IAppStorage>(new NullStorage());
        Services.AddSingleton(new HttpClient());
        Services.AddScoped<LlmConnectionStore>();

        var cut = Render<LlmConnectionPicker>(p => p.Add(c => c.PickOnly, true));

        Assert.Empty(cut.FindAll("button"));

        //With none saved, the dropdown is where the person is told what to do about it.
        Assert.Contains("add one in Settings", cut.Find("option").TextContent);
    }

    //Only the Anthropic adapter sends an output cap, so the box would silently do nothing elsewhere.
    [Fact]
    public void MaxTokens_ShownOnlyForAnthropic()
    {
        var cut = RenderModalWithOpenForm();

        Assert.Contains(cut.FindAll("label"), l => l.TextContent.Trim() == "Max tokens");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "OpenAI").Click();

        Assert.DoesNotContain(cut.FindAll("label"), l => l.TextContent.Trim() == "Max tokens");
    }

    //Anthropic's model field is the one optional one, so its placeholder must name the model a blank
    //field actually runs — derived from the provider constant so the offer and the behavior can't drift
    //apart again (they did once: the placeholder said haiku while a blank field ran opus).
    [Fact]
    public void AnthropicModelPlaceholder_NamesTheDefaultThatActuallyRuns()
    {
        var cut = RenderModalWithOpenForm();

        Assert.NotNull(cut.Find($"input[placeholder='{AnthropicProvider.DefaultModel} (default)']"));
    }

    [Fact]
    public void GeminiButton_OffersGeminisEndpointAsTheBaseUrl()
    {
        var cut = RenderModalWithOpenForm();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Gemini").Click();

        Assert.NotNull(cut.Find($"input[placeholder='{OpenAiProvider.GeminiBaseUrl}']"));
    }

    //A model added somewhere else has to reach a picker that was created before it existed. Every AI panel
    //is built with the page and merely hidden afterwards, so "created before it existed" is the normal
    //case, not the corner one: without this, adding a model under Settings looked like it had not saved.
    [Fact]
    public async Task APickerCatchesUpWhenAnotherOneSavesAModel()
    {
        Services.AddSingleton<IAppStorage>(new InMemoryStorage());
        Services.AddSingleton(new HttpClient());
        Services.AddScoped<LlmConnectionStore>();

        var picking = Render<LlmConnectionPicker>(p => p.Add(c => c.PickOnly, true));

        Assert.Contains("add one in Settings", picking.Find("option").TextContent);

        //The other picker — the one under Settings — adds one.
        var editing = Render<LlmConnectionPicker>();

        editing.FindAll("button").First(b => b.TextContent.Trim() == "+").Click();
        editing.Find("input[placeholder='e.g. Claude Opus']").Change("Added elsewhere");
        editing.FindAll("button").First(b => b.TextContent.Trim() == "Add").Click();

        picking.WaitForAssertion(() => Assert.Contains("Added elsewhere", picking.Markup));

        //And it is picked, rather than merely listed: a panel showing a model it will not use is worse
        //than one showing none.
        Assert.Equal("Added elsewhere", picking.Find("select").GetAttribute("value"));

        await Task.CompletedTask;
    }


    //The settings of the model you have chosen are what this panel is for, so they are simply shown.
    //There used to be an Edit button standing between the two, which is a mode for something already
    //decided by the dropdown beside it.
    [Fact]
    public void ChoosingAModelShowsItsSettings_WithNoEditButton()
    {
        Services.AddSingleton<IAppStorage>(new InMemoryStorage());
        Services.AddSingleton(new HttpClient());
        Services.AddScoped<LlmConnectionStore>();

        var cut = Render<LlmConnectionPicker>();

        //Add two, so there is something to switch between.
        foreach (var name in new[] { "Alpha", "Beta" })
        {
            cut.FindAll("button").First(b => b.TextContent.Trim() == "+").Click();
            cut.Find("input[placeholder='e.g. Claude Opus']").Change(name);
            cut.Find("input[type=password]").Change("key-" + name);
            cut.FindAll("button").First(b => b.TextContent.Trim() == "Add").Click();
        }

        //Saving leaves the form up, showing what was saved.
        cut.WaitForAssertion(() => Assert.Equal("Beta", cut.Find("input[placeholder='e.g. Claude Opus']").GetAttribute("value")));

        //Nothing is pressed to see another model's settings: choosing it is the whole gesture.
        cut.Find("select").Change("Alpha");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Alpha", cut.Find("input[placeholder='e.g. Claude Opus']").GetAttribute("value"));
            Assert.Equal("key-Alpha", cut.Find("input[type=password]").GetAttribute("value"));
        });

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Edit");
        Assert.DoesNotContain("Edit model", cut.Markup);

        //Cancel stays: with the form always on screen it reverts rather than closes.
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Cancel");
    }

    //The four provider families the factory routes on. "Other" is the label; its value is "Custom".
    [Fact]
    public void ProviderGroup_OffersAllFourFamilies()
    {
        var cut = RenderModalWithOpenForm();

        var group = cut.Find("[aria-label='Provider']");
        var texts = group.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToList();

        Assert.Equal(new[] { "Anthropic", "OpenAI", "Gemini", "Other" }, texts);
    }
}
