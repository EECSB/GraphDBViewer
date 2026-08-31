using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

///<summary>
///The answers this deployment gives the shared viewer, in one place so every bUnit context renders the
///app the users of this build actually get: named for this product, no server to route connections
///through, and a bundled showcase. It mirrors Program.cs, and a test that renders a component reading
///any of it has to register this or the render throws.
///</summary>
internal static class WebOnlyHost
{
    public static ViewerHostOptions Options()
    {
        return new ViewerHostOptions
        {
            AppName = "Graph DB Viewer",
            EditionLabel = "Web edition",
            HasServerRoute = false,
            ShowcaseUrl = "showcase/index.html",
            ServerEditionUrl = "https://github.com/EECSB/GraphDBViewer#the-server-edition-docker"
        };
    }
}
