using Microsoft.AspNetCore.Components.Forms;

namespace GraphDBViewerWeb.Code;

///<summary>
///A source of assets that also takes them (2026-09-30): what a form's Upload and the picker's call, when the
///IAssetSource registered is one of these. Apart from IAssetSource so a source that only lends its files stays
///one, and the web-only edition, which registers no source at all, is untouched.
///</summary>
public interface IAssetUploader
{
    ///<summary>The permission an account needs to upload, which a field checks before it offers Upload.</summary>
    string UploadPermission { get; }

    ///<summary>One file into a folder, the top level when it is empty: the asset it became, or why not.</summary>
    Task<AssetUpload> UploadAsync(string name, string mediaType, Stream content, long length, string folder, CancellationToken cancellationToken);
}

///<summary>What an upload came to: the asset, or the sentence saying why there is none.</summary>
public sealed class AssetUpload
{
    public AssetInfo Item { get; set; }

    public string Error { get; set; }
}

///<summary>What a picker shows for a field: images for a photo, an image or a logo, and anything for the rest.</summary>
public static class AssetKinds
{
    public const string Image = "image";
    public const string Any = "any";

    private static readonly string[] Pictures = ["photo", "image", "logo", "picture", "icon", "thumbnail"];

    public static string ForField(string name)
    {
        if (Pictures.Contains((name ?? "").Trim(), StringComparer.OrdinalIgnoreCase))
            return Image;

        return Any;
    }
}

///<summary>A file chosen in a form, sent to the source that keeps files (2026-09-30).</summary>
public static class AssetUploads
{
    ///<summary>
    ///The largest file a form sends. A form reads the file into the page before it sends it, so this is kept to
    ///what a photo or a document is; bigger files go through the source's own page, which streams them.
    ///</summary>
    public const long MostFromAForm = 25L * 1024 * 1024;

    ///<summary>Whether a field offers Upload: a source that takes files, and an account that may give it one.</summary>
    public static bool MayUpload(IAssetSource source, AccountInfo account)
    {
        if (source is not IAssetUploader uploader || account == null)
            return false;

        return account.Can(uploader.UploadPermission);
    }

    public static async Task<AssetUpload> UploadAsync(IAssetSource source, IBrowserFile file, string folder, CancellationToken cancellationToken)
    {
        if (source is not IAssetUploader uploader)
            return new AssetUpload { Error = "There is nowhere here to upload a file to." };

        if (file.Size > MostFromAForm)
            return new AssetUpload { Error = $"{file.Name} is {ByteSizes.Human(file.Size)}, more than a form sends ({ByteSizes.Human(MostFromAForm)}). Upload it on the {source.Name}'s own page, and choose it here." };

        try
        {
            await using var stream = file.OpenReadStream(MostFromAForm, cancellationToken);

            return await uploader.UploadAsync(file.Name, file.ContentType, stream, file.Size, folder ?? "", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AssetUpload { Error = $"{file.Name} was not sent: {ex.Message}" };
        }
    }

    ///<summary>
    ///A file chosen in a form sent, and what was kept handed to <paramref name="sent"/>: why it was not sent, or null.
    ///The file field, the file list and the picker each wrote this branch themselves until 2026-11-07.
    ///</summary>
    public static async Task<string> SendAsync(IAssetSource source, IBrowserFile file, string folder, Func<AssetInfo, Task> sent)
    {
        var upload = await UploadAsync(source, file, folder, CancellationToken.None);

        if (upload.Error != null)
            return upload.Error;

        await sent(upload.Item);

        return null;
    }
}
