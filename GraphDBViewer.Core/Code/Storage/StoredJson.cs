using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///A typed value read back from the string a store kept it as, the one way every <see cref="IAppStorage"/> that keeps
///JSON as a string reads it: <see cref="IndexedDbAppStorage"/>, <see cref="RemoteAppStorage"/> and
///<see cref="FallbackAppStorage"/> each had a copy of this until 2026-11-07.
///</summary>
public static class StoredJson
{
    ///<summary>
    ///The value, or <c>default(T)</c> when nothing is stored. A value that no longer deserializes, written by an older
    ///or other serializer, reads as absent rather than throwing; the app defaults anything missing.
    ///</summary>
    public static T Read<T>(string json)
    {
        if (string.IsNullOrEmpty(json))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }
}
