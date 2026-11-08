using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Reads a Wikipedia article for the model (2026-11-02): what the knowledge-graph window's fetch box did, as a tool beside the
///graph tool, so "draw a graph of the article on Marie Curie" reads the article rather than what the model remembers of it.
///A read, so it waits for the person on the rung that asks about everything, and its card shows the article it read.
///</summary>
public sealed class WikipediaRunner : ILlmToolRunner, IWriteCapableToolRunner
{
    public const string ReadName = "read_wikipedia_article";

    private readonly ILlmToolRunner _inner;
    private readonly Func<string, CancellationToken, Task<(string Text, string Error)>> _fetch;

    ///<param name="inner">The other tools, or null where there are none.</param>
    ///<param name="fetch">Reads an article's plain text by its title or link, or says why it could not (<see cref="WikipediaSource.FetchExtractAsync"/>).</param>
    public WikipediaRunner(ILlmToolRunner inner, Func<string, CancellationToken, Task<(string Text, string Error)>> fetch)
    {
        _inner = inner;
        _fetch = fetch;
    }

    ///<summary>The tool, described for the model.</summary>
    public static LlmTool ReadTool
    {
        get
        {
            return new LlmTool
            {
                Name = ReadName,
                Description = "Reads a Wikipedia article's plain text, by its title or a link to it. "
                    + "For a question about the article or a graph drawn from it; a link to another language's Wikipedia reads that one.",
                InputSchemaJson = "{\"type\":\"object\",\"properties\":{\"article\":{\"type\":\"string\",\"description\":\"The article's title, or a link to it.\"}},\"required\":[\"article\"]}"
            };
        }
    }

    public IReadOnlyList<LlmTool> Tools
    {
        get
        {
            var tools = new List<LlmTool>();

            if (_inner != null)
                tools.AddRange(_inner.Tools);

            tools.Add(ReadTool);

            return tools;
        }
    }

    public bool AllowWrites
    {
        get
        {
            return _inner is IWriteCapableToolRunner writable && writable.AllowWrites;
        }
        set
        {
            if (_inner is IWriteCapableToolRunner writable)
                writable.AllowWrites = value;
        }
    }

    public async Task<LlmToolOutput> RunToolAsync(string name, string argumentsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (name == ReadName)
            return await ReadAsync(argumentsJson, ct);

        if (_inner == null)
            return LlmToolOutput.Failure($"There is no tool called {name} here.");

        return await _inner.RunToolAsync(name, argumentsJson, ct);
    }

    //No longer than a text file sent with a question may be, and said where it was cut, so the model does not take the start
    //of an article for the whole of it.
    private async Task<LlmToolOutput> ReadAsync(string argumentsJson, CancellationToken ct)
    {
        var article = ArticleOf(argumentsJson);

        if (string.IsNullOrWhiteSpace(article))
            return LlmToolOutput.Failure("Say which article: its title, or a link to it.");

        var (text, error) = await _fetch(article, ct);

        if (error != null)
            return LlmToolOutput.Failure(error);

        if (text.Length > AiAttachment.MostTextCharacters)
            text = $"{text.Substring(0, AiAttachment.MostTextCharacters)}\n\n[The article goes on: this is its first {AiAttachment.MostTextCharacters:N0} characters.]";

        return LlmToolOutput.Result(text);
    }

    private static string ArticleOf(string argumentsJson)
    {
        try
        {
            using var arguments = JsonDocument.Parse(argumentsJson);

            if (arguments.RootElement.ValueKind == JsonValueKind.Object
                && arguments.RootElement.TryGetProperty("article", out var article)
                && article.ValueKind == JsonValueKind.String)
                return article.GetString();
        }
        catch (JsonException) { }

        return null;
    }
}
