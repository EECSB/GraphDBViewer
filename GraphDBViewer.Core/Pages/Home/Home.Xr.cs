using System.Text;
using Microsoft.JSInterop;
using Net.Codecrete.QrCodeGenerator;

namespace GraphDBViewerWeb.Pages;

//The VR / AR (WebXR) viewer overlay: opens a standalone three.js scene of the current 3D graph that
//renderer.xr can drive into an immersive session (see XrViewerInterop.js). The graph engine has no WebXR,
//so this is a separate scene snapshotting the live 3D layout.
public partial class Home
{
    private const string XrContainerId = "gdbvXrViewer";

    private bool xrViewerOpen;
    private bool xrHasWebXR;
    private bool xrVrSupported;
    private bool xrArSupported;
    private bool xrSecureContext;

    //A short message shown under the viewer: an open / entry failure, or null when all is well.
    private string xrStatus;

    //The "open on your phone" QR points at the deployed HTTPS app, where WebXR is a secure context for a
    //phone (the dev server / localhost isn't reachable or secure from a phone). The QR is built in C# as an
    //inline SVG (Net.Codecrete.QrCodeGenerator), the same approach as the MeshOptimizer project — no JS lib.
    private const string DeploymentUrl = "https://eecs.blog/BlazorApps/GraphDBViewer/";
    private string xrPhoneUrl;
    private string xrQrSvg;

    //Opens the overlay, then (once its container has rendered) builds the WebXR scene and reads which
    //immersive modes this device supports so the Enter VR / Enter AR buttons can enable themselves.
    private async Task OpenXrViewerAsync()
    {
        xrViewerOpen = true;
        xrStatus = null;
        xrPhoneUrl = null;
        xrQrSvg = null;
        xrHasWebXR = false;
        xrVrSupported = false;
        xrArSupported = false;
        StateHasChanged();

        var result = await JS.InvokeAsync<XrOpenResult>("xrViewerInterop.open", XrContainerId);

        if (result == null || !result.Ok)
        {
            xrStatus = result?.Error ?? "Could not open the viewer.";
        }
        else
        {
            xrHasWebXR = result.HasXR;
            xrVrSupported = result.Vr;
            xrArSupported = result.Ar;
            xrSecureContext = result.Secure;
        }

        StateHasChanged();

        //The "open on your phone" QR points at the deployed HTTPS app — the phone is where AR usually runs,
        //and the dev server / localhost isn't reachable or a secure context for it.
        xrPhoneUrl = DeploymentUrl;
        GenerateQr(DeploymentUrl);
        StateHasChanged();
    }

    //Builds the phone-link QR as an inline SVG — one 1×1 rect per dark module, inside a 4-module quiet zone —
    //mirroring the MeshOptimizer project's approach (Net.Codecrete.QrCodeGenerator, no JS library).
    private void GenerateQr(string text)
    {
        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        int n = qr.Size;
        int border = 4;
        int dim = n + 2 * border;

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{-border} {-border} {dim} {dim}\" preserveAspectRatio=\"xMidYMid meet\" shape-rendering=\"crispEdges\" style=\"width: 100%; height: auto; display: block;\">");
        sb.Append($"<rect x=\"{-border}\" y=\"{-border}\" width=\"{dim}\" height=\"{dim}\" fill=\"#ffffff\"/>");

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                if (qr.GetModule(x, y))
                    sb.Append($"<rect x=\"{x}\" y=\"{y}\" width=\"1\" height=\"1\" fill=\"#000000\"/>");
            }
        }

        sb.Append("</svg>");
        xrQrSvg = sb.ToString();
    }

    private async Task CloseXrViewerAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("xrViewerInterop.close");
        }
        catch { }

        xrViewerOpen = false;
        StateHasChanged();
    }

    private async Task EnterVrAsync()
    {
        var r = await JS.InvokeAsync<XrEnterResult>("xrViewerInterop.enterVR");
        if (r != null && !r.Ok)
            xrStatus = $"VR: {r.Error}";

        StateHasChanged();
    }

    private async Task EnterArAsync()
    {
        var r = await JS.InvokeAsync<XrEnterResult>("xrViewerInterop.enterAR");
        if (r != null && !r.Ok)
            xrStatus = $"AR: {r.Error}";

        StateHasChanged();
    }

    private class XrOpenResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public bool HasXR { get; set; }
        public bool Vr { get; set; }
        public bool Ar { get; set; }
        public bool Secure { get; set; }
        public int NodeCount { get; set; }
    }

    private class XrEnterResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
    }
}
