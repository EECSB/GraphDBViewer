using System.Globalization;
using GraphDBViewer.Mcp;
using ModelContextProtocol.Server;

//
//`gdbv-mcp`: a Graph DB Viewer tab's database, served to an AI model over the Model Context Protocol (2026-11-03, GDS
//Viewer's `gds mcp` brought across). Started by an MCP client - Claude Desktop, Claude Code or another - rather than typed:
//
//    claude mcp add gdbv -- gdbv-mcp
//
//**Standard output is the protocol's**, so nothing here writes to it: one line of prose in that stream is a message the
//client cannot parse. What a person running it by hand needs to know goes to standard error, which clients keep as the
//server's log.
//

int port = TabLink.DefaultPort;

//The pages whose tabs are taken besides this computer's own and the public viewer (TabLink.Origins): a Treeality instance's.
var origins = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--link-port" && i + 1 < args.Length)
    {
        if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
        {
            Console.Error.WriteLine($"--link-port is the port a viewer tab links on, from 1 to 65535, and {args[i + 1]} is not one.");

            return 2;
        }

        i++;
    }
    else if (args[i] == "--link-origin" && i + 1 < args.Length)
    {
        if (TabLink.OriginOf(args[i + 1]) == null)
        {
            Console.Error.WriteLine($"--link-origin is the address of a page whose viewer tabs may link, such as https://treeality.example, and {args[i + 1]} is not one.");

            return 2;
        }

        origins.Add(args[i + 1]);
        i++;
    }
    else if (args[i] == "--help" || args[i] == "-h")
    {
        Console.Error.WriteLine($@"gdbv-mcp [--link-port N] [--link-origin ADDRESS]...: serves a Graph DB Viewer tab's database to an MCP client
over standard input and output. Tabs are taken from pages on this computer and from {TabLink.PublicViewer}; --link-origin
names another page, such as a Treeality instance's address, and may be given more than once.");

        return 0;
    }
    else
    {
        Console.Error.WriteLine($"gdbv-mcp takes --link-port N and --link-origin ADDRESS and nothing else, and was given {args[i]}.");

        return 2;
    }
}

await using var link = new TabLink(port) { Log = line => Console.Error.WriteLine(line), Origins = origins };

var session = new LinkSession(link);

Console.Error.WriteLine($"gdbv-mcp {McpServing.Version}: a Graph DB Viewer tab's database, for an MCP client talking over standard input and output.");

//
//**The link listens from the start**, so a tab already started finds it at once and waits. One gdbv-mcp per Claude window
//and one port: the first one up has it, and the rest say so here and try again when link_tab is called.
//
if (link.TryListen(out string why))
    Console.Error.WriteLine($"A viewer tab can link on port {link.Port}, by the code its AI settings show under MCP server.");
else
    Console.Error.WriteLine($"Not listening for a viewer tab yet. {why}");

await McpServing.RunAsync(session, new StdioServerTransport("gdbv"), CancellationToken.None);

return 0;
