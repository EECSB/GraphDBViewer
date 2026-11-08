using System.Text;
using System.Text.Json;

namespace GraphDBViewer.Mcp
{
    ///<summary>What a call is answered: the text the model reads, whether it is a refusal, and a picture when there is one.</summary>
    public sealed record ServedAnswer(string Text, bool IsError, byte[]? Png = null);

    ///<summary>One tool as the server lists it: its name, what it does, the JSON schema of its arguments, and what it touches.</summary>
    public sealed record ServedTool(string Name, string Description, string InputSchema, bool ReadsOnly, bool Destroys);

    ///
    ///What `gdbv-mcp` serves (2026-11-03, the user: "the MCP server also", GDS Viewer's brought across): link_tab and
    ///unlink_tab, and the tools a linked Graph DB Viewer tab answers - Ask AI's own query tools and its picture of the
    ///graph, run in that tab against the database it is connected to, and describe_tab, what the tab has open.
    ///
    ///**Everything is the tab's.** Unlike GDS Viewer's, which works on the files in a folder until a tab is linked, a graph
    ///lives behind the connection a tab makes - with the person's login, in their browser - so there is nothing for the
    ///server to work on alone, and each tool but the link's refuses with how to link one.
    ///
    ///**What the model works on is said, never assumed.** When the tab goes - Stop pressed, the page reloaded - the next
    ///call is refused with why, rather than with a bare "no tab is linked".
    ///
    public sealed class LinkSession
    {
        ///<summary>How long link_tab waits for a tab when none has said hello yet.</summary>
        public static readonly TimeSpan LinkWait = TimeSpan.FromSeconds(20);

        ///<summary>How long a call sent to a tab is waited for: a query over a large graph takes a while.</summary>
        public static readonly TimeSpan CallWithin = TimeSpan.FromMinutes(2);

        private const string NoArguments = """{"type":"object","properties":{}}""";

        private const string CodeArgument = """{"type":"object","properties":{"code":{"type":"string","description":"The eight digits the viewer shows under MCP server in its AI settings, like 4821 7356."}},"required":["code"]}""";

        private static string QueryArgument(string description)
        {
            return $$$"""{"type":"object","properties":{"query":{"type":"string","description":"{{{description}}}"}},"required":["query"]}""";
        }

        ///
        ///The tools, in the order a client lists them. The three Ask AI has are named and described as it names them
        ///(QueryToolRunner and GraphPictureRunner in GraphDBViewer.Core), so a model is told the same thing either way.
        ///
        public static readonly IReadOnlyList<ServedTool> Tools = new List<ServedTool>
        {
            new ServedTool(
                "link_tab",
                "Links these tools to a Graph DB Viewer tab, so they run in it, against the database it is connected to, as the person "
                    + "watches. The person starts it in the viewer, under MCP server in Settings, AI settings, which shows a code - ask "
                    + "them for it. Waits up to 20 seconds for the tab. Linked, describe_tab says what it has open.",
                CodeArgument,
                false,
                false),
            new ServedTool("unlink_tab", "Ends the link to the Graph DB Viewer tab.", NoArguments, false, false),
            new ServedTool(
                "describe_tab",
                "What the linked Graph DB Viewer tab has open: the database it is connected to and its query language, what the "
                    + "graph on its canvas holds, and whether it lets these tools change the database.",
                NoArguments,
                true,
                false),
            new ServedTool(
                "run_read_query",
                "Runs a read-only query, in the language describe_tab names, against the database the linked tab is connected to, "
                    + "and returns the result as JSON. A query that would change the database is refused.",
                QueryArgument("The read-only query to run."),
                true,
                false),
            new ServedTool(
                "run_write_query",
                "Runs a query that changes the database the linked tab is connected to, and returns the result as JSON. This is not "
                    + "undoable. The tab refuses it unless the person allowed changes under MCP server in its AI settings. Make the "
                    + "smallest change that does what was asked, read first with run_read_query when unsure, and say plainly what changed.",
                QueryArgument("The query to run."),
                false,
                true),
            new ServedTool(
                "look_at_graph",
                "A picture of the graph as the linked tab's view draws it now, 2D or 3D.",
                NoArguments,
                true,
                false)
        };

        public LinkSession(TabLink link)
        {
            Link = link;
        }

        public TabLink Link { get; }

        ///<summary>The server's instructions to the model, said once as the client connects.</summary>
        public string Instructions => "These tools work on a graph database through a Graph DB Viewer tab the person has open: link one "
            + "with link_tab and the code its AI settings show under MCP server, then describe_tab for what it has open. Queries run "
            + "in that tab, with its connection, as the person watches.";

        ///<summary>The MCP client the call came from, as link_tab tells the tab it is linked to.</summary>
        private string client = "";

        ///<summary>One call at a time, as the tab answers them.</summary>
        private readonly SemaphoreSlim turn = new SemaphoreSlim(1, 1);

        ///<summary>Runs one call as the protocol sends it. <paramref name="calling"/> is the client it came from.</summary>
        public async Task<ServedAnswer> CallAsync(string name, JsonElement arguments, string calling, CancellationToken cancel)
        {
            await turn.WaitAsync(cancel);

            try
            {
                client = calling;

                return await callAsync(name, arguments, cancel);
            }
            catch (LinkRefusal refusal)
            {
                return refused(refusal.Message);
            }
            finally
            {
                turn.Release();
            }
        }

        private async Task<ServedAnswer> callAsync(string name, JsonElement arguments, CancellationToken cancel)
        {
            if (name == "link_tab")
                return await linkTabAsync(arguments, cancel);
            else if (name == "unlink_tab")
                return await unlinkTabAsync();

            if (!Tools.Any(tool => tool.Name == name))
                return refused($"There is no tool named {name}.");

            if (Link.TakeEnding() is string ended)
            {
                return refused(
                    $"{ended} So {name} was not run. Ask the person to press Start under MCP server in the viewer's AI settings again "
                    + "and give you the new code, then link_tab with it.");
            }

            if (!Link.IsLinked)
            {
                return refused(
                    $"{name} runs in a linked Graph DB Viewer tab. Ask the person to open Settings, AI settings in the viewer, press "
                    + "Start under MCP server, and give you the code it shows; then link_tab with it.");
            }

            var answer = await Link.CallAsync(name, arguments, CallWithin, cancel);

            return new ServedAnswer(answer.Text, answer.IsError, answer.Png);
        }

        private async Task<ServedAnswer> linkTabAsync(JsonElement arguments, CancellationToken cancel)
        {
            string code = "";

            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("code", out var given))
            {
                if (given.ValueKind == JsonValueKind.String)
                    code = given.GetString() ?? "";
                else if (given.ValueKind == JsonValueKind.Number)
                    code = given.GetRawText();
            }

            string graph = await Link.LinkAsync(code, client, LinkWait, cancel);

            var said = new StringBuilder("Linked to the Graph DB Viewer tab");

            if (graph.Length > 0)
                said.Append($" with {graph} open");

            said.Append(". The query tools run in that tab from now on, against its connection, as the person watches.");

            //
            //What the tab has open, so the model starts from where the person is rather than asking. **Its refusal is not the
            //link's**: the tab is linked by now, whatever describe_tab does (GDS Viewer's, 2026-11-03).
            //
            try
            {
                var described = await Link.CallAsync("describe_tab", default, CallWithin, cancel);

                if (!described.IsError)
                    said.Append($"\n\n{described.Text}");
            }
            catch (LinkRefusal refusal)
            {
                said.Append($"\n\ndescribe_tab did not answer yet: {refusal.Message} Call it when you need what the tab has open.");
            }

            return new ServedAnswer(said.ToString(), false);
        }

        private async Task<ServedAnswer> unlinkTabAsync()
        {
            if (!await Link.UnlinkAsync())
                return new ServedAnswer("No Graph DB Viewer tab was linked.", false);

            return new ServedAnswer("The link to the Graph DB Viewer tab is ended.", false);
        }

        private static ServedAnswer refused(string why)
        {
            return new ServedAnswer(why, true);
        }
    }
}
