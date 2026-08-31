using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Server.Api;

///<summary>
///The server-side half of <see cref="ServerProxyGraphDb"/>: it runs the query the browser could not.
///It reuses the same drivers the browser uses rather than reimplementing them — they are built on
///HttpClient and ClientWebSocket, which work at least as well outside a browser as in one.
///</summary>
public static class GraphApi
{
    ///<summary>
    ///Sent on every proxied request. A browser always supplies a User-Agent; HttpClient supplies none,
    ///and some public endpoints reject requests without one — Wikidata answers a bare request with
    ///403 "Please set a user-agent". Its robot policy asks for a contact address, so anyone pointing
    ///this at a public endpoint in volume should extend this string with one.
    ///</summary>
    public const string UserAgent = "GraphDBViewer/1.0 (graph database viewer)";

    ///<summary>
    ///The target is whatever the caller names, deliberately — this is a developer tool, and pointing it
    ///at an arbitrary endpoint is the point of the feature. Two consequences are worth staying awake to,
    ///because neither is true of the browser-direct path:
    ///
    ///  * the endpoint takes no authentication today, so anything that can reach this host can make the
    ///    host open connections on its behalf, including to addresses only the host can route to;
    ///  * the connection's credentials travel to the server instead of staying in the browser.
    ///
    ///Both are the expected shape for a single-user tool on a trusted network. Worth revisiting when the
    ///Auth module lands and this can sit behind a signed-in user.
    ///</summary>
    ///<summary>Returns the endpoint so the caller can put authorization on it.</summary>
    public static IEndpointConventionBuilder MapGraphApi(this WebApplication app)
    {
        //Built from the client's own constant, so the two halves cannot drift apart.
        var execute = app.MapPost("/" + ServerProxyGraphDb.ExecutePath, ExecuteAsync);

        //Says only that a host is here, and is deliberately left off the returned builder so it stays
        //anonymous: a client asks this before it could possibly be signed in. It reveals nothing that
        //serving the page did not already reveal.
        app.MapGet("/" + ServerProxyGraphDb.CapabilitiesPath, () => Results.Ok(new { serverRoute = true }));

        return execute;
    }

    private static async Task<IResult> ExecuteAsync(GraphQueryRequest request, GraphConnectionPool pool, CancellationToken cancellationToken)
    {
        if (request?.Connection == null)
            return Results.BadRequest("A connection is required.");

        //The pool reuses one driver per connection so a bulk commit does not re-handshake on every query.
        //It only ever builds a browser-direct driver, so this cannot bounce back out to the proxy and loop:
        //ViaServer is read at the client's call site, never here.
        var result = await pool.ExecuteAsync(request.Connection, request.Query ?? "", cancellationToken);

        return Results.Ok(GraphQueryResponse.From(result));
    }
}
