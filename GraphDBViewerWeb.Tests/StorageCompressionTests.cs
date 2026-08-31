using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

//Every store that has somewhere to put a value packs it this way (deflate + base64 above a threshold), so
//the same data takes far less room in IndexedDB and far less bandwidth on the way to the host. The
//round-trip must be lossless for every shape the app persists (short strings, big JSON, unicode, empty),
//and Decode must tolerate the odd unmarked value. Shared rather than copied because FallbackAppStorage
//mirrors values between two adapters — one that re-encoded in the middle would be a bug waiting for a
//reconnect.
public class StorageCompressionTests
{
    [Fact]
    public void ShortValue_RoundTrips_Uncompressed()
    {
        var value = "graphdbviewer:darkMode -> true";

        var encoded = StorageCompression.Encode(value);

        //Short values are stored raw (a marker + the text), not compressed.
        Assert.StartsWith("r", encoded);
        Assert.Equal(value, StorageCompression.Decode(encoded));
    }

    [Fact]
    public void LargeValue_IsCompressed_AndRoundTrips()
    {
        //A realistic heavy blob: a big, repetitive JSON array like a graph result.
        var value = "[" + string.Join(",", Enumerable.Range(0, 4000)
            .Select(i => $"{{\"id\":\"{i}\",\"label\":\"Component\",\"properties\":{{\"name\":\"Node {i}\"}}}}")) + "]";

        var encoded = StorageCompression.Encode(value);

        //It actually shrank, and it survives the round-trip byte-for-byte.
        Assert.StartsWith("z", encoded);
        Assert.True(encoded.Length < value.Length, "compressed form should be smaller than the original");
        Assert.Equal(value, StorageCompression.Decode(encoded));
    }

    [Fact]
    public void Unicode_RoundTrips_ThroughCompression()
    {
        //Long enough to compress, with multi-byte characters the UTF-8 path must preserve.
        var value = string.Concat(Enumerable.Repeat("café — naïve — Zürich — 日本語 — Москва — ", 40));

        var encoded = StorageCompression.Encode(value);

        Assert.StartsWith("z", encoded);
        Assert.Equal(value, StorageCompression.Decode(encoded));
    }

    [Fact]
    public void EmptyString_RoundTrips()
    {
        var encoded = StorageCompression.Encode("");

        Assert.Equal("", StorageCompression.Decode(encoded));
    }

    [Fact]
    public void Null_EncodesToNull_AndDecodesFromNull()
    {
        Assert.Null(StorageCompression.Encode(null));
        Assert.Null(StorageCompression.Decode(null));
    }

    [Fact]
    public void Decode_TreatsAnUnmarkedValueAsRaw()
    {
        //Defensive: a value that never went through Encode (e.g. legacy) comes back untouched.
        var legacy = "[\"a\",\"b\",\"c\"]";

        Assert.Equal(legacy, StorageCompression.Decode(legacy));
    }

    [Fact]
    public void RoundTrips_TypedValueSerializedAsTheAppDoes()
    {
        //Mirrors SetAsync<T>: serialize, encode, decode, deserialize — the shape saved connections use.
        var original = new Dictionary<string, int> { ["Screw"] = 3, ["Gear"] = 3, ["Mount"] = 1 };

        var encoded = StorageCompression.Encode(JsonSerializer.Serialize(original));
        var restored = JsonSerializer.Deserialize<Dictionary<string, int>>(StorageCompression.Decode(encoded));

        Assert.Equal(original, restored);
    }
}
