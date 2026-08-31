using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using GraphDBViewerWeb.Code;
using GraphDBViewerWeb.Components;
using Microsoft.Extensions.DependencyInjection;

namespace GraphDBViewerWeb.Tests;

//Markup + interaction cover for the knowledge-graph modal. The provider call is stubbed at the
//HttpMessageHandler layer with an Anthropic-shaped response, so the whole generate → parse → preview →
//use flow runs for real without spending tokens.
public class KgGenerateModalTests : BunitContext
{
    private const string ConnectionsKey = "graphdbviewer:llmConnections";
    private const string MergeModeKey = "graphdbviewer:kgMergeMode";

    //A two-node one-edge generation ("Alice worksAt Acme") used across the flow tests.
    private const string SampleReply = """
        {"nodes":[
            {"id":"alice","label":"Person","properties":{"name":"Alice"}},
            {"id":"acme","label":"Company","properties":{"name":"Acme Inc."}}
        ],"edges":[{"source":"alice","target":"acme","label":"worksAt","properties":{}}]}
        """;

    //A canvas already holding Acme under a different id — what Merge must fold onto.
    private const string CanvasJson = """
        [{"id":"acme-1","label":"Company","properties":{"name":"Acme"}}]
        """;

    private sealed class StubProviderHandler : HttpMessageHandler
    {
        private readonly string _replyText;

        ///<summary>Raw body returned for requests to wikipedia.org, so the fetch flow is testable too.</summary>
        public string WikipediaJson { get; set; }

        public StubProviderHandler(string replyText)
        {
            _replyText = replyText;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body;

            if (request.RequestUri != null && request.RequestUri.Host.Contains("wikipedia"))
                body = WikipediaJson;
            else
                body = JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = _replyText } } });

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }

    private async Task<InMemoryStorage> StorageWithConnectionAsync()
    {
        var storage = new InMemoryStorage();
        await storage.SetAsync(ConnectionsKey, new Dictionary<string, LlmConnection>
        {
            ["Test"] = new LlmConnection { Name = "Test", ProviderType = "Anthropic", ApiKey = "k" }
        });

        return storage;
    }

    private StubProviderHandler handler;

    private IRenderedComponent<KgGenerateModal> RenderModal(
        InMemoryStorage storage,
        string modelReply,
        JsonElement currentGraph = default,
        SchemaVocabulary schema = null)
    {
        handler = new StubProviderHandler(modelReply);
        Services.AddSingleton<IAppStorage>(storage);
        Services.AddSingleton(new HttpClient(handler));
        Services.AddScoped<LlmConnectionStore>();
        Services.AddSingleton(WebOnlyHost.Options());

        return Render<KgGenerateModal>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.Schema, schema)
            .Add(c => c.CurrentGraph, currentGraph)
            .Add(c => c.OnUseGraph, args => captured = args)
            .Add(c => c.OnClose, () => closed = true));
    }

    private (string Gremlin, bool Merge)? captured;
    private bool closed;

    private void Generate(IRenderedComponent<KgGenerateModal> cut, string source)
    {
        cut.Find("textarea[placeholder^='Paste notes']").Input(source);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Generate graph").Click();
    }

    //AC 10: the modal stages and stops — no commit affordance, so it can't be added by accident.
    [Fact]
    public async Task Modal_HasNoCommitAffordance()
    {
        var cut = RenderModal(await StorageWithConnectionAsync(), SampleReply);

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Commit"));
    }

    //AC 9: absent key ⇒ Replace, no special-casing.
    [Fact]
    public async Task ModeToggle_DefaultsToReplaceOnFirstUse()
    {
        var cut = RenderModal(await StorageWithConnectionAsync(), SampleReply);

        var replace = cut.FindAll("button").First(b => b.TextContent.Trim() == "Replace drawing");

        Assert.Contains("btn-danger", replace.ClassList);
    }

    [Fact]
    public async Task ModeToggle_RemembersMergeAcrossSessions()
    {
        var storage = await StorageWithConnectionAsync();
        await storage.SetAsync(MergeModeKey, true);

        var cut = RenderModal(storage, SampleReply);
        var merge = cut.FindAll("button").First(b => b.TextContent.Trim() == "Merge into drawing");

        Assert.Contains("btn-primary", merge.ClassList);
    }

    [Fact]
    public async Task ModeToggle_PersistsTheChoice()
    {
        var storage = await StorageWithConnectionAsync();
        var cut = RenderModal(storage, SampleReply);

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Merge into drawing").Click();

        Assert.True(await storage.GetAsync<bool>(MergeModeKey));
    }

    //AC 11: grounding is an option only when there is a schema to ground in.
    [Fact]
    public async Task GroundingToggle_OnlyOfferedWhenASchemaExists()
    {
        var without = RenderModal(await StorageWithConnectionAsync(), SampleReply);

        Assert.Empty(without.FindAll("#kgGroundSchema"));
    }

    [Fact]
    public async Task GroundingToggle_OfferedWithASchema()
    {
        var cut = RenderModal(await StorageWithConnectionAsync(), SampleReply, schema: new SchemaVocabulary());

        Assert.Single(cut.FindAll("#kgGroundSchema"));
    }

    [Fact]
    public async Task Generate_Replace_PreviewsTheWholeGraphAndHandsItOver()
    {
        var cut = RenderModal(await StorageWithConnectionAsync(), SampleReply);

        Generate(cut, "Alice works at Acme.");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("2 node(s) · 1 edge(s)", cut.Markup);
            Assert.Contains("Person ×1", cut.Markup);
            Assert.Contains("addV('Person')", cut.Markup);
        });

        cut.FindAll("button").First(b => b.TextContent.Contains("Use this graph")).Click();

        Assert.NotNull(captured);
        Assert.False(captured.Value.Merge);
        Assert.Contains("addV('Company')", captured.Value.Gremlin);
        Assert.True(closed);
    }

    //AC 8: in Merge the preview is the post-fold result — the repeat entity is merged, not counted as
    //new, and the delta hands over the existing canvas id instead of a colliding addV.
    [Fact]
    public async Task Generate_Merge_PreviewsThePostFoldResult()
    {
        var storage = await StorageWithConnectionAsync();
        await storage.SetAsync(MergeModeKey, true);

        var canvas = JsonDocument.Parse(CanvasJson).RootElement.Clone();
        var cut = RenderModal(storage, SampleReply, canvas);

        Generate(cut, "Alice works at Acme.");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("1 new node(s) · 1 new edge(s) · 1 merged into existing", cut.Markup);

            //TextContent decodes HTML entities, so the apostrophes compare cleanly.
            var warnings = cut.Find(".alert-warning").TextContent;
            Assert.Contains("Merged 'Acme Inc.' into existing 'Acme'", warnings);
        });

        cut.FindAll("button").First(b => b.TextContent.Contains("Use this graph")).Click();

        Assert.NotNull(captured);
        Assert.True(captured.Value.Merge);
        Assert.DoesNotContain("addV('Company')", captured.Value.Gremlin);
        Assert.Contains("'acme-1'", captured.Value.Gremlin);
    }

    //AC 6 + AC 7: a parse failure shows a clear error with the raw output, and invalid JSON names the
    //output cap as the likely cause.
    [Fact]
    public async Task Generate_ParseFailure_ShowsErrorAndRawOutput()
    {
        var cut = RenderModal(await StorageWithConnectionAsync(), "this is not a graph");

        Generate(cut, "Some text.");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("not valid JSON", cut.Markup);
            Assert.Contains("Max tokens", cut.Markup);
            Assert.Contains("this is not a graph", cut.Markup);
        });
    }

    [Fact]
    public async Task UploadedFile_FillsTheSourceText()
    {
        var cut = RenderModal(await StorageWithConnectionAsync(), SampleReply);

        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>()
            .UploadFiles(InputFileContent.CreateFromText("Alice works at Acme.", "notes.txt"));

        var textarea = cut.Find("textarea[placeholder^='Paste notes']");
        var value = textarea.GetAttribute("value");

        if (value == null)
            value = textarea.TextContent;

        Assert.Contains("Alice works at Acme.", value);
    }

    //A PDF used to be turned away at the door with an explanation about the browser. It is read now, by
    //the same managed parser the desktop half of this codebase uses.
    [Fact]
    public async Task UploadedPdf_IsReadIntoTheSourceText()
    {
        var cut = RenderModal(await StorageWithConnectionAsync(), SampleReply);

        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page.AddText("Acme Robotics employs Alice Chen.", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);

        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>()
            .UploadFiles(InputFileContent.CreateFromBinary(builder.Build(), "report.pdf"));

        cut.WaitForAssertion(() =>
        {
            var textarea = cut.Find("textarea[placeholder^='Paste notes']");
            var value = textarea.GetAttribute("value") ?? textarea.TextContent;

            Assert.Contains("Acme Robotics", value);
        });
    }

    //And something that only claims to be one fails by name, rather than by stack trace.
    [Fact]
    public async Task UploadedPdf_ThatIsNotOne_SaysWhichFile()
    {
        var cut = RenderModal(await StorageWithConnectionAsync(), SampleReply);

        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>()
            .UploadFiles(InputFileContent.CreateFromText("%PDF-1.7 …", "report.pdf"));

        cut.WaitForAssertion(() => Assert.Contains("report.pdf", cut.Find(".alert-danger").TextContent));
    }

    [Fact]
    public async Task WikipediaFetch_FillsTheSourceText()
    {
        var cut = RenderModal(await StorageWithConnectionAsync(), SampleReply);
        handler.WikipediaJson = """
            {"query":{"pages":{"1":{"title":"Acme","extract":"Acme is a company founded by Alice."}}}}
            """;

        cut.Find("input[placeholder*='Wikipedia']").Change("Acme");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Fetch").Click();

        cut.WaitForAssertion(() =>
        {
            var textarea = cut.Find("textarea[placeholder^='Paste notes']");
            var value = textarea.GetAttribute("value");

            if (value == null)
                value = textarea.TextContent;

            Assert.Contains("Acme is a company founded by Alice.", value);
        });
    }

    [Fact]
    public async Task WikipediaFetch_MissingArticle_ShowsAClearError()
    {
        var cut = RenderModal(await StorageWithConnectionAsync(), SampleReply);
        handler.WikipediaJson = """
            {"query":{"pages":{"-1":{"title":"Nope","missing":""}}}}
            """;

        cut.Find("input[placeholder*='Wikipedia']").Change("Nope");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Fetch").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No Wikipedia article found", cut.Markup);
        });
    }

    [Fact]
    public async Task UploadedFile_OverTheCap_IsRefusedWithAClearError()
    {
        var cut = RenderModal(await StorageWithConnectionAsync(), SampleReply);

        var oversized = new string('x', 1024 * 1024 + 1);
        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>()
            .UploadFiles(InputFileContent.CreateFromText(oversized, "big.txt"));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("the cap is 1024 KB", cut.Markup);
        });
    }

    [Fact]
    public async Task Generate_Merge_NothingNew_DisablesUse()
    {
        var storage = await StorageWithConnectionAsync();
        await storage.SetAsync(MergeModeKey, true);

        //The generation IS the canvas: same entity, no edges — the delta comes out empty.
        var canvas = JsonDocument.Parse(CanvasJson).RootElement.Clone();
        var reply = """{"nodes":[{"id":"acme","label":"Company","properties":{"name":"Acme"}}],"edges":[]}""";
        var cut = RenderModal(storage, reply, canvas);

        Generate(cut, "Acme exists.");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Nothing new", cut.Markup);
        });

        var use = cut.FindAll("button").First(b => b.TextContent.Contains("Use this graph"));

        Assert.True(use.HasAttribute("disabled"));
    }
}
