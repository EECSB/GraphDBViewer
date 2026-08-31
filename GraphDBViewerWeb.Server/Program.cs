using GraphDBViewerWeb.Server.Api;

//The backend edition's host. It serves the same WebAssembly app the static build serves, and adds the
//query proxy the browser sometimes needs. Nothing else: no accounts, no database, no server-side state.
var builder = WebApplication.CreateBuilder(args);

//One driver per distinct connection, reused across requests, so a bulk commit pays the handshake once
//instead of on every query. The eviction service closes them once they have been idle a while.
builder.Services.AddSingleton<GraphConnectionPool>();
builder.Services.AddHostedService<GraphConnectionPoolEvictionService>();

var app = builder.Build();

//Serves the client's _framework payload, then its static assets — including the core's, which arrive at
//_content/GraphDBViewer.Core/ exactly as they do without a host.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapGraphApi();

//Any route that is not a file and not the API is the single-page app.
app.MapFallbackToFile("index.html");

app.Run();
