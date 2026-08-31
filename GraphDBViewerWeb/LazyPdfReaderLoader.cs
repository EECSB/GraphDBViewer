using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.WebAssembly.Services;

namespace GraphDBViewerWeb.Code;

///<summary>
///The WASM half of <see cref="IPdfReaderLoader"/>: fetches the PdfPig assemblies the project file has
///set aside with <c>BlazorWebAssemblyLazyLoad</c>, the first time somebody loads a PDF.
///
///Lives in the host rather than in Core because <see cref="LazyAssemblyLoader"/> is a Blazor WebAssembly
///service; the shared library is also referenced by the server host, which has no such thing.
///</summary>
public sealed class LazyPdfReaderLoader : IPdfReaderLoader
{
    private readonly LazyAssemblyLoader _loader;

    //One shared attempt. Two files dropped in quick succession must not start two downloads of the same
    //four megabytes, and a load that already succeeded must not be repeated at all.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;

    public LazyPdfReaderLoader(LazyAssemblyLoader loader)
    {
        _loader = loader;
    }

    public async Task<string> EnsureLoadedAsync()
    {
        if (_loaded)
            return null;

        await _gate.WaitAsync();

        try
        {
            if (_loaded)
                return null;

            await _loader.LoadAssembliesAsync(PdfReaderAssemblies.Names.ToList());
            _loaded = true;

            return null;
        }
        catch (Exception ex)
        {
            //The reader is fetched on demand, so its download can fail long after the app started —
            //offline, or a connection that dropped. Say that, rather than letting the PDF fail later
            //with a parse error that blames the file.
            return $"Could not download the PDF reader ({ex.Message}). Check your connection and try again, or paste the text instead.";
        }
        finally
        {
            _gate.Release();
        }
    }
}
