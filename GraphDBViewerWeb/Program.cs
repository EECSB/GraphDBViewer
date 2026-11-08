using Blazored.LocalStorage;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using GraphDBViewerWeb;
using GraphDBViewerWeb.Code;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddBlazoredLocalStorage();

//Whether this page is the viewer embedded in another site (the core's /embed page injects it, synced 2026-11-02). This
//build has no host to hand an embed token to, so nothing reads its token here.
builder.Services.AddScoped<EmbedSession>();

//IndexedDB-backed persistence (far larger quota than localStorage's ~5 MB, with transparent compression and
//a one-time migration of existing localStorage data). LocalAppStorage remains as the localStorage-backed
//IAppStorage should it ever need swapping back — the migration reads through Blazored directly.
//Except for the embedded viewer, which keeps what it keeps in memory, as Treeality's does: it has no business in this
//browser's own storage for this site (MemoryAppStorage).
builder.Services.AddScoped<IndexedDbAppStorage>();
builder.Services.AddScoped<IAppStorage>(sp =>
{
    if (sp.GetRequiredService<EmbedSession>().IsEmbedded)
        return new MemoryAppStorage();

    return sp.GetRequiredService<IndexedDbAppStorage>();
});
builder.Services.AddScoped<WorkspaceStore>();
builder.Services.AddScoped<LlmConnectionStore>();

//The shell's own state, and it is not optional: the application shell moved the top bar out of Home into
//MainLayout, which injects this. Without the registration the synced core fails DI resolution on the
//first render and this app does not boot at all — which is why SYNCING.md names it as the one edit the
//script cannot make. Theme and full-width live here now instead of in Home.
builder.Services.AddScoped<ShellState>();

//The PDF reader is fetched on first use rather than at boot: it is the single largest thing this app
//ships, and a session that never opens a PDF should never pay for it. The assemblies are set aside as
//BlazorWebAssemblyLazyLoad items in this project's file.
builder.Services.AddScoped<IPdfReaderLoader, LazyPdfReaderLoader>();

//What this host is, told to the shared viewer once. The load-bearing answer is HasServerRoute: there is
//no server behind this build, and GremlinConnection.ViaServer defaults to true, so without it every new
//connection would be aimed at a host that does not exist. The showcase is the viewer's own, carried by the core since
//2026-11-06 (Treeality's Plugins page shows it too); this build offers it from Settings and the opening question.
//wwwroot/showcase/index.html only sends the address the README and the blog link to on to it.
var hostOptions = new ViewerHostOptions
{
    AppName = "Graph DB Viewer",
    EditionLabel = "Web edition",
    HasServerRoute = false,
    ShowcaseUrl = ViewerPlugin.ShowcasePath,

    //Offered even though this build has no host: the Server button stays on screen and explains
    //itself, since the reason to want the other edition is exactly what hiding it would hide.
    ServerEditionUrl = "https://github.com/EECSB/GraphDBViewer#the-server-edition-docker",

    //Light blue 1 for someone who has chosen no theme (the user, 2026-11-02); Treeality starts dark.
    DefaultTheme = ShellState.ThemeLightBlue
};

//Only a Development build looks for hand-filled credentials. The file is git-ignored, so it is absent
//in every deployment and on every other machine, and fetching what is not there logs a 404 in the
//console of everyone who never had one.
if (builder.HostEnvironment.IsDevelopment())
    hostOptions.DevSecretsPath = DevSecrets.Path;

builder.Services.AddSingleton(hostOptions);

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

//The AI panels send to a model with a client of their own, with no timeout of its own: the one above gives up at
//HttpClient's default of 100 seconds, which a long answer or a thinking model runs past. Each request carries a clock
//of five minutes instead (LlmProviderFactory.RequestTimeout), as in Treeality.
builder.Services.AddSingleton(new LlmHttpClient(LlmProviderFactory.NewHttpClient()));

var app = builder.Build();

//Whether a host is there to proxy database connections cannot be known when this is built: the same
//WebAssembly output is served from a static file host and from GraphDBViewerWeb.Server. So ask, once,
//before the first render -- HasServerRoute decides what the connection card offers and which driver a
//connection gets, so the answer has to be in before anything draws.
using (var scope = app.Services.CreateScope())
{
    var http = scope.ServiceProvider.GetRequiredService<HttpClient>();
    var options = scope.ServiceProvider.GetRequiredService<ViewerHostOptions>();

    options.HasServerRoute = await HostProbe.HostAnswersAsync(http);

    //Something answered, so this same output is running as the full-stack edition rather than the
    //web one, and the label under the title should say which of the two you are looking at.
    if (options.HasServerRoute)
        options.EditionLabel = "Server edition";
}

await app.RunAsync();

