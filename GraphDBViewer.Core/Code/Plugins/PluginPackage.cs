using System.IO;
using System.IO.Compression;
using System.Linq;

namespace GraphDBViewerWeb.Code;

///<summary>
///A plugin as a zip: the same folder an install-by-URL fetches over the network, handed over as one
///file instead. <strong>The zip is opened in the browser, never on the server</strong> — the bytes
///have to be there anyway to be loaded-to-prove, the manifest rules already live here, and it leaves
///the host with one endpoint that stores bytes under their own hash and knows nothing about plugins.
///
///<strong>Nothing composes a path.</strong> Two entries are read, both by a name the zip itself
///supplied or the manifest named, and every other entry is ignored — so a zip carrying
///<c>../../etc/passwd</c> is a zip carrying an entry nobody asks for.
///</summary>
public static class PluginPackage
{
    ///<summary>The one file that makes a zip a plugin. Exactly one entry may be called this, at any depth.</summary>
    public const string ManifestName = "manifest.json";

    ///<summary>
    ///The most a package may unpack to. A zip is small and its contents are known — a manifest and an
    ///assembly — so this is a bomb guard rather than a budget: the largest bundled perspective is under
    ///half a megabyte, and a plugin an order of magnitude past that is one worth being asked about.
    ///</summary>
    public const long MaxUnpackedBytes = 64L * 1024 * 1024;

    ///<summary>What a zip turned out to hold: the manifest, and the assembly its entry names.</summary>
    public sealed class Opened
    {
        public PluginManifest Manifest { get; init; }

        public byte[] Assembly { get; init; }

        ///<summary>The assembly's name inside the zip, for the sentence that says what was installed.</summary>
        public string AssemblyName { get; init; }
    }

    ///<summary>
    ///Opens a package, or says why it is not one. Every refusal is a sentence an administrator can act
    ///on, because every one of them is somebody standing there having just picked a file.
    ///</summary>
    public static (Opened Package, string Error) Read(byte[] zip)
    {
        if (zip == null || zip.Length == 0)
            return (null, "That file is empty.");

        ZipArchive archive;

        try
        {
            archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            return (null, "That file is not a zip. A plugin package is a zip holding the plugin's manifest.json and the assembly its entry names.");
        }
        catch (Exception ex)
        {
            return (null, $"The zip could not be opened: {ex.Message}");
        }

        using (archive)
        {
            //The manifest is what makes the zip a plugin, and where it sits decides where everything
            //else sits — so a zip wrapping the folder (which is what right-click-compress makes) works
            //without the administrator having to know that it did.
            var manifests = archive.Entries.Where(entry => Named(entry, ManifestName)).ToList();

            if (manifests.Count == 0)
                return (null, $@"There is no {ManifestName} in that zip. A plugin package is the plugin's folder — its manifest and the assembly beside it — zipped up.");

            if (manifests.Count > 1)
                return (null, $@"That zip holds {manifests.Count} files called {ManifestName} ({string.Join(", ", manifests.Take(3).Select(entry => entry.FullName))}). One zip is one plugin, so it may hold one.");

            var manifestEntry = manifests[0];

            PluginManifest manifest;

            try
            {
                manifest = PluginManifest.Parse(TextOf(manifestEntry));
            }
            catch (PluginManifestException ex)
            {
                return (null, ex.Message);
            }
            catch (Exception ex)
            {
                return (null, $"The manifest could not be read out of the zip: {ex.Message}");
            }

            string assemblyFile;

            try
            {
                (assemblyFile, _) = DotNetAssemblyLoader.ParseEntry(manifest.Entry);
            }
            catch (PluginLoadException ex)
            {
                return (null, ex.Message);
            }

            //Beside the manifest, exactly as an install by URL resolves it against the manifest's URL.
            //The folder comes from the zip, not from anything a caller typed.
            var folder = FolderOf(manifestEntry.FullName);
            var wanted = folder + assemblyFile;

            var assemblyEntry = archive.Entries.FirstOrDefault(entry => string.Equals(entry.FullName, wanted, StringComparison.OrdinalIgnoreCase));

            if (assemblyEntry == null)
                return (null, $@"The manifest's entry names ""{assemblyFile}"", and there is no such file beside the manifest in that zip. It holds {Listed(archive)}.");

            if (assemblyEntry.Length > MaxUnpackedBytes)
                return (null, $@"""{assemblyFile}"" unpacks to {Megabytes(assemblyEntry.Length)}, past the {Megabytes(MaxUnpackedBytes)} a plugin assembly may be.");

            byte[] assembly;

            try
            {
                assembly = BytesOf(assemblyEntry);
            }
            catch (Exception ex)
            {
                return (null, $@"""{assemblyFile}"" could not be read out of the zip: {ex.Message}");
            }

            if (assembly.Length == 0)
                return (null, $@"""{assemblyFile}"" is empty in that zip.");

            return (new Opened { Manifest = manifest, Assembly = assembly, AssemblyName = assemblyFile }, null);
        }
    }

    ///<summary>Whether an entry is a file of this name, wherever it sits. Zip separators are always forward slashes.</summary>
    private static bool Named(ZipArchiveEntry entry, string name)
    {
        return string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase);
    }

    ///<summary>The manifest's folder inside the zip, with its trailing slash, or "" at the root.</summary>
    private static string FolderOf(string fullName)
    {
        var cut = fullName.LastIndexOf('/');

        if (cut < 0)
            return "";

        return fullName.Substring(0, cut + 1);
    }

    private static string TextOf(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxUnpackedBytes)
            throw new InvalidDataException($"it unpacks to {Megabytes(entry.Length)}");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    ///<summary>
    ///An entry's bytes, read against the length the zip declares rather than to the end of the stream —
    ///so a zip that lies about a length runs out of its declared budget instead of out of memory.
    ///</summary>
    private static byte[] BytesOf(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();

        var read = new byte[81920];
        long total = 0;

        while (true)
        {
            var got = stream.Read(read, 0, read.Length);

            if (got == 0)
                break;

            total += got;

            if (total > MaxUnpackedBytes)
                throw new InvalidDataException($"it unpacks to more than {Megabytes(MaxUnpackedBytes)}");

            buffer.Write(read, 0, got);
        }

        return buffer.ToArray();
    }

    ///<summary>The first few names in the zip, so "there is no such file" can be answered with "here is what there is".</summary>
    private static string Listed(ZipArchive archive)
    {
        var names = archive.Entries.Select(entry => entry.FullName).Take(6).ToList();

        if (names.Count == 0)
            return "nothing at all";

        return string.Join(", ", names);
    }

    private static string Megabytes(long bytes)
    {
        return $"{bytes / 1024d / 1024d:0.#} MB";
    }
}
