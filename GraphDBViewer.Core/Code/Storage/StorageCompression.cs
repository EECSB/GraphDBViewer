using System.IO.Compression;
using System.Text;

namespace GraphDBViewerWeb.Code;

///<summary>
///How a stored value is packed, shared by every <see cref="IAppStorage"/> that has somewhere to put it.
///
///A one-character marker plus the payload: the raw string when it is short enough that deflate would cost
///more than it saves, deflate + base64 when it is not. The saved workspace is mostly repetitive JSON — a
///graph result packs to a fraction of its size — which is what keeps a heavy workspace inside the browser's
///quota, and what keeps the same blob a sane thing to put on the wire.
///
///Both adapters use it so a value written by one is readable by the other: <see cref="FallbackAppStorage"/>
///mirrors between them, and a value that had to be re-encoded in the middle would be a bug waiting for a
///reconnect.
///</summary>
public static class StorageCompression
{
    //Values longer than this are stored compressed; shorter ones aren't worth the deflate/base64 overhead.
    public const int CompressThreshold = 256;

    ///<summary>Marks a payload stored verbatim.</summary>
    public const char RawMarker = 'r';

    ///<summary>Marks a payload stored deflated and base64'd.</summary>
    public const char CompressedMarker = 'z';

    ///<summary>Packs a value for storage. Pure, and round-trips with <see cref="Decode"/>.</summary>
    public static string Encode(string value)
    {
        if (value == null)
            return null;

        if (value.Length < CompressThreshold)
            return RawMarker + value;

        var bytes = Encoding.UTF8.GetBytes(value);

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(bytes, 0, bytes.Length);

        return CompressedMarker + Convert.ToBase64String(output.ToArray());
    }

    ///<summary>Inverse of <see cref="Encode"/>. An unmarked string is returned as-is (defensive).</summary>
    public static string Decode(string stored)
    {
        if (string.IsNullOrEmpty(stored))
            return stored;

        var marker = stored[0];
        var payload = stored.Substring(1);

        if (marker == RawMarker)
            return payload;

        if (marker == CompressedMarker)
        {
            var data = Convert.FromBase64String(payload);

            using var input = new MemoryStream(data);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);

            return Encoding.UTF8.GetString(output.ToArray());
        }

        return stored;
    }
}
