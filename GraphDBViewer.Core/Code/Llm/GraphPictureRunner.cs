using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///The picture tool: the graph as the person's view draws it now, 2D or 3D, for a model that sees pictures (2026-11-02,
///GDS Viewer's look, asked for with Ask AI's files). A picture is the one thing only the browser can make, so the page
///hands in what takes it; this puts it beside the query tools, or on its own where there are none, as with a graph opened
///from a file and no database connected.
///</summary>
public sealed class GraphPictureRunner : ILlmToolRunner, IWriteCapableToolRunner
{
    public const string LookName = "look_at_graph";

    ///<summary>The longest side a picture is drawn at: Anthropic scales anything longer down to it, so more would be sent for nothing.</summary>
    public const int MostPictureSide = 1568;

    private readonly ILlmToolRunner _inner;
    private readonly Func<CancellationToken, Task<LlmToolOutput>> _look;

    ///<param name="inner">The query tools, or null where there are none.</param>
    ///<param name="look">Takes the picture, or says why there is none to take.</param>
    public GraphPictureRunner(ILlmToolRunner inner, Func<CancellationToken, Task<LlmToolOutput>> look)
    {
        _inner = inner;
        _look = look;
    }

    ///<summary>The tool, described for the model. It takes nothing: it is the view as it is, which the person chose.</summary>
    public static LlmTool LookTool
    {
        get
        {
            return new LlmTool
            {
                Name = LookName,
                Description = "A picture of the graph as the person's view draws it now, in 2D or 3D: which vertices and edges are drawn, where, and in which colors and sizes. "
                    + "For seeing the graph's shape, its clusters and what stands out at a glance; for exact counts and properties, run a query.",
                InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}"
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

            tools.Add(LookTool);

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

        if (name == LookName)
            return await _look(ct);

        if (_inner == null)
            return LlmToolOutput.Failure($"There is no tool called {name} here.");

        return await _inner.RunToolAsync(name, argumentsJson, ct);
    }
}
