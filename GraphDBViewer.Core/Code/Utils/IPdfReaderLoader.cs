using System.Collections.Generic;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Fetches the PDF reader before it is first needed.
///
///PdfPig is by a wide margin the largest thing this app ships — around 870 KB compressed, near a fifth
///of the whole download — and it earns none of that until somebody actually loads a PDF. So the WASM
///hosts mark its assemblies <c>BlazorWebAssemblyLazyLoad</c>, which takes them out of the boot manifest,
///and implement this to pull them in on demand.
///
///The seam exists because the halves cannot live together: the code that reads a PDF is in this shared
///library, while <c>LazyAssemblyLoader</c> is a Blazor WebAssembly service that only a WASM host has.
///Making Core depend on it would drag a browser-only package into the server host as well.
///
///Optional by design. A host that registers nothing simply has the assemblies already — which is what
///"not lazy loaded" means — so the caller resolves this with <c>GetService</c> and carries on when it
///comes back null.
///</summary>
public interface IPdfReaderLoader
{
    ///<summary>
    ///Ensures the PDF reader is loaded. Returns null on success, or a message to show the user when it
    ///could not be fetched — offline, or a network that dropped mid-download. Safe to call repeatedly;
    ///the assemblies load once.
    ///</summary>
    Task<string> EnsureLoadedAsync();
}

///<summary>
///The assemblies the PDF reader is made of, named once so the hosts that lazy-load them and the code
///that asks for them cannot drift apart. These same names appear as <c>BlazorWebAssemblyLazyLoad</c>
///items in each WASM host's project file; adding one there and forgetting it here would download an
///assembly nobody asks for, and the reverse would ask for one that was never set aside.
///</summary>
public static class PdfReaderAssemblies
{
    public static IReadOnlyList<string> Names { get; } = new[]
    {
        "UglyToad.PdfPig.wasm",
        "UglyToad.PdfPig.Core.wasm",
        "UglyToad.PdfPig.Fonts.wasm",
        "UglyToad.PdfPig.Tokenization.wasm",
        "UglyToad.PdfPig.Tokens.wasm",
        "UglyToad.PdfPig.DocumentLayoutAnalysis.wasm",
        "UglyToad.PdfPig.Package.wasm"
    };
}
