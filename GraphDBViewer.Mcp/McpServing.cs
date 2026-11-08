using System.Buffers;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GraphDBViewer.Mcp
{
    ///
    ///Puts a <see cref="LinkSession"/> on the Model Context Protocol: its tools listed, its calls answered. GDS Viewer's
    ///McpServing (2026-11-03), with the low-level handlers rather than the SDK's attributed methods, since the tools are
    ///described once, in the session.
    ///
    public static class McpServing
    {
        public const string Version = "1.0.0";

        ///<summary>The server's name, version and instructions, and the two handlers that are all it answers.</summary>
        public static McpServerOptions Options(LinkSession session)
        {
            var tools = new List<Tool>();

            foreach (var tool in LinkSession.Tools)
                tools.Add(toProtocol(tool));

            return new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "gdbv", Title = "gdbv - a Graph DB Viewer tab's database", Version = Version },
                ServerInstructions = session.Instructions,
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = (request, cancel) => ValueTask.FromResult(new ListToolsResult { Tools = tools }),
                    CallToolHandler = async (request, cancel) => await callAsync(session, request.Params, clientOf(request.Server), cancel)
                }
            };
        }

        ///<summary>Serves the session on a transport until the client closes it or <paramref name="cancel"/> fires.</summary>
        public static async Task RunAsync(LinkSession session, ITransport transport, CancellationToken cancel)
        {
            await using var server = McpServer.Create(transport, Options(session));

            await server.RunAsync(cancel);
        }

        ///<summary>One tool as the protocol describes it, with what it touches said in the annotations a client reads to decide what to ask.</summary>
        private static Tool toProtocol(ServedTool tool)
        {
            using var schema = JsonDocument.Parse(tool.InputSchema);

            return new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = schema.RootElement.Clone(),
                Annotations = new ToolAnnotations
                {
                    ReadOnlyHint = tool.ReadsOnly,
                    DestructiveHint = tool.Destroys,
                    OpenWorldHint = false
                }
            };
        }

        ///
        ///Answers one call. Anything that goes wrong is said as an answer rather than a protocol error, so the model reads it
        ///and can try another way instead of the client reporting a broken server.
        ///
        private static async Task<CallToolResult> callAsync(LinkSession session, CallToolRequestParams? asked, string client, CancellationToken cancel)
        {
            if (asked is null)
                return answer("A tool call has to say which tool.", true);

            try
            {
                var result = await session.CallAsync(asked.Name, argumentsOf(asked.Arguments), client, cancel);

                return answer(result.Text, result.IsError, result.Png);
            }
            catch (Exception failed)
            {
                return answer($"{asked.Name} failed: {failed.Message}", true);
            }
        }

        ///<summary>The text, and the picture after it when the tab drew one - which the model is shown, not told about.</summary>
        private static CallToolResult answer(string text, bool isError, byte[]? png = null)
        {
            var content = new List<ContentBlock> { new TextContentBlock { Text = text } };

            if (png is not null)
                content.Add(ImageContentBlock.FromBytes(png, "image/png"));

            return new CallToolResult
            {
                Content = content,
                IsError = isError
            };
        }

        ///<summary>The client a call came from, as the tab it links is told: its title, or its name, as it introduced itself.</summary>
        private static string clientOf(McpServer server)
        {
            var info = server.ClientInfo;

            if (info?.Title is string title && title.Length > 0)
                return title;

            if (info?.Name is string name && name.Length > 0)
                return name;

            return "an MCP client";
        }

        ///<summary>The arguments as the one JSON object the session reads - or nothing, when the call sent none.</summary>
        private static JsonElement argumentsOf(IDictionary<string, JsonElement>? given)
        {
            if (given is null)
                return default;

            var buffer = new ArrayBufferWriter<byte>();

            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();

                foreach (var (name, value) in given)
                {
                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(buffer.WrittenMemory);

            return document.RootElement.Clone();
        }
    }
}
