using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

///<summary>
///Finds a real, filled-in model to call, for the handful of tests that call one.
///
///It reads the same git-ignored dev-secrets.json the app seeds itself from, so there is one place a key
///lives and no second copy to keep in step or leak. The file is served out of the host's wwwroot at
///runtime; here it is found by walking up from the test binary, because a test has no HTTP client to
///fetch itself with.
///</summary>
public static class LiveAi
{
    ///<summary>The first saved model with a key, or null when there is no usable one.</summary>
    public static Task<LlmConnection> FirstConnectionAsync()
    {
        var path = FindSecretsFile();

        if (path == null)
            return Task.FromResult<LlmConnection>(null);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("llmConnections", out var connections))
                return Task.FromResult<LlmConnection>(null);

            foreach (var entry in connections.EnumerateObject())
            {
                var connection = JsonSerializer.Deserialize<LlmConnection>(entry.Value.GetRawText(), Options);

                if (connection == null || string.IsNullOrWhiteSpace(connection.ApiKey))
                    continue;

                if (string.IsNullOrWhiteSpace(connection.Name))
                    connection.Name = entry.Name;

                return Task.FromResult(connection);
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        return Task.FromResult<LlmConnection>(null);
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    //Walks up from the test binary looking for a host's wwwroot copy. Deliberately a search rather than
    //a relative path: the same file lives under a different host in each repo.
    private static string FindSecretsFile()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);

        while (directory != null)
        {
            foreach (var candidate in Candidates(directory))
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static IEnumerable<string> Candidates(DirectoryInfo directory)
    {
        yield return Path.Combine(directory.FullName, DevSecrets.Path);

        foreach (var host in Directory.Exists(directory.FullName) ? Directory.GetDirectories(directory.FullName) : new string[0])
            yield return Path.Combine(host, "wwwroot", DevSecrets.Path);
    }
}
