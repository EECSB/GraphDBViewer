using System.Text;
using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//The MCP server's calls, answered in this tab (2026-11-03, the user: "the MCP server also", GDS Viewer's brought across):
//the link (LiveLink, on the shell, so AI settings' card shows it) hands each call here while the page is open. The tools
//are Ask AI's own - the query tools against the connection in front, and the picture of the graph - and describe_tab,
//what this tab has open.
public partial class Home
{
    //Takes the link's calls and names this tab's graph to it, from the first render until the page goes.
    private void AnswerTheLink()
    {
        Shell.Link.Answer = AnswerLinkCallAsync;
        Shell.Link.GraphName = LinkGraphName;
    }

    //Lets go of the link's calls - unless another page took them since, which keeps them.
    private void LetGoOfTheLink()
    {
        if (Shell.Link.Answer == AnswerLinkCallAsync)
        {
            Shell.Link.Answer = null;
            Shell.Link.GraphName = null;
        }
    }

    //What the hello says this tab has open: the connection, as Ask AI's conversation names it, or the offline canvas.
    private string LinkGraphName()
    {
        if (AiSubject.Length > 0)
            return AiSubject;
        else
            return "no database (offline)";
    }

    //
    //One call from the link. **Changes are the person's to allow**: run_write_query is refused until the card's box is
    //ticked, since a write to a database cannot be taken back - and then runs on the same tools Ask AI's are, with their
    //guard on reads.
    //
    private async Task<LlmToolOutput> AnswerLinkCallAsync(string name, string argumentsJson, CancellationToken ct)
    {
        LlmToolOutput output;

        if (name == "describe_tab")
            output = LlmToolOutput.Result(DescribeTab());
        else if (name == GraphPictureRunner.LookName)
            output = await LookForTheLinkAsync(ct);
        else if (name == QueryToolRunner.ReadToolName || name == QueryToolRunner.WriteToolName)
            output = await QueryForTheLinkAsync(name, argumentsJson, ct);
        else
            output = LlmToolOutput.Failure($"This tab does not answer {name}.");

        await InvokeAsync(StateHasChanged);

        return output;
    }

    private async Task<LlmToolOutput> LookForTheLinkAsync(CancellationToken ct)
    {
        var look = NlLook;

        if (look == null)
            return LlmToolOutput.Failure("No graph is drawn in the 2D or 3D view of this tab, so there is nothing to look at.");

        return await look(ct);
    }

    private async Task<LlmToolOutput> QueryForTheLinkAsync(string name, string argumentsJson, CancellationToken ct)
    {
        if (NlToolRunner is not QueryToolRunner tools)
            return LlmToolOutput.Failure("This tab is not connected to a database that runs queries. Ask the person to connect it first.");

        if (name == QueryToolRunner.WriteToolName)
        {
            if (!Shell.Link.AllowChanges)
            {
                return LlmToolOutput.Failure(
                    "Changes are not allowed over this link: the person has not ticked \"Let it change the database\" under MCP server "
                    + "in the viewer's AI settings. Ask them to, if a change is what they want.");
            }

            tools.AllowWrites = true;
        }

        return await tools.RunToolAsync(name, argumentsJson, ct);
    }

    //What this tab has open, for describe_tab: the database and its language, what the canvas holds, and what the link may do.
    private string DescribeTab()
    {
        var said = new StringBuilder();

        if (isConnected)
            said.AppendLine($"Connected to {AiSubject}, a {Provider.DisplayName} database, which runs {NlQueryPrompt.LanguageDisplayName(Provider.QueryLanguage)} queries.");
        else
            said.AppendLine("Not connected to a database: the canvas is drawn offline, so the query tools cannot run here.");

        var data = CurrentGraphData();

        if (data.ValueKind == JsonValueKind.Undefined)
            said.AppendLine("Nothing is drawn on the canvas.");
        else
        {
            var table = GraphDataConverter.ToTable(data);
            var labels = table.Nodes.GroupBy(node => node.Label).OrderByDescending(group => group.Count()).Take(12).Select(group => $"{group.Key} ({group.Count()})");

            said.AppendLine($"The canvas shows {table.Nodes.Count} vertices and {table.Edges.Count} edges. Vertex labels: {string.Join(", ", labels)}.");
        }

        if (Shell.Link.AllowChanges)
            said.Append("The person allows changes over this link: run_write_query runs.");
        else
            said.Append("The person allows reads only over this link: run_write_query is refused.");

        return said.ToString();
    }
}
